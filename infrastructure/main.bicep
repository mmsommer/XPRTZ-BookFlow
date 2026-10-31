targetScope = 'resourceGroup'

@description('Lowercase prefix used for resource names. Keep it short enough for Azure naming limits.')
@minLength(3)
@maxLength(10)
param namePrefix string = 'bookflow'

@description('Object ID of the Microsoft Entra security group that administers the SQL database.')
param sqlAdministratorObjectId string

@description('Display name of the Microsoft Entra SQL administrator group.')
param sqlAdministratorName string

@description('Optional HTTPS endpoint for the secured notification API. It must support the Event Grid validation handshake.')
@secure()
param notificationWebhookUrl string = ''

@description('Initial and minimum AKS system node count.')
@minValue(2)
@maxValue(5)
param aksNodeCount int = 2

@description('VM size used by the AKS system node pool.')
param aksNodeVmSize string = 'Standard_D2s_v5'

var suffix = uniqueString(resourceGroup().id)
var location = resourceGroup().location
var eventGridWorkerEventTypes = [
  'ManuscriptUploaded'
  'EditorAssigned'
  'ManuscriptEdited'
  'ManuscriptRevised'
  'ManuscriptApproved'
  'PrintOrderRequested'
  'BooksPrinted'
  'BooksDelivered'
  'BooksDistributed'
  'BooksSold'
  'StockUpdated'
  'LowStockAlert'
  'StockDepleted'
]
var notificationEventTypes = [
  'EditorAssigned'
  'ProofprintCreated'
  'ManuscriptEdited'
  'ManuscriptRevised'
  'ManuscriptApproved'
  'BookAvailableForSale'
  'LowStockAlert'
  'StockDepleted'
]
var privateDnsZoneNames = [
  #disable-next-line no-hardcoded-env-urls
  'privatelink.blob.core.windows.net'
  #disable-next-line no-hardcoded-env-urls
  'privatelink.database.windows.net'
  'privatelink.servicebus.windows.net'
  'privatelink.vaultcore.azure.net'
]

resource virtualNetwork 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: '${namePrefix}-vnet'
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.20.0.0/16'
      ]
    }
  }
}

resource aksSubnet 'Microsoft.Network/virtualNetworks/subnets@2023-11-01' = {
  parent: virtualNetwork
  name: 'aks'
  properties: {
    addressPrefix: '10.20.0.0/20'
  }
}

resource privateEndpointsSubnet 'Microsoft.Network/virtualNetworks/subnets@2023-11-01' = {
  parent: virtualNetwork
  name: 'private-endpoints'
  properties: {
    addressPrefix: '10.20.16.0/24'
    privateEndpointNetworkPolicies: 'Disabled'
  }
}

resource privateDnsZones 'Microsoft.Network/privateDnsZones@2020-06-01' = [for zoneName in privateDnsZoneNames: {
  name: zoneName
  location: 'global'
}]

resource privateDnsZoneVnetLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for (zoneName, index) in privateDnsZoneNames: {
  parent: privateDnsZones[index]
  name: '${namePrefix}-${replace(zoneName, '.', '-')}-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: virtualNetwork.id
    }
  }
}]

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: toLower(replace('${namePrefix}${suffix}', '-', ''))
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Disabled'
    supportsHttpsTrafficOnly: true
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Deny'
      ipRules: []
      virtualNetworkRules: []
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource manuscriptContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'manuscripts'
  properties: {
    publicAccess: 'None'
  }
}

resource proofprintsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'proofprints'
  properties: {
    publicAccess: 'None'
  }
}

resource eventGridDeadLetterContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'event-grid-dead-letter'
  properties: {
    publicAccess: 'None'
  }
}

resource sqlServer 'Microsoft.Sql/servers@2022-05-01-preview' = {
  name: '${namePrefix}-sql-${suffix}'
  location: location
  properties: {
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Group'
      login: sqlAdministratorName
      sid: sqlAdministratorObjectId
      tenantId: tenant().tenantId
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
  }
}

resource sqlAzureAdOnlyAuthentication 'Microsoft.Sql/servers/azureADOnlyAuthentications@2022-05-01-preview' = {
  parent: sqlServer
  name: 'Default'
  properties: {
    azureADOnlyAuthentication: true
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2022-05-01-preview' = {
  parent: sqlServer
  name: 'BookFlow'
  location: location
  sku: {
    name: 'S0'
    tier: 'Standard'
    capacity: 10
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 268435456000
    zoneRedundant: false
  }
}

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: '${namePrefix}-sb-${suffix}'
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
  }
}

resource serviceBusTopic 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: serviceBusNamespace
  name: 'bookflow-events'
  properties: {
    defaultMessageTimeToLive: 'P14D'
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    maxSizeInMegabytes: 1024
    requiresDuplicateDetection: true
    supportOrdering: true
  }
}

var serviceSubscriptions = [
  {
    name: 'editor-assignment'
    filter: '''
eventType = 'ManuscriptUploaded'
'''
  }
  {
    name: 'proofprint-generation'
    filter: '''
eventType IN ('ManuscriptUploaded', 'ManuscriptEdited', 'ManuscriptRevised')
'''
  }
  {
    name: 'print-demand-orders'
    filter: '''
eventType IN ('ManuscriptApproved', 'PrintOrderRequested')
'''
  }
  {
    name: 'progress-tracking'
    filter: '''
eventType IN ('ManuscriptUploaded', 'EditorAssigned', 'ManuscriptEdited', 'ManuscriptRevised', 'ManuscriptApproved')
'''
  }
  {
    name: 'inventory'
    filter: '''
eventType IN ('BooksPrinted', 'BooksDelivered', 'BooksDistributed', 'BooksSold', 'StockUpdated', 'LowStockAlert', 'StockDepleted')
'''
  }
]

resource serviceBusSubscriptions 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = [for subscription in serviceSubscriptions: {
  parent: serviceBusTopic
  name: subscription.name
  properties: {
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P14D'
    maxDeliveryCount: 10
  }
}]

resource serviceBusSubscriptionRules 'Microsoft.ServiceBus/namespaces/topics/subscriptions/rules@2024-01-01' = [for (subscription, index) in serviceSubscriptions: {
  parent: serviceBusSubscriptions[index]
  name: '$Default'
  properties: {
    filterType: 'SqlFilter'
    sqlFilter: {
      sqlExpression: subscription.filter
    }
  }
}]

resource eventGridTopic 'Microsoft.EventGrid/topics@2022-06-15' = {
  name: '${namePrefix}-events-${suffix}'
  location: location
  properties: {
    inputSchema: 'EventGridSchema'
    publicNetworkAccess: 'Enabled'
  }
}

resource appIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-workload'
  location: location
}

resource eventGridIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-eventgrid'
  location: location
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: toLower(replace('${namePrefix}acr${suffix}', '-', ''))
  location: location
  sku: {
    name: 'Standard'
  }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: take(toLower('${namePrefix}-kv-${suffix}'), 24)
  location: location
  properties: {
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
    publicNetworkAccess: 'Disabled'
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: tenant().tenantId
  }
}

resource aksCluster 'Microsoft.ContainerService/managedClusters@2024-05-01' = {
  name: '${namePrefix}-aks'
  location: location
  sku: {
    name: 'Base'
    tier: 'Free'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    dnsPrefix: '${namePrefix}-${suffix}'
    enableRBAC: true
    oidcIssuerProfile: {
      enabled: true
    }
    aadProfile: {
      managed: true
      enableAzureRBAC: true
      tenantID: tenant().tenantId
    }
    apiServerAccessProfile: {
      enablePrivateCluster: true
      enablePrivateClusterPublicFQDN: false
      privateDNSZone: 'system'
    }
    agentPoolProfiles: [
      {
        name: 'system'
        mode: 'System'
        count: aksNodeCount
        minCount: aksNodeCount
        maxCount: 5
        enableAutoScaling: true
        vmSize: aksNodeVmSize
        osType: 'Linux'
        osSKU: 'Ubuntu'
        type: 'VirtualMachineScaleSets'
        maxPods: 30
        vnetSubnetID: aksSubnet.id
      }
    ]
    securityProfile: {
      workloadIdentity: {
        enabled: true
      }
    }
    networkProfile: {
      networkPlugin: 'azure'
      networkPluginMode: 'overlay'
      networkPolicy: 'azure'
      loadBalancerSku: 'standard'
      outboundType: 'loadBalancer'
      serviceCidr: '10.2.0.0/16'
      dnsServiceIP: '10.2.0.10'
    }
  }
}

resource appFederatedCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: appIdentity
  name: 'bookflow-workloads'
  properties: {
    audiences: [
      'api://AzureADTokenExchange'
    ]
    issuer: aksCluster.properties.oidcIssuerProfile.issuerURL
    subject: 'system:serviceaccount:bookflow:bookflow'
  }
}

var privateEndpointDefinitions = [
  {
    name: 'blob'
    resourceId: storageAccount.id
    groupId: 'blob'
    privateDnsZoneId: privateDnsZones[0].id
  }
  {
    name: 'sql'
    resourceId: sqlServer.id
    groupId: 'sqlServer'
    privateDnsZoneId: privateDnsZones[1].id
  }
  {
    name: 'servicebus'
    resourceId: serviceBusNamespace.id
    groupId: 'namespace'
    privateDnsZoneId: privateDnsZones[2].id
  }
  {
    name: 'keyvault'
    resourceId: keyVault.id
    groupId: 'vault'
    privateDnsZoneId: privateDnsZones[3].id
  }
]

resource privateEndpoints 'Microsoft.Network/privateEndpoints@2023-11-01' = [for endpoint in privateEndpointDefinitions: {
  name: '${namePrefix}-${endpoint.name}-pe'
  location: location
  properties: {
    subnet: {
      id: privateEndpointsSubnet.id
    }
    privateLinkServiceConnections: [
      {
        name: '${namePrefix}-${endpoint.name}-connection'
        properties: {
          privateLinkServiceId: endpoint.resourceId
          groupIds: [
            endpoint.groupId
          ]
        }
      }
    ]
  }
}]

resource privateEndpointDnsZoneGroups 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = [for (endpoint, index) in privateEndpointDefinitions: {
  parent: privateEndpoints[index]
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'privatelink'
        properties: {
          privateDnsZoneId: endpoint.privateDnsZoneId
        }
      }
    ]
  }
}]

resource storageBlobRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, appIdentity.id, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: storageAccount
  properties: {
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}

resource eventGridSenderRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(eventGridTopic.id, appIdentity.id, 'd5a91429-5739-47e2-a06b-3470a27159e7')
  scope: eventGridTopic
  properties: {
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'd5a91429-5739-47e2-a06b-3470a27159e7')
  }
}

resource serviceBusSenderRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, appIdentity.id, '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
  scope: serviceBusNamespace
  properties: {
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
  }
}

resource serviceBusReceiverRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, appIdentity.id, '4f6b6d84-a4a0-4b3b-8f1b-efcbeab7e2c6')
  scope: serviceBusNamespace
  properties: {
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6b6d84-a4a0-4b3b-8f1b-efcbeab7e2c6')
  }
}

resource keyVaultSecretsRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, appIdentity.id, '4633458b-17de-408a-b874-0445c86b69e6')
  scope: keyVault
  properties: {
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}

resource eventGridServiceBusRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusTopic.id, eventGridIdentity.id, '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
  scope: serviceBusTopic
  properties: {
    principalId: eventGridIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
  }
}

resource eventGridDeadLetterRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, eventGridIdentity.id, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: storageAccount
  properties: {
    principalId: eventGridIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}

resource aksAcrPullRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, aksCluster.id, '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  scope: registry
  properties: {
    principalId: aksCluster.properties.identityProfile.kubeletidentity.objectId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}

resource workerEventSubscription 'Microsoft.EventGrid/topics/eventSubscriptions@2022-06-15' = {
  parent: eventGridTopic
  name: 'bookflow-worker-routing'
  properties: {
    deliveryWithResourceIdentity: {
      destination: {
        endpointType: 'ServiceBusTopic'
        properties: {
          resourceId: serviceBusTopic.id
          deliveryAttributeMappings: [
            {
              name: 'eventType'
              type: 'Dynamic'
              properties: {
                sourceField: 'eventType'
              }
            }
            {
              name: 'MessageId'
              type: 'Dynamic'
              properties: {
                sourceField: 'id'
              }
            }
          ]
        }
      }
      identity: {
        type: 'UserAssigned'
        userAssignedIdentity: eventGridIdentity.id
      }
    }
    deadLetterWithResourceIdentity: {
      deadLetterDestination: {
        endpointType: 'StorageBlob'
        properties: {
          resourceId: storageAccount.id
          blobContainerName: 'event-grid-dead-letter'
        }
      }
      identity: {
        type: 'UserAssigned'
        userAssignedIdentity: eventGridIdentity.id
      }
    }
    eventDeliverySchema: 'EventGridSchema'
    filter: {
      includedEventTypes: eventGridWorkerEventTypes
    }
    retryPolicy: {
      maxDeliveryAttempts: 10
      eventTimeToLiveInMinutes: 1440
    }
  }
  dependsOn: [
    eventGridServiceBusRoleAssignment
    eventGridDeadLetterRoleAssignment
    eventGridDeadLetterContainer
  ]
}

resource notificationEventSubscription 'Microsoft.EventGrid/topics/eventSubscriptions@2022-06-15' = if (!empty(notificationWebhookUrl)) {
  parent: eventGridTopic
  name: 'bookflow-notifications'
  properties: {
    destination: {
      endpointType: 'WebHook'
      properties: {
        endpointUrl: notificationWebhookUrl
      }
    }
    deadLetterWithResourceIdentity: {
      deadLetterDestination: {
        endpointType: 'StorageBlob'
        properties: {
          resourceId: storageAccount.id
          blobContainerName: 'event-grid-dead-letter'
        }
      }
      identity: {
        type: 'UserAssigned'
        userAssignedIdentity: eventGridIdentity.id
      }
    }
    eventDeliverySchema: 'EventGridSchema'
    filter: {
      includedEventTypes: notificationEventTypes
    }
    retryPolicy: {
      maxDeliveryAttempts: 10
      eventTimeToLiveInMinutes: 1440
    }
  }
  dependsOn: [
    eventGridDeadLetterRoleAssignment
    eventGridDeadLetterContainer
  ]
}

output aksClusterName string = aksCluster.name
output aksOidcIssuerUrl string = aksCluster.properties.oidcIssuerProfile.issuerURL
output appWorkloadClientId string = appIdentity.properties.clientId
output appWorkloadServiceAccount string = 'system:serviceaccount:bookflow:bookflow'
output containerRegistryName string = registry.name
output eventGridTopicEndpoint string = eventGridTopic.properties.endpoint
output eventGridTopicName string = eventGridTopic.name
output keyVaultName string = keyVault.name
output sqlDatabaseName string = sqlDatabase.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output storageAccountName string = storageAccount.name
output serviceBusNamespaceName string = serviceBusNamespace.name
output serviceBusTopicName string = serviceBusTopic.name
output serviceBusSubscriptionNames array = [for subscription in serviceSubscriptions: subscription.name]
