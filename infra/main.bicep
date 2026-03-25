targetScope = 'resourceGroup'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Short project prefix used to derive resource names. Use letters, numbers, and hyphens only.')
param namePrefix string = 'bobtools'

@description('Tags applied to all supported resources.')
param tags object = {
  project: 'BobTools'
  workload: 'ocr-broker'
}

@description('Publisher display name for API Management.')
param apimPublisherName string

@description('Publisher email for API Management.')
param apimPublisherEmail string

@allowed([
  'Basic'
  'BasicV2'
  'Consumption'
  'Developer'
  'Premium'
  'PremiumV2'
  'Standard'
  'StandardV2'
])
@description('API Management SKU. Developer is a practical starting point for non-production.')
param apimSku string = 'Developer'

@minValue(1)
@description('API Management capacity for non-consumption SKUs.')
param apimSkuCapacity int = 1

@description('Display name for the API Management product exposed to BobTools clients.')
param apimProductDisplayName string = 'BobTools OCR'

@description('Internal name for the API Management product.')
param apimProductName string = 'bobtools-ocr'

@description('Internal name for the BobTools API in API Management.')
param apimApiName string = 'bobtools-ocr-api'

@description('Public base path for the BobTools OCR API in API Management.')
param apimApiPath string = 'ocr'

@description('Display name of the shared API Management subscription that desktop clients use.')
param apimSubscriptionDisplayName string = 'BobTools Desktop Clients'

@description('Internal name of the shared API Management subscription.')
param apimSubscriptionName string = 'bobtools-desktop'

@description('Log Analytics retention in days.')
param logAnalyticsRetentionInDays int = 30

@description('Key Vault secret name that stores the Azure Vision endpoint.')
param azureVisionEndpointSecretName string = 'azure-vision-endpoint'

@description('Key Vault secret name that stores the Azure Vision API key.')
param azureVisionKeySecretName string = 'azure-vision-key'

@description('Key Vault secret name that stores the BobTools shared client key.')
param bobToolsClientKeySecretName string = 'bobtools-client-key'

@description('Azure AI Vision endpoint used by the broker function.')
param azureVisionEndpoint string

@secure()
@description('Azure AI Vision API key stored in Key Vault and consumed by the broker function.')
param azureVisionKey string

@secure()
@description('Primary shared subscription key for BobTools desktop clients in API Management.')
param bobToolsClientPrimaryKey string

@secure()
@description('Optional secondary shared subscription key for BobTools desktop clients in API Management.')
param bobToolsClientSecondaryKey string = ''

var normalizedPrefix = take(replace(toLower(namePrefix), '-', ''), 10)
var uniqueSuffix = toLower(uniqueString(subscription().subscriptionId, resourceGroup().id, namePrefix))

var logAnalyticsName = take('log-${normalizedPrefix}-${uniqueSuffix}', 63)
var appInsightsName = take('appi-${normalizedPrefix}-${uniqueSuffix}', 63)
var storageAccountName = take('${normalizedPrefix}${take(uniqueSuffix, 10)}sa', 24)
var appServicePlanName = take('plan-${normalizedPrefix}-${uniqueSuffix}', 40)
var functionAppName = take('func-${normalizedPrefix}-${uniqueSuffix}', 60)
var keyVaultName = take('kv-${normalizedPrefix}-${take(uniqueSuffix, 8)}', 24)
var apiManagementName = take('apim-${normalizedPrefix}-${take(uniqueSuffix, 8)}', 50)

var apimSubscriptionObject = union({
  displayName: apimSubscriptionDisplayName
  name: apimSubscriptionName
  primaryKey: bobToolsClientPrimaryKey
  scope: '/products/${apimProductName}'
  state: 'active'
}, empty(bobToolsClientSecondaryKey) ? {} : {
  secondaryKey: bobToolsClientSecondaryKey
})

var functionApiPathBase = 'https://${functionApp.outputs.defaultHostname}/api'
var apimApiResourceId = resourceId('Microsoft.ApiManagement/service/apis', apiManagementName, apimApiName)
var keyVaultSecretsUserRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
var functionVisionEndpointSecretUri = '${keyVault.outputs.uri}secrets/${azureVisionEndpointSecretName}'
var functionVisionKeySecretUri = '${keyVault.outputs.uri}secrets/${azureVisionKeySecretName}'
var functionSharedClientKeySecretUri = '${keyVault.outputs.uri}secrets/${bobToolsClientKeySecretName}'

module logAnalytics 'br/public:avm/res/operational-insights/workspace:0.15' = {
  name: 'logAnalytics'
  params: {
    location: location
    name: logAnalyticsName
    retentionInDays: logAnalyticsRetentionInDays
    tags: tags
  }
}

module appInsights 'br/public:avm/res/insights/component:0.7' = {
  name: 'appInsights'
  params: {
    applicationType: 'web'
    location: location
    name: appInsightsName
    tags: tags
    workspaceResourceId: logAnalytics.outputs.resourceId
  }
}

module storage 'br/public:avm/res/storage/storage-account:0.32' = {
  name: 'storage'
  params: {
    allowBlobPublicAccess: false
    location: location
    name: storageAccountName
    publicNetworkAccess: 'Enabled'
    tags: tags
  }
}

module appServicePlan 'br/public:avm/res/web/serverfarm:0.7' = {
  name: 'appServicePlan'
  params: {
    location: location
    name: appServicePlanName
    skuCapacity: 1
    skuName: 'Y1'
    tags: tags
    zoneRedundant: false
  }
}

module keyVault 'br/public:avm/res/key-vault/vault:0.13' = {
  name: 'keyVault'
  params: {
    diagnosticSettings: [
      {
        logCategoriesAndGroups: [
          {
            categoryGroup: 'allLogs'
          }
        ]
        metricCategories: [
          {
            category: 'AllMetrics'
          }
        ]
        name: 'send-to-log-analytics'
        workspaceResourceId: logAnalytics.outputs.resourceId
      }
    ]
    enablePurgeProtection: true
    enableRbacAuthorization: true
    location: location
    name: keyVaultName
    publicNetworkAccess: 'Enabled'
    secrets: [
      {
        name: azureVisionEndpointSecretName
        value: azureVisionEndpoint
      }
      {
        name: azureVisionKeySecretName
        value: azureVisionKey
      }
      {
        name: bobToolsClientKeySecretName
        value: bobToolsClientPrimaryKey
      }
    ]
    sku: 'standard'
    tags: tags
  }
}

module functionApp 'br/public:avm/res/web/site:0.22' = {
  name: 'functionApp'
  params: {
    configs: [
      {
        applicationInsightResourceId: appInsights.outputs.resourceId
        name: 'appsettings'
        properties: {
          AzureVision__ApiKey: '@Microsoft.KeyVault(SecretUri=${functionVisionKeySecretUri})'
          AzureVision__Endpoint: '@Microsoft.KeyVault(SecretUri=${functionVisionEndpointSecretUri})'
          BobTools__SharedAccessKey: '@Microsoft.KeyVault(SecretUri=${functionSharedClientKeySecretUri})'
          FUNCTIONS_EXTENSION_VERSION: '~4'
          FUNCTIONS_WORKER_RUNTIME: 'dotnet-isolated'
        }
        storageAccountResourceId: storage.outputs.resourceId
      }
      {
        name: 'web'
        properties: {
          apiManagementConfig: {
            id: apimApiResourceId
          }
          ftpsState: 'Disabled'
          healthCheckPath: '/api/health'
          http20Enabled: true
          keyVaultReferenceIdentity: 'SystemAssigned'
          minTlsVersion: '1.2'
        }
      }
    ]
    diagnosticSettings: [
      {
        logCategoriesAndGroups: [
          {
            categoryGroup: 'allLogs'
          }
        ]
        metricCategories: [
          {
            category: 'AllMetrics'
          }
        ]
        name: 'send-to-log-analytics'
        workspaceResourceId: logAnalytics.outputs.resourceId
      }
    ]
    hostNamesDisabled: true
    httpsOnly: true
    kind: 'functionapp'
    location: location
    managedIdentities: {
      systemAssigned: true
    }
    name: functionAppName
    serverFarmResourceId: appServicePlan.outputs.resourceId
    tags: tags
  }
}

resource keyVaultResource 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: keyVault.outputs.name
}

resource functionAppKeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.outputs.resourceId, functionApp.outputs.systemAssignedMIPrincipalId, 'key-vault-secrets-user')
  scope: keyVaultResource
  properties: {
    principalId: functionApp.outputs.systemAssignedMIPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: keyVaultSecretsUserRoleDefinitionId
  }
}

module apiManagement 'br/public:avm/res/api-management/service:0.14' = {
  name: 'apiManagement'
  params: {
    apis: [
      {
        displayName: 'BobTools OCR API'
        name: apimApiName
        operations: [
          {
            displayName: 'Health'
            method: 'GET'
            name: 'health'
            urlTemplate: '/health'
          }
          {
            displayName: 'Scan'
            method: 'POST'
            name: 'scan'
            urlTemplate: '/scan'
          }
        ]
        path: apimApiPath
        protocols: [
          'https'
        ]
        serviceUrl: functionApiPathBase
        subscriptionRequired: true
        type: 'http'
      }
    ]
    diagnosticSettings: [
      {
        logCategoriesAndGroups: [
          {
            categoryGroup: 'allLogs'
          }
        ]
        metricCategories: [
          {
            category: 'AllMetrics'
          }
        ]
        name: 'send-to-log-analytics'
        workspaceResourceId: logAnalytics.outputs.resourceId
      }
    ]
    enableDeveloperPortal: false
    location: location
    managedIdentities: {
      systemAssigned: true
    }
    name: apiManagementName
    products: [
      {
        apis: [
          apimApiName
        ]
        description: 'Shared OCR broker product for BobTools desktop clients.'
        displayName: apimProductDisplayName
        name: apimProductName
        state: 'published'
        subscriptionRequired: true
      }
    ]
    publisherEmail: apimPublisherEmail
    publisherName: apimPublisherName
    publicNetworkAccess: 'Enabled'
    sku: apimSku
    skuCapacity: apimSkuCapacity
    subscriptions: [
      apimSubscriptionObject
    ]
    tags: tags
  }
}

output apiManagementName string = apiManagement.outputs.name
output apiManagementGatewayUrl string = 'https://${apiManagement.outputs.name}.azure-api.net/${apimApiPath}'
output functionAppName string = functionApp.outputs.name
output functionAppDefaultHostname string = functionApp.outputs.defaultHostname
output keyVaultName string = keyVault.outputs.name
output keyVaultUri string = keyVault.outputs.uri
output logAnalyticsWorkspaceName string = logAnalytics.outputs.name