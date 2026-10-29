param namePrefix string
param location string
param tags object
param privateMode bool
param vnetAddressPrefix string
param aciSubnetPrefix string
param endpointSubnetPrefix string

resource egress 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
  name: '${namePrefix}-egress'
  location: location
  tags: tags
  sku: {
    name: 'Standard'
  }
  properties: {
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
  }
}
resource nat 'Microsoft.Network/natGateways@2024-05-01' = {
  name: '${namePrefix}-nat'
  location: location
  tags: tags
  sku: {
    name: 'Standard'
  }
  properties: {
    idleTimeoutInMinutes: 10
    publicIpAddresses: [
      {
        id: egress.id
      }
    ]
  }
}
resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${namePrefix}-vnet'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [vnetAddressPrefix]
    }
    subnets: concat(
      [
        {
          name: 'aci'
          properties: {
            addressPrefix: aciSubnetPrefix
            natGateway: {
              id: nat.id
            }
            delegations: [
              {
                name: 'aci'
                properties: {
                  serviceName: 'Microsoft.ContainerInstance/containerGroups'
                }
              }
            ]
          }
        }
      ],
      privateMode
        ? [
            {
              name: 'endpoints'
              properties: {
                addressPrefix: endpointSubnetPrefix
                privateEndpointNetworkPolicies: 'Disabled'
              }
            }
          ]
        : []
    )
  }
}
resource registryZone 'Microsoft.Network/privateDnsZones@2020-06-01' = if (privateMode) {
  name: 'privatelink.azurecr.io'
  location: 'global'
  tags: tags
}
resource vaultZone 'Microsoft.Network/privateDnsZones@2020-06-01' = if (privateMode) {
  name: 'privatelink.vaultcore.azure.net'
  location: 'global'
  tags: tags
}
resource registryLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = if (privateMode) {
  parent: registryZone
  name: '${namePrefix}-vnet'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}
resource vaultLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = if (privateMode) {
  parent: vaultZone
  name: '${namePrefix}-vnet'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet.id
    }
  }
}
output aciSubnetId string = '${vnet.id}/subnets/aci'
output endpointSubnetId string = privateMode ? '${vnet.id}/subnets/endpoints' : ''
output registryDnsZoneId string = privateMode ? registryZone!.id : ''
output vaultDnsZoneId string = privateMode ? vaultZone!.id : ''
output egressIp string = egress.properties.ipAddress
