# Azure Deployment Runbook

This runbook covers the Azure hosting path for Greta's Game using the Aspire AppHost in `apphost.cs`.

For environment-specific deployed values and handoff notes, see [`azure-environment-handoff.md`](./azure-environment-handoff.md).

For CI/CD follow-up planning, see [`cicd-next-steps.md`](./cicd-next-steps.md).

## Architecture

- `web`: Next.js app from the sibling `../gretas-game-web` repo, deployed as a public Azure Container App
- `api`: .NET Minimal API, deployed as an internal Azure Container App
- `migrations`: FluentMigrator worker, deployed as an Azure Container Apps Job and run on demand after deployment
- `postgres`: Azure Database for PostgreSQL Flexible Server in Azure, local Postgres container during `aspire run`
- `appinsights`: Azure Application Insights for backend telemetry

Browser traffic stays same-origin:

- users hit the `web` app over HTTPS
- the Next.js route handler proxies `/api/*` requests to `API_URL`
- the browser never calls the API cross-origin

## Prerequisites

- Azure subscription with permission to create:
  - Azure Container Apps
  - Azure Container Apps Jobs
  - Azure Container Registry
  - Azure Database for PostgreSQL Flexible Server
  - Application Insights
  - resource groups
- Azure CLI installed
- Aspire CLI `13.5.x`
- .NET 10 SDK
- Node 22+ and `pnpm`
- both repos checked out side by side:
  - `gretas-game-api`
  - `gretas-game-web`

Install Azure CLI on macOS:

```sh
brew install azure-cli
```

Authenticate:

```sh
az login
az account set --subscription "<subscription-id-or-name>"
```

## Recommended Domain Strategy

Recommended order:

1. Deploy to Azure first using the default Azure hostname.
2. Register or buy the domain.
3. Bind `www.yourdomain.com` to the `web` app.
4. Add the apex/root domain later if you want it.

If your subscription supports it and the TLD is available, buying the domain in Azure keeps billing and DNS management together. If not, any registrar works as long as you can manage DNS records.

## AppHost Parameters

The AppHost accepts these deployment parameters:

- `customDomain`
- `certificateName`

They default to empty strings so the first Azure deployment can succeed before the domain is attached.

For CI, pass them as:

```sh
Parameters__customDomain=www.example.com
Parameters__certificateName=www-example-com
```

Leave both unset or empty for the first deployment.

## Manual Deployment

From the `gretas-game-api` repo root:

```sh
dotnet build GretasGame.slnx
aspire publish --apphost ./apphost.cs --environment Staging --non-interactive -o ./aspire-output
aspire deploy --apphost ./apphost.cs --environment Staging --non-interactive
```

Required Azure settings can be supplied as environment variables:

```sh
export Azure__SubscriptionId="<subscription-id>"
export Azure__Location="eastus2"
export Azure__ResourceGroup="rg-gretas-game-staging"
```

### After Deploy: Run Migrations

The migration worker is published as an Azure Container Apps Job so it does not restart forever like a normal service.

After `aspire deploy`, start the job:

```sh
az containerapp job start \
  --resource-group rg-gretas-game-staging \
  --name migrations
```

Then check execution status:

```sh
az containerapp job execution list \
  --resource-group rg-gretas-game-staging \
  --name migrations \
  --output table
```

Verify the API and web app:

```sh
az containerapp list --resource-group rg-gretas-game-staging --output table
```

Open the `web` app URL and verify gameplay, score submission, and leaderboard reads.

## DNS And HTTPS

### Start with `www`

For `www.example.com`, Azure Container Apps managed certificates need:

- `CNAME` record:
  - host: `www`
  - value: the generated Azure Container App hostname for `web`
- `TXT` record:
  - host: `asuid.www`
  - value: the domain verification code from the `web` app

After the records resolve, bind the hostname and certificate in Azure.

### Optional Apex Domain

For `example.com`, Azure Container Apps uses:

- `A` record:
  - host: `@`
  - value: the Azure Container Apps environment static IP
- `TXT` record:
  - host: `asuid`
  - value: the domain verification code

### CAA Note

If the domain has `CAA` records, allow DigiCert or managed certificate issuance will fail.

## GitHub Actions Setup

The deployment workflow in `.github/workflows/deploy-azure.yml` expects a GitHub Environment named `azure-staging`.

Configure these environment secrets:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`

Configure these environment variables:

- `AZURE_LOCATION`
- `AZURE_RESOURCE_GROUP`
- `AZURE_ENVIRONMENT_NAME`
- `CUSTOM_DOMAIN` (optional at first)
- `CUSTOM_DOMAIN_CERTIFICATE_NAME` (optional at first)

The workflow:

- checks out both repos as siblings
- installs .NET, Node, pnpm, and Aspire CLI
- authenticates to Azure using OIDC
- runs tests
- deploys with `aspire deploy`
- starts the `migrations` job

## Observability

`GretasGame.ServiceDefaults` now enables Azure Monitor export automatically whenever `APPLICATIONINSIGHTS_CONNECTION_STRING` is injected by the AppHost. Local development still works with the Aspire dashboard via OTLP.

## Post-Deploy Checklist

- `web` loads over HTTPS on the Azure default hostname
- score submission works through `/api/*`
- leaderboard reads succeed
- migration job finishes successfully
- API can connect to PostgreSQL
- Application Insights receives traces and logs
- custom domain validates and serves HTTPS after binding
