VERSION=2.3
docker build -t pltdevreg.azurecr.io/testimages/commandcenter:$VERSION .
docker push pltdevreg.azurecr.io/testimages/commandcenter:$VERSION