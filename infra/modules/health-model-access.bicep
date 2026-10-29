@description('Name of an existing model in this module deployment resource group. No model is created.')
param modelName string
param principalId string
param roleDefinitionId string
param owner string = ''

resource model 'Microsoft.CloudHealth/healthmodels@2025-05-01-preview' existing = {
  name: modelName
}
resource access 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(model.id, empty(owner) ? principalId : owner, roleDefinitionId)
  scope: model
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: roleDefinitionId
  }
}
