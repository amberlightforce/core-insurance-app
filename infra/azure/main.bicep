// One stamp of the core insurance system (INFRASTRUCTURE §6): deploy into resource group rg-coreins-<env>-<region>.
//   az deployment group create -g rg-coreins-dev-gwc -f infra/azure/main.bicep -p infra/azure/dev.bicepparam
// First deployment: deployApps=false (creates the registry), push the image, then deploy again with deployApps=true.
targetScope = 'resourceGroup'

@description('Azure region. Default Germany West Central (INFRASTRUCTURE §10 decision 1).')
param location string = 'germanywestcentral'

@description('Short region code used in names.')
param regionCode string = 'gwc'

@allowed(['dev', 'prod'])
param environmentName string

@description('Stamp settings passed to the application (INFRASTRUCTURE §5).')
param stampLegalEntity string = 'GR-TEST'
param stampCountry string = 'GR'

@description('Globally unique suffix for registry, Key Vault and storage names.')
@minLength(3)
@maxLength(8)
param uniqueSuffix string = substring(uniqueString(resourceGroup().id), 0, 6)

@description('Deploy api, worker, gotenberg and the migrate job. False on the very first run, before an image exists.')
param deployApps bool = true

@description('Application image tag in the registry (repository coreins).')
param imageTag string = 'latest'

@description('Gotenberg image (public, pinned).')
param gotenbergImage string = 'gotenberg/gotenberg:8.37.0'

// PostgreSQL
param postgresSkuName string = 'Standard_B1ms'
@allowed(['Burstable', 'GeneralPurpose', 'MemoryOptimized'])
param postgresSkuTier string = 'Burstable'
param postgresStorageGB int = 32
param postgresBackupRetentionDays int = 7
@allowed(['Disabled', 'ZoneRedundant', 'SameZone'])
param postgresHighAvailability string = 'Disabled'
param postgresGeoRedundantBackup bool = false
param postgresAdminLogin string = 'pgadmin'
@secure()
param postgresAdminPassword string
@secure()
@description('Password of the `app` role (created on the server before the first migrate run).')
param appDbPassword string
@secure()
@description('Password of the `migrator` role.')
param migratorDbPassword string

// Storage
@description('Retention (days) of the immutability policies on documents and audit-archive.')
param immutabilityRetentionDays int

// Apps
param apiMinReplicas int = 0
param apiMaxReplicas int = 3
param gotenbergMaxReplicas int = 2

@description('Entra ID tenant of the staff app registration.')
param entraTenantId string = subscription().tenantId
@description('Client ID of the staff app registration. Empty disables built-in authentication (not allowed for real use).')
param entraClientId string = ''
@description('Name of the Key Vault secret holding the app registration client secret (created manually).')
param entraClientSecretName string = 'entra-client-secret'

// Monitoring
param logDailyQuotaGb string = '0.15'
param logRetentionDays int = 30

var namePrefix = 'coreins-${environmentName}-${regionCode}'
var compactPrefix = 'coreins${environmentName}${uniqueSuffix}'
var tags = {
  app: 'coreins'
  env: environmentName
  stamp: '${environmentName}-${regionCode}'
}

module network 'modules/network.bicep' = {
  name: 'network'
  params: { location: location, namePrefix: namePrefix, tags: tags }
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    location: location
    namePrefix: namePrefix
    tags: tags
    dailyQuotaGb: logDailyQuotaGb
    retentionInDays: logRetentionDays
  }
}

module identities 'modules/identities.bicep' = {
  name: 'identities'
  params: { location: location, namePrefix: namePrefix, tags: tags }
}

module registry 'modules/registry.bicep' = {
  name: 'registry'
  params: {
    location: location
    name: 'acr${compactPrefix}'
    tags: tags
    pullPrincipalIds: [
      identities.outputs.api.principalId
      identities.outputs.worker.principalId
      identities.outputs.migrate.principalId
    ]
  }
}

module postgres 'modules/postgres.bicep' = {
  name: 'postgres'
  params: {
    location: location
    namePrefix: namePrefix
    tags: tags
    dbSubnetId: network.outputs.dbSubnetId
    privateDnsZoneId: network.outputs.postgresDnsZoneId
    skuName: postgresSkuName
    skuTier: postgresSkuTier
    storageSizeGB: postgresStorageGB
    backupRetentionDays: postgresBackupRetentionDays
    highAvailability: postgresHighAvailability
    geoRedundantBackup: postgresGeoRedundantBackup
    administratorLogin: postgresAdminLogin
    administratorPassword: postgresAdminPassword
  }
}

var pgHost = postgres.outputs.fqdn

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    location: location
    name: 'kv-${compactPrefix}'
    tags: tags
    appsSubnetId: network.outputs.appsSubnetId
    secretReaderPrincipalIds: [
      identities.outputs.api.principalId
      identities.outputs.worker.principalId
      identities.outputs.migrate.principalId
    ]
    coreConnectionString: 'Host=${pgHost};Database=coreins;Username=app;Password=${appDbPassword};SslMode=Require'
    migratorConnectionString: 'Host=${pgHost};Database=coreins;Username=migrator;Password=${migratorDbPassword};SslMode=Require'
  }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    location: location
    name: 'st${compactPrefix}'
    tags: tags
    appsSubnetId: network.outputs.appsSubnetId
    blobContributorPrincipalIds: [
      identities.outputs.api.principalId
      identities.outputs.worker.principalId
    ]
    immutabilityRetentionDays: immutabilityRetentionDays
  }
}

module environment 'modules/containerapps-env.bicep' = {
  name: 'containerapps-env'
  params: {
    location: location
    namePrefix: namePrefix
    tags: tags
    appsSubnetId: network.outputs.appsSubnetId
    logAnalyticsWorkspaceName: monitoring.outputs.workspaceName
  }
}

var appImage = '${registry.outputs.loginServer}/coreins:${imageTag}'

// Settings shared by api, worker and migrate (INFRASTRUCTURE §5). Secrets come from Key Vault references.
var commonEnv = [
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'Blob__ServiceUri', value: storage.outputs.blobEndpoint }
  { name: 'Blob__UseManagedIdentity', value: 'true' }
  { name: 'AzureAd__TenantId', value: entraTenantId }
  { name: 'AzureAd__ClientId', value: entraClientId }
  { name: 'DocRender__Url', value: 'http://gotenberg' }
  { name: 'Email__Provider', value: 'AzureCommunicationServices' }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: monitoring.outputs.appInsightsConnectionString }
  { name: 'Ai__Enabled', value: 'false' }
  { name: 'Stamp__Id', value: environmentName }
  { name: 'Stamp__LegalEntity', value: stampLegalEntity }
  { name: 'Stamp__Country', value: stampCountry }
]

module gotenberg 'modules/container-app.bicep' = if (deployApps) {
  name: 'app-gotenberg'
  params: {
    location: location
    name: 'gotenberg'
    tags: tags
    environmentId: environment.outputs.id
    identityId: identities.outputs.gotenberg.id
    image: gotenbergImage
    ingress: 'internal'
    targetPort: 3000
    minReplicas: 0
    maxReplicas: gotenbergMaxReplicas
    cpu: '1.0'
    memory: '2Gi'
    liveProbePath: '/health'
    readyProbePath: '/health'
  }
}

module migrateJob 'modules/migrate-job.bicep' = if (deployApps) {
  name: 'job-migrate'
  params: {
    location: location
    name: 'migrate'
    tags: tags
    environmentId: environment.outputs.id
    identityId: identities.outputs.migrate.id
    image: appImage
    registryServer: registry.outputs.loginServer
    secrets: [
      {
        name: 'connectionstrings-migrator'
        keyVaultUrl: keyVault.outputs.migratorSecretUri
        identity: identities.outputs.migrate.id
      }
    ]
    env: concat(commonEnv, [
      { name: 'APP_ROLE', value: 'migrate' }
      { name: 'AZURE_CLIENT_ID', value: identities.outputs.migrate.clientId }
      { name: 'ConnectionStrings__Migrator', secretRef: 'connectionstrings-migrator' }
    ])
  }
}

module worker 'modules/container-app.bicep' = if (deployApps) {
  name: 'app-worker'
  params: {
    location: location
    name: 'worker'
    tags: tags
    environmentId: environment.outputs.id
    identityId: identities.outputs.worker.id
    image: appImage
    registryServer: registry.outputs.loginServer
    ingress: 'none'
    minReplicas: 1
    maxReplicas: 1
    secrets: [
      {
        name: 'connectionstrings-core'
        keyVaultUrl: keyVault.outputs.coreSecretUri
        identity: identities.outputs.worker.id
      }
    ]
    env: concat(commonEnv, [
      { name: 'APP_ROLE', value: 'worker' }
      { name: 'AZURE_CLIENT_ID', value: identities.outputs.worker.clientId }
      { name: 'ConnectionStrings__Core', secretRef: 'connectionstrings-core' }
    ])
  }
}

module api 'modules/container-app.bicep' = if (deployApps) {
  name: 'app-api'
  params: {
    location: location
    name: 'api'
    tags: tags
    environmentId: environment.outputs.id
    identityId: identities.outputs.api.id
    image: appImage
    registryServer: registry.outputs.loginServer
    ingress: 'external'
    minReplicas: apiMinReplicas
    maxReplicas: apiMaxReplicas
    secrets: concat(
      [
        {
          name: 'connectionstrings-core'
          keyVaultUrl: keyVault.outputs.coreSecretUri
          identity: identities.outputs.api.id
        }
      ],
      empty(entraClientId)
        ? []
        : [
            {
              name: 'microsoft-provider-authentication-secret'
              keyVaultUrl: '${keyVault.outputs.uri}secrets/${entraClientSecretName}'
              identity: identities.outputs.api.id
            }
          ]
    )
    env: concat(commonEnv, [
      { name: 'APP_ROLE', value: 'api' }
      { name: 'AZURE_CLIENT_ID', value: identities.outputs.api.clientId }
      { name: 'ConnectionStrings__Core', secretRef: 'connectionstrings-core' }
    ])
    entraAuth: empty(entraClientId)
      ? {}
      : {
          tenantId: entraTenantId
          clientId: entraClientId
          clientSecretName: 'microsoft-provider-authentication-secret'
        }
  }
}

output registryLoginServer string = registry.outputs.loginServer
output apiFqdn string = deployApps ? api!.outputs.fqdn : ''
output migrateJobName string = deployApps ? migrateJob!.outputs.name : ''
output postgresFqdn string = pgHost
output keyVaultUri string = keyVault.outputs.uri
