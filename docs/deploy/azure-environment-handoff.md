# Azure Environment Handoff

This document captures the current live Azure staging setup for Greta's Game so you have one place with the deployed values, URLs, DNS settings, and routine operations.

## Current Status

- staging deployment: live
- custom domain: live
- HTTPS on `www.gretagames.com`: working
- database migrations job: working
- Application Insights wiring: enabled
- GitHub Actions deployment workflow: present

## Azure Account And Scope

- tenant ID: `318b3fc2-4e24-4399-a4ac-d2ef0d67802c`
- subscription name: `gretagames`
- subscription ID: `ca8901ad-c957-4c80-97fd-0d007121c4e2`
- region: `eastus2`
- resource group: `rg-gretagames-staging`

## Live URLs

- primary site: `https://www.gretagames.com`
- Azure default web hostname: `https://web.salmoncliff-b786d3be.eastus2.azurecontainerapps.io`
- Aspire dashboard: `https://aspire-dashboard.ext.salmoncliff-b786d3be.eastus2.azurecontainerapps.io`

Notes:

- `web` is public.
- `api` is intentionally internal-only inside Azure Container Apps.
- browser traffic stays same-origin through the Next.js app; `/api/*` is proxied server-side to the internal API.

## Deployed Azure Resources

### Container Apps

- managed environment: `acaenvjkk4c6gbwpolo`
- environment default domain: `salmoncliff-b786d3be.eastus2.azurecontainerapps.io`
- environment static IP: `48.204.253.79`
- web container app: `web`
- api container app: `api`
- migrations job: `migrations`

### Data And Observability

- PostgreSQL flexible server: `postgres-jkk4c6gbwpolo`
- PostgreSQL host: `postgres-jkk4c6gbwpolo.postgres.database.azure.com`
- database name: `gretasgame`
- Application Insights: `appinsights-jkk4c6gbwpolo`
- Azure Container Registry: `acaenvacrjkk4c6gbwpolo`
- Key Vault: `postgreskv-jkk4c6gbwpolo`

### Managed Identities

- API identity: `api_identity-jkk4c6gbwpolo`
- migrations identity: `migrations_identity-jkk4c6gbwpolo`
- environment/ACR identity: `aca_env_mi-jkk4c6gbwpolo`

## Custom Domain Setup

### Active Binding

- bound hostname: `www.gretagames.com`
- managed certificate resource: `www-gretagames-com`
- certificate validation method: `CNAME`

### DNS Records Used

The live `www` binding uses:

- `CNAME`
  - name: `www`
  - value: `web.salmoncliff-b786d3be.eastus2.azurecontainerapps.io`
- `TXT`
  - name: `asuid.www`
  - value: `C6B1F298E5D12F506F80D043E2669B23B522CF8FD6F3F5FD1F0DF27C91B9E394`

### Entra Verification Record

The Microsoft Entra custom-domain verification record that was added at the registrar is:

- `TXT`
  - name: `@`
  - value: `MS=ms66035493`

### Apex Domain

The apex/root domain `gretagames.com` is not bound to the Container App yet.

If you later want the apex domain on Azure Container Apps, use:

- `A`
  - name: `@`
  - value: `48.204.253.79`
- `TXT`
  - name: `asuid`
  - value: `C6B1F298E5D12F506F80D043E2669B23B522CF8FD6F3F5FD1F0DF27C91B9E394`

If you add any `CAA` records, include DigiCert:

```txt
0 issue "digicert.com"
```

## AppHost Deployment Parameters

The AppHost now supports optional domain parameters and only applies custom-domain configuration when both are present.

For this environment, the values are:

```sh
Parameters__customDomain=www.gretagames.com
Parameters__certificateName=www-gretagames-com
```

The current behavior in `apphost.cs` is:

- first deploy works with no domain parameters
- later deploys can safely include the custom domain and certificate name

## Manual Deploy Commands

From the `gretas-game-api` repo:

```sh
dotnet build GretasGame.slnx
dotnet test GretasGame.slnx -c Release
aspire publish --apphost ./apphost.cs --environment Staging --non-interactive -o ./aspire-output
aspire deploy --apphost ./apphost.cs --environment Staging --non-interactive
```

Environment variables used for staging:

```sh
export Azure__CredentialSource="AzureDeveloperCli"
export Azure__TenantId="318b3fc2-4e24-4399-a4ac-d2ef0d67802c"
export Azure__SubscriptionId="ca8901ad-c957-4c80-97fd-0d007121c4e2"
export Azure__Location="eastus2"
export Azure__ResourceGroup="rg-gretagames-staging"
export Azure__AllowResourceGroupCreation="true"

export Parameters__customDomain="www.gretagames.com"
export Parameters__certificateName="www-gretagames-com"
```

If you need a first deploy without the custom domain, leave the two `Parameters__*` values empty or unset.

## Routine Operations

### Start the migrations job manually

```sh
az containerapp job start \
  --resource-group rg-gretagames-staging \
  --name migrations
```

### Check migration executions

```sh
az containerapp job execution list \
  --resource-group rg-gretagames-staging \
  --name migrations \
  --output table
```

### List container apps

```sh
az containerapp list \
  --resource-group rg-gretagames-staging \
  --output table
```

### Check the web app

```sh
curl -I https://www.gretagames.com
curl -I https://web.salmoncliff-b786d3be.eastus2.azurecontainerapps.io
```

## GitHub Actions Deployment Inputs

The workflow file is `.github/workflows/deploy-azure.yml`.

It expects a GitHub Environment named `azure-staging`.

Required environment secrets:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`

Required environment variables:

- `AZURE_LOCATION=eastus2`
- `AZURE_RESOURCE_GROUP=rg-gretagames-staging`
- `AZURE_ENVIRONMENT_NAME=Staging`
- `CUSTOM_DOMAIN=www.gretagames.com`
- `CUSTOM_DOMAIN_CERTIFICATE_NAME=www-gretagames-com`

Optional helper secret if the web repo is private:

- `WEB_REPO_READ_TOKEN`

## Verification Checklist

When validating the environment after a deploy:

- `https://www.gretagames.com` returns `200`
- `https://web.salmoncliff-b786d3be.eastus2.azurecontainerapps.io` returns `200`
- the `migrations` job completes successfully
- the API remains internal-only
- the site can submit and read scores through `/api/*`
- Application Insights receives telemetry

## Notes For Future Changes

- The current setup is a staging environment even though the custom domain is live.
- If you want a separate production environment later, create a second resource group and managed environment rather than reusing the staging one.
- The safest next operational improvement is to finish the GitHub OIDC deployment path and make the GitHub Actions workflow the normal deployment route instead of local manual deploys.
