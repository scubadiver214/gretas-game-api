@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param applicationType string = 'web'

param kind string = 'web'

resource law_appinsights 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: take('lawappinsights-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
  }
  tags: {
    'aspire-resource-name': 'law_appinsights'
  }
}

resource appinsights 'Microsoft.Insights/components@2020-02-02' = {
  name: take('appinsights-${uniqueString(resourceGroup().id)}', 260)
  kind: kind
  location: location
  properties: {
    Application_Type: applicationType
    WorkspaceResourceId: law_appinsights.id
  }
  tags: {
    'aspire-resource-name': 'appinsights'
  }
}

output appInsightsConnectionString string = appinsights.properties.ConnectionString

output name string = appinsights.name

output id string = appinsights.id