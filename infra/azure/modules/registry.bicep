// Azure Container Registry (Basic) with AcrPull for the app identities. Admin user disabled.

param location string

@description('Registry name: 5-50 alphanumeric characters, globally unique.')
param name string

param tags object

@description('Principal IDs that may pull images.')
param pullPrincipalIds array

var acrPullRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in pullPrincipalIds: {
    scope: registry
    name: guid(registry.id, principalId, acrPullRoleId)
    properties: {
      roleDefinitionId: acrPullRoleId
      principalId: principalId
      principalType: 'ServicePrincipal'
    }
  }
]

output loginServer string = registry.properties.loginServer
output id string = registry.id
