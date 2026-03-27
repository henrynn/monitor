@secure()
param appInsightsConnectionString string
param containerAppEnvironmentName string
param keyVaultName string
param keyVaultSecretName string = 'demo-config'
param keyVaultUri string
param acrName string
param acrLoginServer string
param registryUsername string
@secure()
param registryPassword string
param location string = resourceGroup().location
param prefix string = 'zavademo'
param serverImage string
param storageAccountName string
param storageAccountUrl string
param externalTodoUrl string = 'https://jsonplaceholder.typicode.com/todos/1'
param externalUuidUrl string = 'https://httpbin.org/uuid'

var acrPullRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
var keyVaultSecretsUserRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
var storageBlobDataReaderRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')

resource containerEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: containerAppEnvironmentName
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: acrName
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource serverApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${prefix}-server'
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: containerEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      secrets: [
        {
          name: 'acr-password'
          value: registryPassword
        }
      ]
      registries: [
        {
          server: acrLoginServer
          username: registryUsername
          passwordSecretRef: 'acr-password'
        }
      ]
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
    }
    template: {
      containers: [
        {
          name: 'server'
          image: serverImage
          env: [
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsightsConnectionString
            }
            {
              name: 'EXTERNAL_TODO_URL'
              value: externalTodoUrl
            }
            {
              name: 'EXTERNAL_UUID_URL'
              value: externalUuidUrl
            }
            {
              name: 'KEYVAULT_SECRET_NAME'
              value: keyVaultSecretName
            }
            {
              name: 'KEYVAULT_URI'
              value: keyVaultUri
            }
            {
              name: 'NODE_ENV'
              value: 'production'
            }
            {
              name: 'OTEL_SERVICE_NAME'
              value: 'zava-demo-server'
            }
            {
              name: 'PORT'
              value: '8080'
            }
            {
              name: 'STORAGE_ACCOUNT_URL'
              value: storageAccountUrl
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 2
      }
    }
  }
}

resource acrPullAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, serverApp.id, 'acr-pull')
  scope: acr
  properties: {
    principalId: serverApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: acrPullRoleDefinitionId
  }
}

resource keyVaultSecretsUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, serverApp.id, 'keyvault-secrets-user')
  scope: keyVault
  properties: {
    principalId: serverApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: keyVaultSecretsUserRoleDefinitionId
  }
}

resource storageBlobReaderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, serverApp.id, 'storage-blob-reader')
  scope: storageAccount
  properties: {
    principalId: serverApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageBlobDataReaderRoleDefinitionId
  }
}

output serverAppName string = serverApp.name
output serverUrl string = 'https://${serverApp.properties.configuration.ingress.fqdn}'
