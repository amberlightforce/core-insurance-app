// Container Apps job `migrate`: manual trigger from the pipeline, same image with APP_ROLE=migrate (INFRASTRUCTURE §1).

param location string
param name string
param tags object
param environmentId string
param identityId string
param image string
param registryServer string

@description('Environment variables: [{ name, value } | { name, secretRef }].')
param env array

@description('Secrets: [{ name, keyVaultUrl, identity }].')
param secrets array

resource job 'Microsoft.App/jobs@2024-03-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 1800
      replicaRetryLimit: 0
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: [{ server: registryServer, identity: identityId }]
      secrets: secrets
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: image
          env: env
          resources: { cpu: json('0.5'), memory: '1Gi' }
        }
      ]
    }
  }
}

output name string = job.name
