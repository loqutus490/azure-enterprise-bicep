// 1. Parameters - These allow you to change names without touching the code
param location string = resourceGroup().location
param appName string = 'my-unique-webapp-${uniqueString(resourceGroup().id)}'
param sku string = 'F1' // Free Tier (change to 'B1' for production)

// 2. The Hosting Plan (The "Hardware")
resource appServicePlan 'Microsoft.Web/serverfarms@2022-03-01' = {
  name: '${appName}-plan'
  location: location
  kind: 'linux'
  sku: {
    name: sku
  }
  properties: {
    reserved: true // Required for Linux
  }
}

// 3. The Web App (The "Container" for your .NET code)
resource webApplication 'Microsoft.Web/sites@2022-03-01' = {
  name: appName
  location: location
  properties: {
    serverFarmId: appServicePlan.id
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0' // Tells Azure to run .NET 8
    }
  }
}

// 4. Output the name so your deploy.sh knows where to send the code
output webAppName string = webApplication.name
