// Key Vault (Standard, RBAC, purge protection), firewalled to the Container Apps subnet (INFRASTRUCTURE §6.2).
// Holds the database connection strings; apps read them through Key Vault references with their identities.

param location string

@description('Key Vault name: 3-24 characters, globally unique.')
param name string

param tags object

@description('Subnet allowed through the firewall (snet-apps).')
param appsSubnetId string

@description('Principal IDs that may read secrets (Key Vault Secrets User).')
param secretReaderPrincipalIds array

@secure()
@description('Connection string for the application role `app` (api, worker).')
param coreConnectionString string

@secure()
@description('Connection string for the migration role `migrator` (migrate job).')
param migratorConnectionString string

@secure()
@description('Server administrator connection to the maintenance database (bootstrap job only).')
param adminConnectionString string

@secure()
@description('Password of the `app` role, applied by the bootstrap job.')
param appDbPassword string

@secure()
@description('Password of the `migrator` role, applied by the bootstrap job.')
param migratorDbPassword string

var secretsUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
      virtualNetworkRules: [
        { id: appsSubnetId, ignoreMissingVnetServiceEndpoint: false }
      ]
    }
  }
}

resource coreSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'connectionstrings-core'
  properties: { value: coreConnectionString }
}

resource migratorSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'connectionstrings-migrator'
  properties: { value: migratorConnectionString }
}

resource adminSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'connectionstrings-admin'
  properties: { value: adminConnectionString }
}

resource appPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'db-app-password'
  properties: { value: appDbPassword }
}

resource migratorPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'db-migrator-password'
  properties: { value: migratorDbPassword }
}

resource readers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in secretReaderPrincipalIds: {
    scope: vault
    name: guid(vault.id, principalId, secretsUserRoleId)
    properties: {
      roleDefinitionId: secretsUserRoleId
      principalId: principalId
      principalType: 'ServicePrincipal'
    }
  }
]

output uri string = vault.properties.vaultUri
output coreSecretUri string = coreSecret.properties.secretUri
output migratorSecretUri string = migratorSecret.properties.secretUri
output adminSecretUri string = adminSecret.properties.secretUri
output appRoleSecretUri string = appPasswordSecret.properties.secretUri
output migratorRoleSecretUri string = migratorPasswordSecret.properties.secretUri
