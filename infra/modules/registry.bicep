param name string
param location string
param tags object
param principalId string
param privateMode bool
param endpointSubnetId string
param privateDnsZoneId string

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: privateMode ? 'Premium' : 'Basic'
  }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: privateMode ? 'Disabled' : 'Enabled'
    networkRuleBypassOptions: 'AzureServices'
  }
}
var acrPullRole = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '7f951dda-4ed3-4680-a7ca-43fe172d538d'
)
resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, tags[?'she-owner'] ?? principalId, acrPullRole)
  scope: registry
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: acrPullRole
  }
}
resource endpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = if (privateMode) {
  name: '${name}-endpoint'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: endpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${name}-registry'
        properties: {
          privateLinkServiceId: registry.id
          groupIds: ['registry']
        }
      }
    ]
  }
}
resource dns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = if (privateMode) {
  parent: endpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'registry'
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}
output loginServer string = registry.properties.loginServer
