// Azure Communication Services Email with an Azure-managed domain (INFRASTRUCTURE §6.2). Data stays in Europe.
// A custom sending domain replaces the managed one before real customer mail (Stage 2).

param namePrefix string
param tags object

@description('Data residency of the Communication Services resources.')
param dataLocation string = 'Europe'

@description('Principal IDs allowed to send mail (custom role "CoreIns Email Sender" on the Communication Services resource).')
param senderPrincipalIds array

// Custom role with only the actions Microsoft documents for sending email with an Entra ID identity
// (Communication Services "send email with Microsoft Entra ID / SMTP authentication" guidance). All three exist in
// `az provider operation show --namespace Microsoft.Communication`; there is no email data action to use instead.
resource emailSenderRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, 'coreins-email-sender')
  properties: {
    roleName: 'CoreIns Email Sender (${resourceGroup().name})'
    description: 'Send email through Azure Communication Services. Nothing else.'
    type: 'CustomRole'
    assignableScopes: [resourceGroup().id]
    permissions: [
      {
        actions: [
          'Microsoft.Communication/CommunicationServices/Read'
          'Microsoft.Communication/CommunicationServices/Write'
          'Microsoft.Communication/EmailServices/write'
        ]
        notActions: []
        dataActions: []
        notDataActions: []
      }
    ]
  }
}

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
    name: guid(communication.id, principalId, emailSenderRole.id)
    properties: {
      roleDefinitionId: emailSenderRole.id
      principalId: principalId
      principalType: 'ServicePrincipal'
    }
  }
]

output endpoint string = 'https://${communication.properties.hostName}'
output senderDomain string = managedDomain.properties.mailFromSenderDomain
