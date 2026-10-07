// Azure Database for PostgreSQL Flexible Server 17, private access only (VNet integration), database `coreins`,
// the same extension set as local (INFRASTRUCTURE §4 rule 3, §6.2).

param location string
param namePrefix string
param tags object

@description('Delegated subnet (snet-db).')
param dbSubnetId string

@description('Private DNS zone for private access.')
param privateDnsZoneId string

@description('SKU name, e.g. Standard_B1ms (Stage 1) or Standard_D2ds_v5 (Stage 2).')
param skuName string = 'Standard_B1ms'

@allowed(['Burstable', 'GeneralPurpose', 'MemoryOptimized'])
param skuTier string = 'Burstable'

param storageSizeGB int = 32
param backupRetentionDays int = 7

@allowed(['Disabled', 'ZoneRedundant', 'SameZone'])
param highAvailability string = 'Disabled'

param geoRedundantBackup bool = false

@description('Server administrator login (bootstrap only; the app uses the `app` and `migrator` roles).')
param administratorLogin string

@secure()
param administratorPassword string

// Same list as infra/local/pg-init (INFRASTRUCTURE §4 rule 3).
var extensions = 'PG_TRGM,UNACCENT,PGCRYPTO,BTREE_GIST,PG_STAT_STATEMENTS'

resource server 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: 'psql-${namePrefix}'
  location: location
  tags: tags
  sku: { name: skuName, tier: skuTier }
  properties: {
    version: '17'
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorPassword
    storage: { storageSizeGB: storageSizeGB, autoGrow: 'Enabled' }
    backup: {
      backupRetentionDays: backupRetentionDays
      geoRedundantBackup: geoRedundantBackup ? 'Enabled' : 'Disabled'
    }
    highAvailability: { mode: highAvailability }
    network: {
      delegatedSubnetResourceId: dbSubnetId
      privateDnsZoneArmResourceId: privateDnsZoneId
      publicNetworkAccess: 'Disabled'
    }
    authConfig: {
      activeDirectoryAuth: 'Disabled'
      passwordAuth: 'Enabled'
    }
  }
}

var parameters = [
  { name: 'azure.extensions', value: extensions }
  { name: 'shared_preload_libraries', value: 'pg_stat_statements' }
  { name: 'require_secure_transport', value: 'on' }
  { name: 'timezone', value: 'UTC' }
  // Statement logging stays off: bootstrap statements carry role credentials (as SCRAM verifiers) and application
  // statements may carry personal data (infra/README.md).
  { name: 'log_statement', value: 'none' }
  { name: 'log_min_duration_statement', value: '-1' }
]

// Server parameters must be applied one at a time.
@batchSize(1)
resource configurations 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = [
  for parameter in parameters: {
    parent: server
    name: parameter.name
    properties: { value: parameter.value, source: 'user-override' }
  }
]

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: server
  name: 'coreins'
  properties: { charset: 'UTF8', collation: 'en_US.utf8' }
  dependsOn: [configurations]
}

output fqdn string = server.properties.fullyQualifiedDomainName
output databaseName string = database.name
