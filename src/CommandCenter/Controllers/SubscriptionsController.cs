// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace CommandCenter.Controllers
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using CommandCenter.DimensionUsageStore;
    using CommandCenter.Models;
    using CommandCenter.OperationsStore;

    using Microsoft.AspNetCore.Authentication.OpenIdConnect;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Options;
    using Microsoft.Marketplace.Metering;
    using Microsoft.Marketplace.Metering.Models;
    using Microsoft.Marketplace.SaaS;
    using Microsoft.Marketplace.SaaS.Models;

    /// <summary>
    /// Subscriptions panel.
    /// </summary>
    [Authorize("CommandCenterAdmin")]
    [Authorize(AuthenticationSchemes = OpenIdConnectDefaults.AuthenticationScheme)]
    public class SubscriptionsController : Controller
    {
        private readonly IMarketplaceSaaSClient marketplaceClient;

        private readonly IMarketplaceMeteringClient meteringClient;

        private readonly IOperationsStore operationsStore;

        private readonly IDimensionUsageStore dimensionUsageStore;

        private readonly CommandCenterOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="SubscriptionsController"/> class.
        /// </summary>
        /// <param name="marketplaceClient">Marketplace API client.</param>
        /// <param name="meteringClient">Metering API client.</param>
        /// <param name="operationsStore">Operations store.</param>
        /// <param name="dimensionUsageStore">DimensionUsage store.</param>
        /// <param name="options">Solution options.</param>
        public SubscriptionsController(
            IMarketplaceSaaSClient marketplaceClient,
            IMarketplaceMeteringClient meteringClient,
            IOperationsStore operationsStore,
            IDimensionUsageStore dimensionUsageStore,
            IOptionsMonitor<CommandCenterOptions> options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            this.marketplaceClient = marketplaceClient;
            this.meteringClient = meteringClient;
            this.operationsStore = operationsStore;
            this.dimensionUsageStore = dimensionUsageStore;
            this.options = options.CurrentValue;
        }

        /// <summary>
        /// Error page.
        /// </summary>
        /// <returns>Action result.</returns>
        [AllowAnonymous]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return this.View(
                new ErrorViewModel
                {
                    RequestId = Activity.Current?.Id ?? this.HttpContext.TraceIdentifier,
                });
        }

        /// <summary>
        /// Subscriptions dashboard home.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Action result.</returns>
        public async Task<IActionResult> Index(CancellationToken cancellationToken)
        {
            var subscriptions = await marketplaceClient.Fulfillment.ListSubscriptionsAsync(cancellationToken: cancellationToken).ToListAsync();

            var subscriptionsViewModel = subscriptions.Select(SubscriptionViewModel.FromSubscription)
                .Where(s => s.State != SubscriptionStatusEnum.Unsubscribed || this.options.ShowUnsubscribed);

            var newViewModel = new List<SubscriptionViewModel>();

            var taskList = new List<Task<SubscriptionViewModel>>();

            foreach (var subscription in subscriptionsViewModel)
            {
                taskList.Add(this.GetSubscriptionDetails(subscription, cancellationToken));
            }

            foreach (var task in taskList)
            {
                var subscription = await task.ConfigureAwait(false);
                newViewModel.Add(subscription);
            }

            return this.View(newViewModel.OrderByDescending(s => s.SubscriptionName));
        }

        /// <summary>
        /// Not authorized.
        /// </summary>
        /// <returns>Action result.</returns>
        [AllowAnonymous]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult NotAuthorized()
        {
            return this.View();
        }

        /// <summary>
        /// Return subscription operations.
        /// </summary>
        /// <param name="subscriptionId">Subscription.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Action result.</returns>
        public async Task<IActionResult> Operations(Guid subscriptionId, CancellationToken cancellationToken)
        {
            var subscriptionOperations =
                await this.operationsStore.GetAllSubscriptionRecordsAsync(subscriptionId, cancellationToken).ConfigureAwait(false);

            var subscription =
                (await this.marketplaceClient.Fulfillment.GetSubscriptionAsync(subscriptionId, null, null, cancellationToken).ConfigureAwait(false)).Value;

            var operations = new List<Operation>();

            foreach (var operation in subscriptionOperations)
            {
                operations.Add(
                    await this.marketplaceClient.Operations.GetOperationStatusAsync(
                        subscriptionId,
                        operation.OperationId,
                        null,
                        null,
                        cancellationToken).ConfigureAwait(false));
            }

            return this.View(new OperationsViewModel
            {
                SubscriptionName = subscription.Name,
                Operations = operations,
            });
        }

        /// <summary>
        /// Return subscription dimensions usage events.
        /// </summary>
        /// <param name="subscriptionId">Subscription.</param>
        /// <param name="showErrorMessage">showErrorMessage.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Action result.</returns>
        [HttpGet]
        public async Task<IActionResult> SubscriptionDimensionUsage(Guid subscriptionId, bool showErrorMessage, CancellationToken cancellationToken)
        {
            var subscription =
                (await this.marketplaceClient.Fulfillment.GetSubscriptionAsync(subscriptionId, null, null, cancellationToken).ConfigureAwait(false)).Value;

            var dimensionEventViewModel = new DimensionEventViewModel()
            {
                SubscriptionId = subscriptionId,
                OfferId = subscription.OfferId,
                PlanId = subscription.PlanId,
                EventTime = DateTime.Now,
                SubscriptionDimensions = this.options.Dimensions
                                                .Where(dim => dim.PlanIds.Contains(subscription.PlanId) && dim.OfferIds.Contains(subscription.OfferId))
                                                .Select(dim => dim.DimensionId).ToList(),
                PastUsageEvents = await this.dimensionUsageStore.GetAllDimensionRecordsAsync(subscriptionId, cancellationToken).ConfigureAwait(false),
            };

            if (showErrorMessage)
            {
                this.ViewBag.UpdateError = "Unable to sent dimension usage, please see logs!";
            }

            return this.View(dimensionEventViewModel);
        }

        /// <summary>
        /// Send Meter Usage on a subscription.
        /// </summary>
        /// <param name="model">DimensionEventViewModel.</param>
        /// <param name="cancellationToken">Cancellationtoken.</param>
        /// <returns>A <see cref="Task{TResult}"/> representing the result of the asynchronous operation.</returns>
        [HttpPost]
        public async Task<IActionResult> SubscriptionDimensionUsage(DimensionEventViewModel model, CancellationToken cancellationToken)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            var usage = new UsageEvent()
            {
                ResourceId = model.SubscriptionId,
                PlanId = model.PlanId,
                Dimension = model.SelectedDimension,
                Quantity = model.Quantity,
                EffectiveStartTime = model.EventTime,
            };

            var updateResult = (await this.meteringClient.Metering.PostUsageEventAsync(usage, null, null, cancellationToken).ConfigureAwait(false)).Value;
            DimensionUsageRecord dimRecord = new DimensionUsageRecord(usage.ResourceId?.ToString(), DateTime.Now.ToString("o"));

            bool errorMessage = true;

            if (updateResult != null)
            {
                dimRecord.UsageEventId = updateResult.UsageEventId;
                dimRecord.Status = updateResult.Status.ToString();
                dimRecord.Quantity = updateResult.Quantity;
                dimRecord.Dimension = updateResult.Dimension;
                dimRecord.EffectiveStartTime = updateResult.MessageTime;
                dimRecord.PlanId = updateResult.PlanId;
                errorMessage = false;
            }
            else
            {
                dimRecord.Status = UsageEventStatusEnum.BadArgument.ToString();
                dimRecord.Quantity = usage.Quantity;
                dimRecord.Dimension = usage.Dimension;
                dimRecord.EffectiveStartTime = usage.EffectiveStartTime;
                dimRecord.PlanId = usage.PlanId;
            }

            await this.dimensionUsageStore.RecordAsync(model.SubscriptionId, dimRecord, cancellationToken).ConfigureAwait(false);

            return this.RedirectToAction("SubscriptionDimensionUsage", new { model.SubscriptionId, errorMessage });
        }

        /// <summary>
        /// Actions on a subscription.
        /// </summary>
        /// <param name="subscriptionId">Subscription ID.</param>
        /// <param name="subscriptionAction">Action to take.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A <see cref="Task{TResult}"/> representing the result of the asynchronous operation.</returns>
        public async Task<IActionResult> SubscriptionAction(
            Guid subscriptionId,
            ActionsEnum subscriptionAction,
            CancellationToken cancellationToken)
        {
            switch (subscriptionAction)
            {
                case ActionsEnum.Activate:

                    var subscription1 = (await this.marketplaceClient.Fulfillment.GetSubscriptionAsync(
                        subscriptionId,
                        null,
                        null,
                        cancellationToken).ConfigureAwait(false)).Value;

                    var subscriptionPlan = new SubscriberPlan()
                    {
                        PlanId = subscription1.PlanId,
                        Quantity = 1,
                    };

                    await this.marketplaceClient.Fulfillment.ActivateSubscriptionAsync(
                       subscriptionId,
                       subscriptionPlan,
                       null,
                       null,
                       cancellationToken).ConfigureAwait(false);
                    break;
                case ActionsEnum.Update:
                    var availablePlans = (await this.marketplaceClient.Fulfillment.ListAvailablePlansAsync(
                        subscriptionId,
                        null,
                        null,
                        cancellationToken).ConfigureAwait(false)).Value;

                    var subscription = (await this.marketplaceClient.Fulfillment.GetSubscriptionAsync(
                        subscriptionId,
                        null,
                        null,
                        cancellationToken).ConfigureAwait(false)).Value;

                    var pendingOperations = (await this.marketplaceClient.Operations.ListOperationsAsync(
                        subscriptionId,
                        null,
                        null,
                        cancellationToken).ConfigureAwait(false)).Value;

                    var updateSubscriptionViewModel = new UpdateSubscriptionViewModel
                    {
                        SubscriptionId = subscriptionId,
                        SubscriptionName = subscription.Name,
                        CurrentPlan = subscription.PlanId,
                        AvailablePlans = availablePlans.Plans.ToList(),
                        PendingOperations = pendingOperations.Operations.Any(
                            o => o.Status == OperationStatusEnum.InProgress),
                    };

                    return this.View("UpdateSubscription", updateSubscriptionViewModel);

                case ActionsEnum.Ack:
                    break;

                case ActionsEnum.Unsubscribe:
                    await this.marketplaceClient.Fulfillment.DeleteSubscriptionAsync(
                        subscriptionId,
                        null,
                        null,
                        cancellationToken).ConfigureAwait(false);

                    return this.RedirectToAction("Index");

                default:
                    throw new ArgumentOutOfRangeException(nameof(subscriptionAction), subscriptionAction, null);
            }

            return this.View();
        }

        /// <summary>
        /// Update subscription action.
        /// </summary>
        /// <param name="model">Subscription details.</param>
        /// <param name="cancellationToken">Cancellationtoken.</param>
        /// <returns>A <see cref="Task{TResult}"/> representing the result of the asynchronous operation.</returns>
        [HttpPost]
        public async Task<IActionResult> UpdateSubscription(
            UpdateSubscriptionViewModel model,
            CancellationToken cancellationToken)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            var pendingOperations = await this.marketplaceClient.Operations.ListOperationsAsync(
                model.SubscriptionId,
                null,
                null,
                cancellationToken).ConfigureAwait(false);

            if (pendingOperations.Value.Operations.Any(o => o.Status == OperationStatusEnum.InProgress))
            {
                return this.RedirectToAction("Index");
            }

            var updateResult = await this.marketplaceClient.Fulfillment.UpdateSubscriptionAsync(
                model.SubscriptionId,
                new SubscriberPlan { PlanId = model.NewPlan },
                null,
                null,
                cancellationToken).ConfigureAwait(false);

            await this.operationsStore.RecordAsync(model.SubscriptionId, Guid.Parse(updateResult), cancellationToken).ConfigureAwait(false);

            return this.RedirectToAction("Index");
        }

        private async Task<SubscriptionViewModel> GetSubscriptionDetails(SubscriptionViewModel subscription, CancellationToken cancellationToken)
        {
            var recordedSubscriptionOperations =
                    await this.operationsStore.GetAllSubscriptionRecordsAsync(
                        subscription.SubscriptionId,
                        cancellationToken).ConfigureAwait(false);

            subscription.ExistingOperations = (await this.operationsStore.GetAllSubscriptionRecordsAsync(
                subscription.SubscriptionId,
                cancellationToken).ConfigureAwait(false)).Any();
            subscription.OperationCount = recordedSubscriptionOperations.Count();

            if (this.options.EnableDimensionMeterReporting)
            {
                subscription.IsDimensionEnabled = this.options.Dimensions
                                                        .Any(dim => dim.PlanIds.Contains(subscription.PlanId) && dim.OfferIds.Contains(subscription.OfferId));
            }

            return subscription;
        }
    }
}
