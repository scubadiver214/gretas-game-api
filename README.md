# Greta's Game for fun - API, database and orchestration

.NET 10 backend for [Greta's Game for fun](../gretas-game-web): a Minimal API over PostgreSQL with FluentMigrator migrations, plus the Aspire AppHost that runs the whole game (database, migrations, API and the Next.js web app) locally, and a docker-compose file for running it all in containers.

## Layout

```
apphost.cs                        Aspire AppHost (single-file): Postgres + pgweb, migrations, api, web
src/
  GretasGame.Api/                 Minimal API + Dapper (scores endpoints), Dockerfile
  GretasGame.Migrations/          FluentMigrator migrations (players, scores)
  GretasGame.MigrationService/    Worker that creates the DB if needed and runs MigrateUp, then exits; Dockerfile
  GretasGame.ServiceDefaults/     Aspire service defaults (OpenTelemetry, health checks, resilience)
tests/
  GretasGame.Api.Tests/           xUnit tests for validation rules (no database needed)
docker-compose.yml                postgres + migrations + api + web (built from ../gretas-game-web)
```

The web repo must be checked out as a sibling directory: `../gretas-game-web`.

## Requirements

- .NET 10 SDK (pinned in `global.json`)
- [Aspire CLI](https://aspire.dev) 13.x
- Docker (Aspire uses it for the Postgres container; compose uses it for everything)
- Node 22 + pnpm (for the web app when running under Aspire)

## Dev loop with Aspire

```sh
aspire run
```

The dashboard link is printed in the terminal. Resources:

| Resource     | What it is                                              |
| ------------ | ------------------------------------------------------- |
| `postgres`   | PostgreSQL 17 container with a data volume, plus pgweb   |
| `migrations` | Runs FluentMigrator `MigrateUp` against `gretasgame`, then exits |
| `api`        | Minimal API on `http://localhost:5080`, waits for migrations |
| `web`        | Next.js app (pnpm), `API_URL` injected from the api endpoint |

Open the `web` endpoint from the dashboard. To play on a phone, open the same port on your machine's LAN IP.

## Docker compose

```sh
docker compose up --build
```

Then open <http://localhost:3000>. The API is also exposed on <http://localhost:5081> (a different port from Aspire so both can run at once). Set `POSTGRES_PASSWORD` in a `.env` file to override the dev default.

## API

| Method | Route                            | Notes                                                        |
| ------ | -------------------------------- | ------------------------------------------------------------ |
| POST   | `/api/scores`                    | `{ nickname, character, mode, score, durationSeconds }` -> upserts the player, inserts the score, returns `{ id, rank, isPersonalBest }` |
| GET    | `/api/scores/top?mode=kitchen&limit=20` | Ranked leaderboard for a mode (`kitchen` or `delivery`) |
| GET    | `/api/players/{nickname}/best`   | Personal best per mode for a nickname                        |
| GET    | `/health`, `/alive`              | Health checks (development only, from ServiceDefaults)       |
| GET    | `/openapi/v1.json`               | OpenAPI document (development only)                          |

Validation lives in `Scores/ScoreRules.cs` and mirrors the client: nickname 2-16 characters (letters, digits, spaces, `_` `'` `-`), character `boy`/`girl`, mode `kitchen`/`delivery`, score 0-300 (kitchen) or 0-3000 (delivery), duration 0-600 s, `limit` at most 100.

## Database

Migrations are plain FluentMigrator classes in `GretasGame.Migrations`:

- `M202609150001_CreatePlayers` - `players(id uuid, nickname, nickname_normalized unique, character, created_at)`
- `M202609150002_CreateScores` - `scores(id uuid, player_id fk, mode, score, duration_seconds, created_at)` + indexes for leaderboard queries

Add a new migration by dropping a class with the next `[Migration(yyyyMMddNNNN)]` number into that project; the migration service applies it on the next start.

## Build and test

```sh
dotnet build
dotnet test
```
