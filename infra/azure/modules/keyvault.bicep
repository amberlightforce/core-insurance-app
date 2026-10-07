// Key Vault (Standard, RBAC, purge protection), firewalled to the Container Apps subnet (INFRASTRUCTURE §6.2).
// Holds the database credentials and the Entra client secret. Least privilege: "Key Vault Secrets User" is granted
// per SECRET, never on the vault; each identity can read only the secrets its own workload needs
// (checked in CI by scripts/check-keyvault-rbac.py):
//   connectionstrings-core                          -> api, worker
//   entra-client-secret                             -> api (Container Apps built-in authentication)
//   connectionstrings-migrator                      -> migrate
//   connectionstrings-admin, db-*-password          -> bootstrap

param location string

@description('Key Vault name: 3-24 characters, globally unique.')
param name string

param tags object

@description('Subnet allowed through the firewall (snet-apps).')
param appsSubnetId string

@description('Principal IDs that read connectionstrings-core (api, worker).')
param coreReaderPrincipalIds array

@description('Principal IDs that read entra-client-secret (api).')
param authReaderPrincipalIds array

@description('Principal IDs that read connectionstrings-migrator (migrate).')
param migratorReaderPrincipalIds array

@description('Principal IDs that read the administrator connection and the role passwords (bootstrap).')
param bootstrapReaderPrincipalIds array

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

@secure()
@description('Client secret of the staff app registration (Container Apps built-in authentication).')
param entraClientSecret string

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

resource entraSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'entra-client-secret'
  properties: { value: entraClientSecret }
}

// ---- Per-secret read access (no vault-scope assignments) ----

resource coreReaders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in coreReaderPrincipalIds: {
    scope: coreSecret
    name: guid(coreSecret.id, principalId, secretsUserRoleId)
    properties: { roleDefinitionId: secretsUserRoleId, principalId: principalId, principalType: 'ServicePrincipal' }
  }
]

resource authReaders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in authReaderPrincipalIds: {
    scope: entraSecret
    name: guid(entraSecret.id, principalId, secretsUserRoleId)
    properties: { roleDefinitionId: secretsUserRoleId, principalId: principalId, principalType: 'ServicePrincipal' }
  }
]

resource migratorReaders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in migratorReaderPrincipalIds: {
    scope: migratorSecret
    name: guid(migratorSecret.id, principalId, secretsUserRoleId)
    properties: { roleDefinitionId: secretsUserRoleId, principalId: principalId, principalType: 'ServicePrincipal' }
  }
]

resource adminReaders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in bootstrapReaderPrincipalIds: {
    scope: adminSecret
    name: guid(adminSecret.id, principalId, secretsUserRoleId)
    properties: { roleDefinitionId: secretsUserRoleId, principalId: principalId, principalType: 'ServicePrincipal' }
  }
]

resource appPasswordReaders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in bootstrapReaderPrincipalIds: {
    scope: appPasswordSecret
    name: guid(appPasswordSecret.id, principalId, secretsUserRoleId)
    properties: { roleDefinitionId: secretsUserRoleId, principalId: principalId, principalType: 'ServicePrincipal' }
  }
]

resource migratorPasswordReaders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in bootstrapReaderPrincipalIds: {
    scope: migratorPasswordSecret
    name: guid(migratorPasswordSecret.id, principalId, secretsUserRoleId)
    properties: { roleDefinitionId: secretsUserRoleId, principalId: principalId, principalType: 'ServicePrincipal' }
  }
]

output uri string = vault.properties.vaultUri
output coreSecretUri string = coreSecret.properties.secretUri
output migratorSecretUri string = migratorSecret.properties.secretUri
output adminSecretUri string = adminSecret.properties.secretUri
output appRoleSecretUri string = appPasswordSecret.properties.secretUri
output migratorRoleSecretUri string = migratorPasswordSecret.properties.secretUri
output entraSecretUri string = entraSecret.properties.secretUri
