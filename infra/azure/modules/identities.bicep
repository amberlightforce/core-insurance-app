// One user-assigned managed identity per app and job (INFRASTRUCTURE §6.2). `bootstrap` is the only identity that can
// read the PostgreSQL administrator credential.

param location string
param namePrefix string
param tags object

var apps = ['api', 'worker', 'migrate', 'gotenberg', 'bootstrap']

resource identities 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = [
  for app in apps: {
    name: 'id-${namePrefix}-${app}'
    location: location
    tags: tags
  }
]

output api object = {
  id: identities[0].id
  principalId: identities[0].properties.principalId
  clientId: identities[0].properties.clientId
}
output worker object = {
  id: identities[1].id
  principalId: identities[1].properties.principalId
  clientId: identities[1].properties.clientId
}
output migrate object = {
  id: identities[2].id
  principalId: identities[2].properties.principalId
  clientId: identities[2].properties.clientId
}
output gotenberg object = {
  id: identities[3].id
  principalId: identities[3].properties.principalId
  clientId: identities[3].properties.clientId
}
output bootstrap object = {
  id: identities[4].id
  principalId: identities[4].properties.principalId
  clientId: identities[4].properties.clientId
}
