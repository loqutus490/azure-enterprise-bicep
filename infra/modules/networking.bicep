param name string
param location string

resource vnet 'Microsoft.Network/virtualNetworks@2023-05-01' = {
  name: 'vnet-${name}'
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.0.0.0/16'
      ]
    }
    subnets: [
      {
        name: 'snet-app'
        properties: {
          addressPrefix: '10.0.1.0/24'
          networkSecurityGroup: {
            id: appNsg.id
          }
          delegations: [
            {
              name: 'delegation-app'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
        }
      }
      {
        name: 'snet-private-endpoints'
        properties: {
          addressPrefix: '10.0.2.0/24'
          networkSecurityGroup: {
            id: peNsg.id
          }
        }
      }
      {
        name: 'snet-bot'
        properties: {
          addressPrefix: '10.0.3.0/24'
          networkSecurityGroup: {
            id: botNsg.id
          }
        }
      }
    ]
  }
}

resource appNsg 'Microsoft.Network/networkSecurityGroups@2023-05-01' = {
  name: 'nsg-app-${name}'
  location: location
  properties: {
    securityRules: [
      {
        name: 'AllowHTTPS'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '443'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
        }
      }
      {
        name: 'DenyAllInbound'
        properties: {
          priority: 4096
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
        }
      }
    ]
  }
}

resource peNsg 'Microsoft.Network/networkSecurityGroups@2023-05-01' = {
  name: 'nsg-pe-${name}'
  location: location
  properties: {
    securityRules: [
      {
        name: 'DenyAllInbound'
        properties: {
          priority: 4096
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
        }
      }
    ]
  }
}

resource botNsg 'Microsoft.Network/networkSecurityGroups@2023-05-01' = {
  name: 'nsg-bot-${name}'
  location: location
  properties: {
    securityRules: [
      {
        name: 'AllowHTTPS'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourcePortRange: '*'
          destinationPortRange: '443'
          sourceAddressPrefix: 'AzureBotService'
          destinationAddressPrefix: '*'
        }
      }
      {
        name: 'DenyAllInbound'
        properties: {
          priority: 4096
          direction: 'Inbound'
          access: 'Deny'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: '*'
        }
      }
    ]
  }
}

// Private DNS zones for private endpoint resolution
resource searchDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.search.windows.net'
  location: 'global'
}

resource openaiDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.openai.azure.com'
  location: 'global'
}

resource kvDnsZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: 'privatelink.vaultcore.azure.net'
  location: 'global'
}

resource searchDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: searchDnsZone
  name: 'link-search'
  location: 'global'
  properties: {
    virtualNetwork: {
      id: vnet.id
    }
    registrationEnabled: false
  }
}

resource openaiDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: openaiDnsZone
  name: 'link-openai'
  location: 'global'
  properties: {
    virtualNetwork: {
      id: vnet.id
    }
    registrationEnabled: false
  }
}

resource kvDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  parent: kvDnsZone
  name: 'link-kv'
  location: 'global'
  properties: {
    virtualNetwork: {
      id: vnet.id
    }
    registrationEnabled: false
  }
}

output vnetId string = vnet.id
output appSubnetId string = vnet.properties.subnets[0].id
output peSubnetId string = vnet.properties.subnets[1].id
output botSubnetId string = vnet.properties.subnets[2].id
output searchDnsZoneId string = searchDnsZone.id
output openaiDnsZoneId string = openaiDnsZone.id
output kvDnsZoneId string = kvDnsZone.id
