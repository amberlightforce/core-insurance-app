// Stage 1: Azure dev/pilot, minimum cost, synthetic data only (INFRASTRUCTURE §6).
using 'main.bicep'

param environmentName = 'dev'
param location = 'germanywestcentral'
param regionCode = 'gwc'

param stampLegalEntity = 'GR-TEST'
param stampCountry = 'GR'

param postgresSkuName = 'Standard_B1ms'
param postgresSkuTier = 'Burstable'
param postgresStorageGB = 32
param postgresBackupRetentionDays = 7
param postgresHighAvailability = 'Disabled'
param postgresGeoRedundantBackup = false

// Secrets are read from the pipeline environment, never committed.
param postgresAdminPassword = readEnvironmentVariable('PG_ADMIN_PASSWORD')
param appDbPassword = readEnvironmentVariable('APP_DB_PASSWORD')
param migratorDbPassword = readEnvironmentVariable('MIGRATOR_DB_PASSWORD')

// Synthetic data only: a short retention keeps test documents removable.
param immutabilityRetentionDays = 1

param apiMinReplicas = 0
param apiMaxReplicas = 3
param gotenbergMaxReplicas = 2

param entraClientId = readEnvironmentVariable('ENTRA_CLIENT_ID')

param logDailyQuotaGb = '0.15'
param logRetentionDays = 30
