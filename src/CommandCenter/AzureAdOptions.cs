namespace CommandCenter
{
    public class AzureAdOptions
    {
        public string Instance { get; set; }

        public string Domain { get; set; }

        public string TenantId { get; set; }

        public string ClientId { get; set; }

        public string CallbackPath { get; set; }

        public string SignedOutCallbackPath { get; set; }
    }
}
