@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param aca_env_outputs_azure_container_apps_environment_default_domain string

param aca_env_outputs_azure_container_apps_environment_id string

param web_containerimage string

param certificateName string

param customDomain string

param aca_env_outputs_azure_container_registry_endpoint string

param aca_env_outputs_azure_container_registry_managed_identity_id string

resource web 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'web'
  location: location
  properties: {
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8000
        transport: 'http'
        customDomains: [
          {
            name: customDomain
            bindingType: (certificateName != '') ? 'SniEnabled' : 'Disabled'
            certificateId: (certificateName != '') ? '${aca_env_outputs_azure_container_apps_environment_id}/managedCertificates/${certificateName}' : null
          }
        ]
      }
      registries: [
        {
          server: aca_env_outputs_azure_container_registry_endpoint
          identity: aca_env_outputs_azure_container_registry_managed_identity_id
        }
      ]
    }
    environmentId: aca_env_outputs_azure_container_apps_environment_id
    template: {
      containers: [
        {
          image: web_containerimage
          name: 'web'
          env: [
            {
              name: 'NODE_ENV'
              value: 'production'
            }
            {
              name: 'PORT'
              value: '8000'
            }
            {
              name: 'HOSTNAME'
              value: '0.0.0.0'
            }
            {
              name: 'API_HTTP'
              value: 'https://api.internal.${aca_env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__api__http__0'
              value: 'https://api.internal.${aca_env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'API_HTTPS'
              value: 'https://api.internal.${aca_env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'services__api__https__0'
              value: 'https://api.internal.${aca_env_outputs_azure_container_apps_environment_default_domain}'
            }
            {
              name: 'API_URL'
              value: 'https://api.internal.${aca_env_outputs_azure_container_apps_environment_default_domain}'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
      }
    }
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${aca_env_outputs_azure_container_registry_managed_identity_id}': { }
    }
  }
}