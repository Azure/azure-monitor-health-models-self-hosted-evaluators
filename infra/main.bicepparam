using './main.bicep'

param location = 'eastus'
param resourceGroupName = 'rg-she'
param namePrefix = 'she'
// Prerequisites are safe to provision without an image. deploy.ps1 controls the final phase.
param deployContainerGroup = false
param networkMode = 'public'
// Replace with real existing model IDs before deployment; this template never creates models/entities.
param healthModelResourceIds = [
  '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/models/providers/Microsoft.CloudHealth/healthmodels/replace-me'
]
param adminPrincipalIds = []
param pluginConfigurations = [
  {
    key: 'STATIC'
    config: {
      schemaVersion: 1
      name: 'static-demo'
      package: 'static'
      endpoints: {}
      auth: {
        type: 'none'
      }
    }
  }
]
param modelConfigurations = [
  {
    key: 'DEMO'
    config: {
      schemaVersion: 1
      name: 'demo-model'
      healthModel: {
        dataPlaneUri: 'https://replace-me.example.invalid'
      }
      signals: [
        {
          entity: 'demo-entity'
          name: 'demo-signal'
          plugin: 'static-demo'
          query: {
            kind: 'observation'
            value: 1
          }
          evaluation: {
            unhealthy: {
              operator: 'GreaterThan'
              threshold: 10
            }
          }
        }
      ]
    }
  }
]
