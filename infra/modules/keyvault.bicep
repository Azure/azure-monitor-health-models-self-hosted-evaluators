param name string
param location string
param tags object
param principalId string
param adminPrincipalIds string[]
param privateMode bool
@allowed([
  'Enabled'
  'Disabled'
])
param publicNetworkAccess string
param endpointSubnetId string
param privateDnsZoneId string

var effectivePublicNetworkAccess = privateMode ? 'Disabled' : publicNetworkAccess

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 90
    publicNetworkAccess: effectivePublicNetworkAccess
    networkAcls: {
      bypass: 'None'
      defaultAction: effectivePublicNetworkAccess == 'Disabled' ? 'Deny' : 'Allow'
    }
  }
}
var runtimeRoles = [
  '4633458b-17de-408a-b874-0445c86b69e6' // Key Vault Secrets User
  'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba' // Key Vault Certificate User
]
resource runtimeAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for role in runtimeRoles: {
    name: guid(vault.id, tags[?'she-owner'] ?? principalId, role)
    scope: vault
    properties: {
      principalId: principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
    }
  }
]
var officerRole = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
)
resource adminAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for admin in adminPrincipalIds: {
    name: guid(vault.id, admin, officerRole)
    scope: vault
    properties: {
      principalId: admin
      roleDefinitionId: officerRole
    }
  }
]
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
        name: '${name}-vault'
        properties: {
          privateLinkServiceId: vault.id
          groupIds: ['vault']
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
        name: 'vault'
        properties: {
          privateDnsZoneId: privateDnsZoneId
        }
      }
    ]
  }
}
output vaultUri string = vault.properties.vaultUri
