// One Container App (api, worker or gotenberg). Health probes use /health/live and /health/ready when enabled.

param location string
param name string
param tags object
param environmentId string

@description('User-assigned identity resource ID (registry pull, Key Vault references, Azure SDK credential).')
param identityId string

param image string

@description('Registry login server; empty for public images.')
param registryServer string = ''

@allowed(['external', 'internal', 'none'])
param ingress string

param targetPort int = 8080
param minReplicas int
param maxReplicas int

@description('vCPU as a string, e.g. "0.5".')
param cpu string = '0.5'
param memory string = '1Gi'

@description('Environment variables: [{ name, value } | { name, secretRef }].')
param env array = []

@description('Secrets: [{ name, keyVaultUrl, identity }].')
param secrets array = []

@description('Probe paths, or empty to use the platform defaults.')
param liveProbePath string = '/health/live'
param readyProbePath string = '/health/ready'

@description('Container Apps built-in authentication (Entra ID). Empty object disables it. Keys: tenantId, clientId, clientSecretName.')
param entraAuth object = {}

var probes = empty(liveProbePath)
  ? []
  : [
      {
        type: 'Liveness'
        httpGet: { path: liveProbePath, port: targetPort }
        periodSeconds: 10
        failureThreshold: 3
      }
      {
        type: 'Readiness'
        httpGet: { path: readyProbePath, port: targetPort }
        periodSeconds: 10
        failureThreshold: 3
      }
    ]

resource app 'Microsoft.App/containerApps@2024-03-01' = {
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
      activeRevisionsMode: 'Single'
      ingress: ingress == 'none'
        ? null
        : {
            external: ingress == 'external'
            targetPort: targetPort
            transport: 'auto'
            allowInsecure: false
          }
      registries: empty(registryServer) ? [] : [{ server: registryServer, identity: identityId }]
      secrets: secrets
    }
    template: {
      containers: [
        {
          name: name
          image: image
          env: env
          resources: { cpu: json(cpu), memory: memory }
          probes: probes
        }
      ]
      scale: { minReplicas: minReplicas, maxReplicas: maxReplicas }
    }
  }
}

resource auth 'Microsoft.App/containerApps/authConfigs@2024-03-01' = if (!empty(entraAuth)) {
  parent: app
  name: 'current'
  properties: {
    platform: { enabled: true }
    globalValidation: {
      unauthenticatedClientAction: 'RedirectToLoginPage'
      redirectToProvider: 'azureactivedirectory'
      excludedPaths: [liveProbePath, readyProbePath]
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: entraAuth.?clientId ?? ''
          clientSecretSettingName: entraAuth.?clientSecretName ?? ''
          openIdIssuer: '${environment().authentication.loginEndpoint}${entraAuth.?tenantId ?? ''}/v2.0'
        }
        validation: {
          allowedAudiences: ['api://${entraAuth.?clientId ?? ''}']
        }
      }
    }
  }
}

output fqdn string = ingress == 'none' ? '' : app.properties.configuration.ingress.fqdn
