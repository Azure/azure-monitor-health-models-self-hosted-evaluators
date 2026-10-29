param name string
param location string
param tags object
@minLength(71)
@maxLength(71)
param imageDigest string
param imageRepository string
param registryLoginServer string
param reporterIdentityResourceId string
param reporterIdentityClientId string
param applicationInsightsConnectionString string
param pluginConfigurations array
param modelConfigurations array
param hostSettings object
param cpu int
param memoryInGB int
param livenessInitialDelaySeconds int
param readinessInitialDelaySeconds int
param probePeriodSeconds int
param probeTimeoutSeconds int
param probeFailureThreshold int
param zones string[]
param networkMode string
param subnetId string

var generatedEnvironment = [
  {
    name: 'AZURE_CLIENT_ID'
    value: reporterIdentityClientId
  }
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: applicationInsightsConnectionString
  }
  {
    name: 'SHE_PACKAGES_DIR'
    value: '/app/packages'
  }
]
var pluginEnvironment = [
  for entry in pluginConfigurations: {
    name: 'SHE_PLUGIN_${entry.key}'
    value: string(entry.config)
  }
]
var modelEnvironment = [
  for entry in modelConfigurations: {
    name: 'SHE_MODEL_${entry.key}'
    value: string(entry.config)
  }
]
var hostEnvironment = [
  for setting in items(hostSettings): {
    name: setting.key
    value: string(setting.value)
  }
]
var groupProperties = {
  sku: 'Standard'
  osType: 'Linux'
  restartPolicy: 'Always'
  imageRegistryCredentials: [
    {
      server: registryLoginServer
      identity: reporterIdentityResourceId
    }
  ]
  containers: [
    {
      name: 'evaluator'
      properties: {
        image: '${registryLoginServer}/${imageRepository}@${imageDigest}'
        environmentVariables: concat(generatedEnvironment, pluginEnvironment, modelEnvironment, hostEnvironment)
        resources: {
          requests: {
            cpu: cpu
            memoryInGB: memoryInGB
          }
        }
        readinessProbe: {
          exec: {
            command: ['/usr/share/dotnet/dotnet', '/app/host/SelfHostedEvaluators.Host.dll', 'probe', 'ready']
          }
          initialDelaySeconds: readinessInitialDelaySeconds
          periodSeconds: probePeriodSeconds
          timeoutSeconds: probeTimeoutSeconds
          failureThreshold: probeFailureThreshold
          successThreshold: 1
        }
        livenessProbe: {
          exec: {
            command: ['/usr/share/dotnet/dotnet', '/app/host/SelfHostedEvaluators.Host.dll', 'probe', 'live']
          }
          initialDelaySeconds: livenessInitialDelaySeconds
          periodSeconds: probePeriodSeconds
          timeoutSeconds: probeTimeoutSeconds
          failureThreshold: probeFailureThreshold
          successThreshold: 1
        }
      }
    }
  ]
}
resource group 'Microsoft.ContainerInstance/containerGroups@2025-09-01' = {
  name: name
  location: location
  tags: union(tags, {
    'she-network-mode': networkMode
  })
  zones: zones
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${reporterIdentityResourceId}': {}
    }
  }
  properties: union(
    groupProperties,
    networkMode == 'public'
      ? {}
      : {
          subnetIds: [
            {
              id: subnetId
            }
          ]
          dnsConfig: {
            nameServers: ['168.63.129.16']
          }
        }
  )
}
output resourceId string = group.id
