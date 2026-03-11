param name string
param location string
param enablePrivateEndpoint bool = false
param privateEndpointSubnetId string = ''
param privateDnsZoneId string = ''

resource storage 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: name
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    publicNetworkAccess: enablePrivateEndpoint ? 'Disabled' : 'Enabled'
    networkAcls: enablePrivateEndpoint ? {
      bypass: 'AzureServices'
      defaultAction: 'Deny'
    } : null
  }
}

resource blobPrivateEndpoint 'Microsoft.Network/privateEndpoints@2023-05-01' = if (enablePrivateEndpoint) {
  name: 'pe-${name}-blob'
  location: location
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'plsc-${name}-blob'
        properties: {
          privateLinkServiceId: storage.id
          groupIds: [
            'blob'
          ]
        }
      }
    ]
  }
}

resource blobDnsGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-05-01' = if (enablePrivateEndpoint) {
  parent: blobPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'config'
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}

output storageName string = storage.name
output storageId string = storage.id
