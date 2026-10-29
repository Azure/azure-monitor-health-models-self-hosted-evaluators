param name string
param location string
param tags object

resource reporter 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: name
  location: location
  tags: tags
}
output resourceId string = reporter.id
output principalId string = reporter.properties.principalId
output clientId string = reporter.properties.clientId
