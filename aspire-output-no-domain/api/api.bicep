@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param aca_env_outputs_azure_container_apps_environment_default_domain string

param aca_env_outputs_azure_container_apps_environment_id string

param api_containerimage string

param api_identity_outputs_id string

param api_containerport string

param postgres_kv_outputs_name string

param postgres_outputs_hostname string

param postgres_username_value string

@secure()
param postgres_password_value string

param appinsights_outputs_appinsightsconnectionstring string

param api_identity_outputs_clientid string

param aca_env_outputs_azure_container_registry_endpoint string

param aca_env_outputs_azure_container_registry_managed_identity_id string

resource postgres_kv 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: postgres_kv_outputs_name
}

resource postgres_kv_connectionstrings__gretasgame 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'connectionstrings--gretasgame'
  parent: postgres_kv
}

resource api 'Microsoft.App/containerApps@2025-10-02-preview' = {
  name: 'api'
  location: location
  properties: {
    configuration: {
      secrets: [
        {
          name: 'connectionstrings--gretasgame'
          identity: api_identity_outputs_id
          keyVaultUrl: postgres_kv_connectionstrings__gretasgame.properties.secretUri
        }
        {
          name: 'gretasgame-uri'
          value: 'postgresql://${uriComponent(postgres_username_value)}:${uriComponent(postgres_password_value)}@${postgres_outputs_hostname}/gretasgame'
        }
        {
          name: 'gretasgame-password'
          value: postgres_password_value
        }
      ]
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: int(api_containerport)
        transport: 'http'
      }
      registries: [
        {
          server: aca_env_outputs_azure_container_registry_endpoint
          identity: aca_env_outputs_azure_container_registry_managed_identity_id
        }
      ]
      runtime: {
        dotnet: {
          autoConfigureDataProtection: true
        }
      }
    }
    environmentId: aca_env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: api_containerimage
          name: 'api'
          env: [
            {
              name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY'
              value: 'in_memory'
            }
            {
              name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
              value: 'true'
            }
            {
              name: 'HTTP_PORTS'
              value: api_containerport
            }
            {
              name: 'ConnectionStrings__gretasgame'
              secretRef: 'connectionstrings--gretasgame'
            }
            {
              name: 'GRETASGAME_HOST'
              value: postgres_outputs_hostname
            }
            {
              name: 'GRETASGAME_PORT'
              value: '5432'
            }
            {
              name: 'GRETASGAME_URI'
              secretRef: 'gretasgame-uri'
            }
            {
              name: 'GRETASGAME_JDBCCONNECTIONSTRING'
              value: 'jdbc:postgresql://${postgres_outputs_hostname}/gretasgame?sslmode=require&authenticationPluginClassName=com.azure.identity.extensions.jdbc.postgresql.AzurePostgresqlAuthenticationPlugin'
            }
            {
              name: 'GRETASGAME_USERNAME'
              value: postgres_username_value
            }
            {
              name: 'GRETASGAME_PASSWORD'
              secretRef: 'gretasgame-password'
            }
            {
              name: 'GRETASGAME_DATABASENAME'
              value: 'gretasgame'
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appinsights_outputs_appinsightsconnectionstring
            }
            {
              name: 'HealthChecks__ExposeHttpEndpoints'
              value: 'true'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: api_identity_outputs_clientid
            }
            {
              name: 'AZURE_TOKEN_CREDENTIALS'
              value: 'ManagedIdentityCredential'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
      }
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${api_identity_outputs_id}': { }
      '${aca_env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}