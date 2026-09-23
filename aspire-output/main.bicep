targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

param postgres_username string = 'XJZfQZaVjU'

@secure()
param postgres_password string

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module appinsights 'appinsights/appinsights.bicep' = {
  name: 'appinsights'
  scope: rg
  params: {
    location: location
  }
}

module aca_env_acr 'aca-env-acr/aca-env-acr.bicep' = {
  name: 'aca-env-acr'
  scope: rg
  params: {
    location: location
  }
}

module aca_env 'aca-env/aca-env.bicep' = {
  name: 'aca-env'
  scope: rg
  params: {
    location: location
    aca_env_acr_outputs_name: aca_env_acr.outputs.name
    userPrincipalId: principalId
  }
}

module postgres 'postgres/postgres.bicep' = {
  name: 'postgres'
  scope: rg
  params: {
    location: location
    administratorLogin: postgres_username
    administratorLoginPassword: postgres_password
    postgres_kv_outputs_name: postgres_kv.outputs.name
  }
}

module postgres_kv 'postgres-kv/postgres-kv.bicep' = {
  name: 'postgres-kv'
  scope: rg
  params: {
    location: location
  }
}

module migrations_identity 'migrations-identity/migrations-identity.bicep' = {
  name: 'migrations-identity'
  scope: rg
  params: {
    location: location
  }
}

module migrations_roles_postgres_kv 'migrations-roles-postgres-kv/migrations-roles-postgres-kv.bicep' = {
  name: 'migrations-roles-postgres-kv'
  scope: rg
  params: {
    location: location
    postgres_kv_outputs_name: postgres_kv.outputs.name
    principalId: migrations_identity.outputs.principalId
  }
}

module api_identity 'api-identity/api-identity.bicep' = {
  name: 'api-identity'
  scope: rg
  params: {
    location: location
  }
}

module api_roles_postgres_kv 'api-roles-postgres-kv/api-roles-postgres-kv.bicep' = {
  name: 'api-roles-postgres-kv'
  scope: rg
  params: {
    location: location
    postgres_kv_outputs_name: postgres_kv.outputs.name
    principalId: api_identity.outputs.principalId
  }
}

output aca_env_AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = aca_env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN

output aca_env_AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = aca_env.outputs.AZURE_CONTAINER_APPS_ENVIRONMENT_ID

output aca_env_AZURE_CONTAINER_REGISTRY_ENDPOINT string = aca_env.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT

output aca_env_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = aca_env.outputs.AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID

output migrations_identity_id string = migrations_identity.outputs.id

output postgres_kv_name string = postgres_kv.outputs.name

output postgres_kv_vaultUri string = postgres_kv.outputs.vaultUri

output postgres_hostName string = postgres.outputs.hostName

output appinsights_appInsightsConnectionString string = appinsights.outputs.appInsightsConnectionString

output migrations_identity_clientId string = migrations_identity.outputs.clientId

output api_identity_id string = api_identity.outputs.id

output api_identity_clientId string = api_identity.outputs.clientId