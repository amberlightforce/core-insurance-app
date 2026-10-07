// Azure Communication Services Email with an Azure-managed domain (INFRASTRUCTURE §6.2). Data stays in Europe.
// A custom sending domain replaces the managed one before real customer mail (Stage 2).

param namePrefix string
param tags object

@description('Data residency of the Communication Services resources.')
param dataLocation string = 'Europe'

@description('Principal IDs allowed to send mail (Contributor on the Communication Services resource).')
param senderPrincipalIds array

// Built-in "Contributor": the documented role for sending through Communication Services with Entra ID.
var contributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b24988ac-6180-42a0-ab88-20f7382dd24c')

resource emailService 'Microsoft.Communication/emailServices@2023-04-01' = {
  name: 'ecs-${namePrefix}'
  location: 'global'
  tags: tags
  properties: { dataLocation: dataLocation }
}

resource managedDomain 'Microsoft.Communication/emailServices/domains@2023-04-01' = {
  parent: emailService
  name: 'AzureManagedDomain'
  location: 'global'
  tags: tags
  properties: {
    domainManagement: 'AzureManaged'
    userEngagementTracking: 'Disabled'
  }
}

resource communication 'Microsoft.Communication/communicationServices@2023-04-01' = {
  name: 'acs-${namePrefix}'
  location: 'global'
  tags: tags
  properties: {
    dataLocation: dataLocation
    linkedDomains: [managedDomain.id]
  }
}

resource senders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in senderPrincipalIds: {
    scope: communication
    name: guid(communication.id, principalId, contributorRoleId)
    properties: {
      roleDefinitionId: contributorRoleId
      principalId: principalId
      principalType: 'ServicePrincipal'
    }
  }
]

output endpoint string = 'https://${communication.properties.hostName}'
output senderDomain string = managedDomain.properties.mailFromSenderDomain
