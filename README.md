# Allagan-Meme-Bot

It is a Discord bot that stores or retrieves text based on commands like '!meme'.  

The architecture is a containerized .Net Core worker application.  In my implementation, I host the Container to Azure Container Services.  The back end is an Azure SQL Database.

Credits to Niels Swimberghe for the very good .Net Core / Container / Azure 'how-to' on:
https://swimburger.net/blog/azure/how-to-create-a-discord-bot-using-the-dotnet-worker-template-and-host-it-on-azure-container-instances

## Container Deployment Notes

Login to the ACR and build the image locally then push it up using two commands:
```
az acr login --name <ACR NAME>
az acr build -r <ACR NAME> -t <CONTAINER NAME>:latest .
```

An instance of the uploaded image was deployed using the following PowerShell: 
```
# Grant access to the ACR by giving a Service Principal the rights to pull
$ACR_REGISTRY_ID = (az acr show --name <ACR NAME> --query id --output tsv )
$SP_PASSWD = (az ad sp create-for-rbac --name acr-service-principal --scopes $ACR_REGISTRY_ID --role acrpull --query password --output tsv) 
$SP_APP_ID = (az ad sp list --display-name acr-service-principal --query [0].appId -o tsv)
# Set important environment variables
$TOKEN = <TOKEN FOR MY DISCORD BOT>
$CONN_STRING = <CONNECTION STRING FOR MY AZURE SQL DATABASE> 
# Create the container instance
az container create --resource-group <RESOURCE GROUP> `
                    --name discord-bot-container `
                    --image <ACR URL>/<IMAGE NAME>:latest `
                    --registry-username $SP_APP_ID `
                    --registry-password $SP_PASSWD `
                    --secure-environment-variables DiscordBotToken="$TOKEN" BackendConnectionString="$CONN_STRING" `
                    --location <AZURE LOCATION>
```

Once spun up, updating the container in Azure with new code needs only two commands:
```
az acr build -r <ACR NAME> -t <CONTAINER NAME>:latest .
az container restart --name <CONTAINER NAME> --resource-group <RESOURCE GROUP>
```

