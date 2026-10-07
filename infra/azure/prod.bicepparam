// Stage 2: production stamp (INFRASTRUCTURE §8): General Purpose PostgreSQL with zone-redundant HA, geo-redundant
// backups, at least two api replicas. Private endpoints, customer-managed keys, Front Door/WAF and Sentinel are
// separate Stage 2 modules added when their trigger happens.
using 'main.bicep'

param environmentName = 'prod'
param location = 'germanywestcentral'
param regionCode = 'gwc'

param stampLegalEntity = readEnvironmentVariable('STAMP_LEGAL_ENTITY')
param stampCountry = 'GR'

param postgresSkuName = 'Standard_D2ds_v5'
param postgresSkuTier = 'GeneralPurpose'
param postgresStorageGB = 128
param postgresBackupRetentionDays = 35
param postgresHighAvailability = 'ZoneRedundant'
param postgresGeoRedundantBackup = true

param postgresAdminPassword = readEnvironmentVariable('PG_ADMIN_PASSWORD')
param appDbPassword = readEnvironmentVariable('APP_DB_PASSWORD')
param migratorDbPassword = readEnvironmentVariable('MIGRATOR_DB_PASSWORD')

// Set from the PLT retention catalogue before go-live (no default: a regulatory value).
param immutabilityRetentionDays = int(readEnvironmentVariable('IMMUTABILITY_RETENTION_DAYS'))

param apiMinReplicas = 2
param apiMaxReplicas = 6
param gotenbergMaxReplicas = 4

param entraClientId = readEnvironmentVariable('ENTRA_CLIENT_ID')

param logDailyQuotaGb = '5'
param logRetentionDays = 90
