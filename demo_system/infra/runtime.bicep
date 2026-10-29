targetScope = 'resourceGroup'

param location string

@description('The same lower-case letters/digits prefix used for main.bicep.')
@minLength(3)
@maxLength(10)
param namePrefix string

@description('Immutable app image in this demo registry: <registry>/<repository>@sha256:<64 lowercase hex digits>.')
param appImage string

@minValue(1)
@maxValue(3600)
param intervalSeconds int = 5

@description('The full metricsIngestionEndpoint output of main.bicep, including the DCR immutable ID and stream route.')
param metricsIngestionEndpoint string

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

resource registry 'Microsoft.ContainerRegistry/registries@2025-11-01' existing = {
  name: '${prefix}${demoOwner}acr'
}

resource runtimeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: 'id-${prefix}-${demoOwner}'
}

var digestParts = split(appImage, '@sha256:')
var digest = last(digestParts)
var immutableImage = length(digestParts) == 2 && length(digest) == 64 && empty(filter(
    range(0, length(digest)),
    i => !contains('0123456789abcdef', substring(digest, i, 1))
  ))
  ? appImage
  : fail('appImage must be a registry-qualified SHA256 reference with 64 lowercase hex digits.')
var runtimeAppImage = startsWith(immutableImage, '${registry.properties.loginServer}/') && length(first(digestParts)) > length('${registry.properties.loginServer}/')
  ? immutableImage
  : fail('appImage must use a repository in the demo registry.')

var prometheusConfig = replace(
  replace(loadTextContent('prometheus.yml'), '__METRICS_INGESTION_ENDPOINT__', metricsIngestionEndpoint),
  '__IDENTITY_CLIENT_ID__',
  runtimeIdentity.properties.clientId
)

resource runtime 'Microsoft.ContainerInstance/containerGroups@2023-05-01' = {
  name: runtimeName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${runtimeIdentity.id}': {}
    }
  }
  properties: {
    osType: 'Linux'
    restartPolicy: 'Always'
    imageRegistryCredentials: [
      {
        server: registry.properties.loginServer
        identity: runtimeIdentity.id
      }
    ]
    containers: [
      {
        name: 'checkout-api'
        properties: {
          image: runtimeAppImage
          command: [
            'dotnet'
            '/app/Checkout.Demo.dll'
            'api'
          ]
          resources: {
            requests: {
              cpu: json('0.5')
              memoryInGB: json('0.5')
            }
          }
          readinessProbe: {
            httpGet: {
              path: '/healthz'
              port: 8080
              scheme: 'http'
            }
            initialDelaySeconds: 5
            periodSeconds: 15
            timeoutSeconds: 5
            failureThreshold: 3
          }
          livenessProbe: {
            httpGet: {
              path: '/healthz'
              port: 8080
              scheme: 'http'
            }
            initialDelaySeconds: 30
            periodSeconds: 30
            timeoutSeconds: 5
            failureThreshold: 3
          }
        }
      }
      {
        name: 'traffic-generator'
        properties: {
          image: runtimeAppImage
          command: [
            'dotnet'
            '/app/Checkout.Demo.dll'
            'traffic'
          ]
          environmentVariables: [
            {
              name: 'DEMO_INTERVAL_SECONDS'
              value: string(intervalSeconds)
            }
            {
              name: 'DEMO_API_URL'
              value: 'http://127.0.0.1:8080'
            }
          ]
          resources: {
            requests: {
              cpu: json('0.25')
              memoryInGB: json('0.5')
            }
          }
        }
      }
      {
        name: 'prometheus'
        properties: {
          image: 'prom/prometheus:v3.15.1'
          command: [
            '/bin/prometheus'
            '--config.file=/etc/checkout-prometheus/prometheus.yml'
            '--storage.tsdb.path=/prometheus'
            '--storage.tsdb.retention.time=1h'
            '--storage.tsdb.retention.size=128MB'
            '--web.listen-address=127.0.0.1:9090'
          ]
          resources: {
            requests: {
              cpu: json('0.25')
              memoryInGB: json('0.5')
            }
          }
          volumeMounts: [
            {
              name: 'prometheus-config'
              mountPath: '/etc/checkout-prometheus'
              readOnly: true
            }
          ]
        }
      }
    ]
    volumes: [
      {
        name: 'prometheus-config'
        secret: {
          'prometheus.yml': base64(prometheusConfig)
        }
      }
    ]
  }
}

output containerGroupName string = runtime.name
output containerGroupResourceId string = runtime.id
