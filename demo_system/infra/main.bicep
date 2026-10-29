targetScope = 'resourceGroup'

param location string

@description('Lower-case letters and digits only. All resources are named and owned by this demo.')
@minLength(3)
@maxLength(10)
param namePrefix string

var prefix = empty(filter(
    range(0, length(namePrefix)),
    i => !contains('abcdefghijklmnopqrstuvwxyz0123456789', substring(namePrefix, i, 1))
  ))
  ? namePrefix
  : fail('namePrefix must contain only lower-case letters and digits.')
var demoOwner = uniqueString(resourceGroup().id, namePrefix)
var tags = {
  'she-demo-system': demoOwner
}
var runtimeName = 'aci-${prefix}-${demoOwner}'

// ACR enforces TLS 1.2; the registry API has no configurable minimum-TLS property.
resource registry 'Microsoft.ContainerRegistry/registries@2025-11-01' = {
  name: '${prefix}${demoOwner}acr'
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    publicNetworkAccess: 'Enabled'
    roleAssignmentMode: 'LegacyRegistryPermissions'
  }
}

resource runtimeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${prefix}-${demoOwner}'
  location: location
  tags: tags
}

// Azure also creates its own default MA_<workspace>_<region>_managed ingestion group.
// The custom route below belongs to the caller-selected resource group instead.
resource workspace 'Microsoft.Monitor/accounts@2023-04-03' = {
  name: 'amw-${prefix}-${demoOwner}'
  location: location
  tags: tags
  properties: {
    publicNetworkAccess: 'Enabled'
  }
}

resource ingestionEndpoint 'Microsoft.Insights/dataCollectionEndpoints@2023-03-11' = {
  name: 'dce-${prefix}-${demoOwner}'
  location: location
  tags: tags
  kind: 'Linux'
  properties: {
    networkAcls: {
      publicNetworkAccess: 'Enabled'
    }
  }
}

resource ingestionRule 'Microsoft.Insights/dataCollectionRules@2023-03-11' = {
  name: 'dcr-${prefix}-${demoOwner}'
  location: location
  tags: tags
  kind: 'Linux'
  properties: {
    dataCollectionEndpointId: ingestionEndpoint.id
    dataSources: {
      prometheusForwarder: [
        {
          name: 'checkout-prometheus'
          streams: [
            'Microsoft-PrometheusMetrics'
          ]
          labelIncludeFilter: {}
        }
      ]
    }
    destinations: {
      monitoringAccounts: [
        {
          name: 'demo-workspace'
          accountResourceId: workspace.id
        }
      ]
    }
    dataFlows: [
      {
        streams: [
          'Microsoft-PrometheusMetrics'
        ]
        destinations: [
          'demo-workspace'
        ]
      }
    ]
  }
}

var acrPullRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '7f951dda-4ed3-4680-a7ca-43fe172d538d'
)
var metricsPublisherRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '3913510d-42f4-4e42-8a64-420c390055eb'
)

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, runtimeIdentity.id, acrPullRoleId)
  scope: registry
  properties: {
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: acrPullRoleId
  }
}

resource metricsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(ingestionRule.id, runtimeIdentity.id, metricsPublisherRoleId)
  scope: ingestionRule
  properties: {
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: metricsPublisherRoleId
  }
}

resource healthModel 'Microsoft.CloudHealth/healthmodels@2026-10-01-preview' = {
  name: 'hm-${prefix}-${demoOwner}'
  location: location
  tags: tags
  properties: {}
}

resource checkout 'Microsoft.CloudHealth/healthmodels/entities@2026-10-01-preview' = {
  parent: healthModel
  name: 'checkout'
  properties: {
    displayName: 'Checkout'
    tags: tags
    signalGroups: {
      dependencies: {
        aggregationType: 'WorstOf'
      }
    }
  }
}

resource api 'Microsoft.CloudHealth/healthmodels/entities@2026-10-01-preview' = {
  parent: healthModel
  name: 'checkout-api'
  properties: {
    displayName: 'Checkout API'
    tags: tags
    // External signals are created by evaluator reports, not by ARM.
    signalAggregationGroups: [
      {
        name: 'api-signals'
        aggregationType: 'WorstOf'
        members: [
          'p95-latency'
          'checkout-error-rate'
          'availability'
        ]
      }
    ]
  }
}

resource traffic 'Microsoft.CloudHealth/healthmodels/entities@2026-10-01-preview' = {
  parent: healthModel
  name: 'checkout-traffic'
  properties: {
    displayName: 'Checkout traffic'
    tags: tags
    signalAggregationGroups: [
      {
        name: 'traffic-signals'
        aggregationType: 'WorstOf'
        members: [
          'traffic-freshness'
        ]
      }
    ]
  }
}

resource apiDependency 'Microsoft.CloudHealth/healthmodels/relationships@2026-10-01-preview' = {
  parent: healthModel
  name: 'checkout-to-api'
  properties: {
    parentEntityName: checkout.name
    childEntityName: api.name
    tags: tags
  }
}

resource trafficDependency 'Microsoft.CloudHealth/healthmodels/relationships@2026-10-01-preview' = {
  parent: healthModel
  name: 'checkout-to-traffic'
  properties: {
    parentEntityName: checkout.name
    childEntityName: traffic.name
    tags: tags
  }
}

// The model's root entity (named after the model) reports the demo system's health.
resource rootDependency 'Microsoft.CloudHealth/healthmodels/relationships@2026-10-01-preview' = {
  parent: healthModel
  name: 'model-to-checkout'
  properties: {
    parentEntityName: healthModel.name
    childEntityName: checkout.name
    tags: tags
  }
}

output owner string = demoOwner
output registryName string = registry.name
output registryLoginServer string = registry.properties.loginServer
output registryResourceId string = registry.id
output identityName string = runtimeIdentity.name
output identityResourceId string = runtimeIdentity.id
output identityClientId string = runtimeIdentity.properties.clientId
output identityPrincipalId string = runtimeIdentity.properties.principalId
output workspaceResourceId string = workspace.id
output workspaceQueryEndpoint string = workspace.properties.metrics.prometheusQueryEndpoint
output healthModelResourceId string = healthModel.id
output dataCollectionRuleResourceId string = ingestionRule.id
output dataCollectionEndpointResourceId string = ingestionEndpoint.id
// The documented stream route is appended to the actual service origin and immutable DCR ID.
output metricsIngestionEndpoint string = '${ingestionEndpoint.properties.metricsIngestion.endpoint}/dataCollectionRules/${ingestionRule.properties.immutableId}/streams/Microsoft-PrometheusMetrics/api/v1/write?api-version=2023-04-24'
output containerGroupName string = runtimeName
output containerGroupResourceId string = resourceId('Microsoft.ContainerInstance/containerGroups', runtimeName)
