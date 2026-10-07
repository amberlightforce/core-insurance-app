// Storage account (StorageV2, LRS) with containers documents and audit-archive (immutability policies), inbound and
// exports. Public access off, soft delete on, firewall allows snet-apps only (INFRASTRUCTURE §6.2).

param location string

@description('Storage account name: 3-24 lowercase letters and digits, globally unique.')
param name string

param tags object

@description('Subnet allowed through the firewall (snet-apps).')
param appsSubnetId string

@description('Principal IDs that read and write blobs (Storage Blob Data Contributor).')
param blobContributorPrincipalIds array

@description('Time-based retention (days) for documents and audit-archive. The policy is left unlocked; locking is an irreversible, separate decision.')
@minValue(1)
param immutabilityRetentionDays int

@description('Soft-delete retention (days) for blobs and containers.')
param softDeleteDays int = 7

var blobContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var immutableContainers = ['documents', 'audit-archive']
var mutableContainers = ['inbound', 'exports']

resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
      virtualNetworkRules: [
        { id: appsSubnetId, action: 'Allow' }
      ]
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: account
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: softDeleteDays }
    containerDeleteRetentionPolicy: { enabled: true, days: softDeleteDays }
  }
}

resource immutable 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [
  for container in immutableContainers: {
    parent: blobService
    name: container
    properties: { publicAccess: 'None' }
  }
]

resource immutabilityPolicies 'Microsoft.Storage/storageAccounts/blobServices/containers/immutabilityPolicies@2023-05-01' = [
  for (container, i) in immutableContainers: {
    parent: immutable[i]
    name: 'default'
    properties: {
      immutabilityPeriodSinceCreationInDays: immutabilityRetentionDays
      allowProtectedAppendWrites: false
    }
  }
]

resource mutable 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [
  for container in mutableContainers: {
    parent: blobService
    name: container
    properties: { publicAccess: 'None' }
  }
]

resource contributors 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in blobContributorPrincipalIds: {
    scope: account
    name: guid(account.id, principalId, blobContributorRoleId)
    properties: {
      roleDefinitionId: blobContributorRoleId
      principalId: principalId
      principalType: 'ServicePrincipal'
    }
  }
]

output blobEndpoint string = account.properties.primaryEndpoints.blob
