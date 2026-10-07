// Virtual network for one stamp (INFRASTRUCTURE §6.2): Container Apps subnet and a delegated PostgreSQL subnet,
// plus the private DNS zone used by PostgreSQL Flexible Server private access.

@description('Azure region.')
param location string

@description('Resource name prefix, e.g. coreins-dev-gwc.')
param namePrefix string

@description('Address space of the virtual network.')
param addressPrefix string = '10.40.0.0/16'

@description('Container Apps subnet (/23 minimum for a workload-profiles environment).')
param appsSubnetPrefix string = '10.40.0.0/23'

@description('PostgreSQL subnet (/27), delegated to Flexible Server.')
param dbSubnetPrefix string = '10.40.2.0/27'

param tags object

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: 'vnet-${namePrefix}'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [addressPrefix]
    }
    subnets: [
      {
        name: 'snet-apps'
        properties: {
          addressPrefix: appsSubnetPrefix
          delegations: [
            {
              name: 'containerapps'
              properties: { serviceName: 'Microsoft.App/environments' }
            }
          ]
          serviceEndpoints: [
            { service: 'Microsoft.Storage' }
            { service: 'Microsoft.KeyVault' }
          ]
        }
      }
      {
        name: 'snet-db'
        properties: {
          addressPrefix: dbSubnetPrefix
          delegations: [
            {
              name: 'postgres'
              properties: { serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers' }
            }
          ]
        }
      }
    ]
  }
}

resource postgresDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: '${namePrefix}.private.postgres.database.azure.com'
  location: 'global'
  tags: tags
}

resource postgresDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: postgresDnsZone
  name: 'link-${vnet.name}'
  location: 'global'
  tags: tags
  properties: {
    registrationEnabled: false
    virtualNetwork: { id: vnet.id }
  }
}

output appsSubnetId string = vnet.properties.subnets[0].id
output dbSubnetId string = vnet.properties.subnets[1].id
output postgresDnsZoneId string = postgresDnsZone.id
