targetScope = 'subscription'

@description('Azure public-cloud region supported by ACI with UAMI and VNet. deploy.ps1 checks availability.')
param location string
param resourceGroupName string = 'rg-she'
@minLength(3)
@maxLength(10)
param namePrefix string = 'she'
param tags object = {}
param deployContainerGroup bool = true
@description('Bare registry manifest digest sha256:<64 lowercase hex>. Required for the container phase.')
param imageDigest string = ''
param imageRepository string = 'evaluator'
type ConfigurationEntry = {
  key: string
  config: object
}
@minLength(1)
param pluginConfigurations ConfigurationEntry[]
@minLength(1)
param modelConfigurations ConfigurationEntry[]
param hostSettings object = {}
@minValue(1)
@maxValue(31)
param cpu int = 1
@minValue(1)
@maxValue(240)
param memoryInGB int = 2
@minValue(1)
param livenessInitialDelaySeconds int = 60
@minValue(1)
param readinessInitialDelaySeconds int = 5
@minValue(1)
param probePeriodSeconds int = 10
@minValue(3)
param probeTimeoutSeconds int = 5
@minValue(1)
param probeFailureThreshold int = 3
@description('Empty, or one ACI-supported availability zone in the selected region.')
param zones string[] = []
@allowed([
  'public'
  'vnetNat'
  'private'
])
param networkMode string = 'public'
@description('Vault-only public access for public/vnetNat deployments. Disabled does not create private connectivity. Private mode always disables it.')
@allowed([
  'Enabled'
  'Disabled'
])
param keyVaultPublicNetworkAccess string = 'Enabled'
param vnetAddressPrefix string = '10.42.0.0/16'
@description('Dedicated, aligned IPv4 subnet, /24 or larger. Validated by deploy.ps1.')
param aciSubnetPrefix string = '10.42.0.0/24'
param endpointSubnetPrefix string = '10.42.1.0/24'
@minLength(1)
param healthModelResourceIds string[]
param adminPrincipalIds string[] = []
param actionGroupId string = ''

resource group 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module identity 'modules/identity.bicep' = {
  name: '${namePrefix}-identity'
  scope: group
  params: {
    name: '${namePrefix}-reporter'
    location: location
    tags: tags
  }
}
module networking 'modules/networking.bicep' = if (networkMode != 'public') {
  name: '${namePrefix}-networking'
  scope: group
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    privateMode: networkMode == 'private'
    vnetAddressPrefix: vnetAddressPrefix
    aciSubnetPrefix: aciSubnetPrefix
    endpointSubnetPrefix: endpointSubnetPrefix
  }
}
module registry 'modules/registry.bicep' = {
  name: '${namePrefix}-registry'
  scope: group
  params: {
    name: '${namePrefix}${uniqueString(subscription().subscriptionId, resourceGroupName)}'
    location: location
    tags: tags
    principalId: identity.outputs.principalId
    privateMode: networkMode == 'private'
    endpointSubnetId: networkMode == 'private' ? networking!.outputs.endpointSubnetId : ''
    privateDnsZoneId: networkMode == 'private' ? networking!.outputs.registryDnsZoneId : ''
  }
}
module monitoring 'modules/monitoring.bicep' = {
  name: '${namePrefix}-monitoring'
  scope: group
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    principalId: identity.outputs.principalId
  }
}
module keyvault 'modules/keyvault.bicep' = {
  name: '${namePrefix}-keyvault'
  scope: group
  params: {
    name: '${namePrefix}-${uniqueString(subscription().subscriptionId, resourceGroupName)}'
    location: location
    tags: tags
    principalId: identity.outputs.principalId
    adminPrincipalIds: adminPrincipalIds
    privateMode: networkMode == 'private'
    publicNetworkAccess: keyVaultPublicNetworkAccess
    endpointSubnetId: networkMode == 'private' ? networking!.outputs.endpointSubnetId : ''
    privateDnsZoneId: networkMode == 'private' ? networking!.outputs.vaultDnsZoneId : ''
  }
}
var contributorRoleId = 'b24988ac-6180-42a0-ab88-20f7382dd24c'
module modelAccess 'modules/health-model-access.bicep' = [
  for (id, i) in healthModelResourceIds: {
    name: '${namePrefix}-model-access-${i}'
    scope: resourceGroup(split(id, '/')[2], split(id, '/')[4])
    params: {
      modelName: last(split(id, '/'))
      principalId: identity.outputs.principalId
      roleDefinitionId: subscriptionResourceId(split(id, '/')[2], 'Microsoft.Authorization/roleDefinitions', contributorRoleId)
      owner: tags[?'she-owner'] ?? ''
    }
  }
]
module containerGroup 'modules/container-group.bicep' = if (deployContainerGroup) {
  name: '${namePrefix}-container-group'
  scope: group
  params: {
    name: '${namePrefix}-evaluator'
    location: location
    tags: tags
    imageDigest: imageDigest
    imageRepository: imageRepository
    registryLoginServer: registry.outputs.loginServer
    reporterIdentityResourceId: identity.outputs.resourceId
    reporterIdentityClientId: identity.outputs.clientId
    applicationInsightsConnectionString: monitoring.outputs.connectionString
    pluginConfigurations: pluginConfigurations
    modelConfigurations: modelConfigurations
    hostSettings: hostSettings
    cpu: cpu
    memoryInGB: memoryInGB
    livenessInitialDelaySeconds: livenessInitialDelaySeconds
    readinessInitialDelaySeconds: readinessInitialDelaySeconds
    probePeriodSeconds: probePeriodSeconds
    probeTimeoutSeconds: probeTimeoutSeconds
    probeFailureThreshold: probeFailureThreshold
    zones: zones
    networkMode: networkMode
    subnetId: networkMode != 'public' ? networking!.outputs.aciSubnetId : ''
  }
  dependsOn: [
    keyvault
    modelAccess
  ]
}
module alerts 'modules/alerts.bicep' = if (!empty(actionGroupId)) {
  name: '${namePrefix}-alerts'
  scope: group
  params: {
    namePrefix: namePrefix
    location: location
    tags: tags
    workspaceId: monitoring.outputs.workspaceResourceId
    applicationInsightsId: monitoring.outputs.applicationInsightsId
    actionGroupId: actionGroupId
  }
}

output containerGroupId string = resourceId(
  subscription().subscriptionId,
  resourceGroupName,
  'Microsoft.ContainerInstance/containerGroups',
  '${namePrefix}-evaluator'
)
output containerGroupName string = '${namePrefix}-evaluator'
output registryLoginServer string = registry.outputs.loginServer
output identityResourceId string = identity.outputs.resourceId
output identityPrincipalId string = identity.outputs.principalId
output identityClientId string = identity.outputs.clientId
output vaultUri string = keyvault.outputs.vaultUri
output roleDefinitionId string = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', contributorRoleId)
output imageDigest string = deployContainerGroup ? imageDigest : ''
output egressIp string = networkMode != 'public' ? networking!.outputs.egressIp : ''
