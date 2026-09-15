# Deploying Greta's Game

Two supported paths:

1. **Docker Compose on a VPS** (any Linux box with Docker) - fully wired up in this folder.
2. **Aspire publish/deploy to Azure Container Apps** - steps below; needs an Azure subscription.

Both use the same three images (web, api, migrations) that `docker compose up --build` builds locally.

---

## 1. Compose on a VPS

### What you get

```
internet ──443──▶ caddy (TLS, HTTP/3) ──▶ web (Next.js :3000) ──/api/*──▶ api (.NET :8080) ──▶ postgres
                                                      ▲
                                          migrations (runs once, then exits)
```

- Caddy obtains and renews Let's Encrypt certificates automatically for `DOMAIN`.
- Only ports 80/443 are published; the API and database stay on the internal network.
- Postgres data lives in the `pgdata` volume; migrations run on every deploy before the API starts.

### One-time server setup

```sh
# Ubuntu 24.04 example
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker $USER && newgrp docker

sudo mkdir -p /srv/gretas && sudo chown $USER /srv/gretas && cd /srv/gretas
git clone <api-repo-url> gretas-game-api
git clone <web-repo-url> gretas-game-web     # must sit next to the API repo

cd gretas-game-api
cp deploy/.env.example deploy/.env
$EDITOR deploy/.env                          # DOMAIN, POSTGRES_PASSWORD (openssl rand -base64 32)
```

Point a DNS `A`/`AAAA` record for `DOMAIN` at the server and open ports 80 and 443 in the firewall.

### Deploy / update

```sh
cd /srv/gretas/gretas-game-api
git -C ../gretas-game-web pull && git pull
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env up -d --build
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env logs -f migrations api
```

The first start takes a minute while Caddy fetches the certificate; then `https://DOMAIN` serves the game.

### Using prebuilt images instead of building on the server

Each repo ships a GitHub Actions workflow (`.github/workflows/publish-image.yml`) that pushes to GHCR on every push to `main`:

- `ghcr.io/<owner>/gretas-game-web`
- `ghcr.io/<owner>/gretas-game-api`
- `ghcr.io/<owner>/gretas-game-migrations`

Set `WEB_IMAGE`, `API_IMAGE`, `MIGRATIONS_IMAGE` in `deploy/.env`, remove the `build:` blocks (or keep them - `up -d` uses the image when it exists), then:

```sh
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env pull
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env up -d
```

### Backups

```sh
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env exec -T postgres \
  pg_dump -U postgres gretasgame | gzip > gretasgame-$(date +%F).sql.gz
```

Restore with `gunzip -c file.sql.gz | docker compose ... exec -T postgres psql -U postgres gretasgame`.

### Operations cheat-sheet

| Task | Command |
| --- | --- |
| Status | `docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env ps` |
| Logs | `... logs -f web api` |
| Restart one service | `... restart api` |
| Rebuild after a code change | `... up -d --build` |
| Stop everything (keep data) | `... down` |
| Stop and delete the database | `... down -v` (irreversible) |

---

## 2. Aspire → Azure Container Apps

The AppHost (`apphost.cs`) already describes the whole system, so Aspire can publish it. This
needs an Azure subscription and the Azure CLI; it is documented rather than committed because
adding the Azure hosting package changes the AppHost's run-mode requirements.

```sh
# Tools
brew install azure-cli && az login

# 1. Add the Azure Container Apps hosting integration to the AppHost
aspire add azure-appcontainers          # adds #:package Aspire.Hosting.Azure.AppContainers
```

Then in `apphost.cs`, before the resources:

```csharp
builder.AddAzureContainerAppEnvironment("env");
```

and swap the local Postgres for a managed one when you're ready (optional - the container also works on ACA):

```csharp
// #:package Aspire.Hosting.Azure.PostgreSQL
var postgres = builder.AddAzurePostgresFlexibleServer("postgres")
    .RunAsContainer(c => c.WithDataVolume("gretas-game-postgres").WithPgWeb()); // local dev unchanged
```

Publish and deploy:

```sh
aspire publish -o ./publish        # emits Bicep + container image build steps
aspire deploy                      # builds images, pushes to ACR, applies Bicep (prompts for subscription/location)
```

Notes:

- `WithExternalHttpEndpoints()` on `web` becomes the public ingress; the API stays internal, exactly like the compose setup.
- The Next.js image is built from `../gretas-game-web` by the AppHost's `AddNextJsApp` resource, so keep the two repos side by side on the machine (or CI runner) doing the deploy.
- Set `API_URL` is already wired via `api.GetEndpoint("http")`; nothing else to configure.
- Costs: ACA consumption plan + a Basic Postgres Flexible Server is a few dollars a month at hobby scale; scale-to-zero keeps idle cost near zero.
