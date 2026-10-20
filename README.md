# Greta's Game for fun - API, database and orchestration

.NET 10 backend for [Greta's Game for fun](../gretas-game-web): a Minimal API over PostgreSQL with FluentMigrator migrations, plus the Aspire AppHost that runs the whole game (database, migrations, API and the Next.js web app) locally, and a docker-compose file for running it all in containers.

## Layout

```
apphost.cs                        Aspire AppHost (single-file): Postgres + pgweb, migrations, api, web
src/
  GretasGame.Api/                 Minimal API + Dapper (scores + game-session endpoints), Dockerfile
  GretasGame.Migrations/          FluentMigrator migrations (players, scores, game_sessions)
  GretasGame.MigrationService/    Worker that creates the DB if needed and runs MigrateUp, then exits; Dockerfile
  GretasGame.ServiceDefaults/     Aspire service defaults (OpenTelemetry, health checks, resilience)
tests/
  GretasGame.Api.Tests/           xUnit: validation rules, plus Integration/ tests against a real
                                  Postgres container (Testcontainers; needs Docker)
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
| POST   | `/api/scores`                    | `{ nickname, character, color?, mode, score, level, durationSeconds, challengeDate? }` -> upserts the player (and colour), inserts the score, returns `{ id, rank, isPersonalBest }`. With `challengeDate` the rank is on that day's board (best per player). |
| GET    | `/api/scores/top?mode=kitchen&limit=20` | Ranked all-time leaderboard for a mode (`kitchen`, `delivery`, or `wordle`), with each player's colour and level |
| GET    | `/api/scores/top?mode=kitchen&date=2026-09-15` | Daily-challenge board for that UTC date: one entry (best score) per player |
| GET    | `/api/players/{nickname}/best`   | Personal best per mode for a nickname                        |
| POST   | `/api/games/{game}/sessions`     | `{ nickname, character, color?, challengeDate? }` -> upserts the player and opens a session, returns `201 { sessionId, startedAt }`. Only `frogger` uses sessions today. |
| POST   | `/api/games/{game}/sessions/{id}/complete` | `{ nickname, score, highestLevel, levelsCompleted, livesLost, durationSeconds }` -> validates, closes the session and inserts one `scores` row linked to it; returns `{ rank, isPersonalBest, previousBest }`. An identical retry returns the same answer; different results for a finished session are `409`, someone else's session `403`, unknown `404`, older than 4 hours `410`. |
| GET    | `/api/games/{game}/players/{nickname}/stats` | `{ gamesPlayed, highestScore, highestLevel, totalScore, lastPlayedAt }` over finished sessions |
| GET    | `/api/games/{game}/players/{nickname}/history?limit=20` | Most recent finished sessions, newest first (limit 1-100) |
| GET    | `/health`, `/alive`              | Health checks (development only, from ServiceDefaults)       |
| GET    | `/openapi/v1.json`               | OpenAPI document (development only)                          |

Validation lives in `Scores/ScoreRules.cs` and mirrors the client: nickname 2-16 characters (letters, digits, spaces, `_` `'` `-`), character `boy`/`girl`, optional colour `#rrggbb`, mode `kitchen`/`delivery`/`wordle` for `POST /api/scores` (and also `frogger` for the leaderboard reads), level 1-5, score 0-1000 (kitchen), 0-5000 (delivery) or 0-800 (wordle), duration 0-600 s, `challengeDate` as `yyyy-MM-dd` and no older than yesterday (UTC), `limit` at most 100.

All leaderboards rank by score (highest first), then whoever got there first, then id, so ranks never tie.

### Game sessions (Frogger)

Frogger runs entirely in the browser and only talks to the API at the start and end of a game. `POST /api/scores` rejects `mode=frogger`; its scores can only come from a completed session, so the server can check them against its own clock. `Games/FroggerRules.cs` mirrors the web game's `config.ts` / `levelConfig.ts` and rejects results that are impossible:

- scores that aren't multiples of 10, or are higher than the cleared levels allow (every home at full marks plus forward points on every lost life);
- `highestLevel` other than `levelsCompleted + 1`, or more lives lost than 3 plus one per 10,000 points;
- a client play time longer than the session has been open (+5 s grace), or levels cleared faster than five frogs can hop the board.

Session writes are rate limited to 30 requests per minute per client (first `X-Forwarded-For` hop, else the socket address; `RateLimits:GameSessions:PermitLimit` overrides it). `Games/SessionGames.cs` is the registry: another arcade game needs a rules class there and its mode added to the `game_sessions` / `scores` check constraints.

Known limitations: nicknames aren't authenticated, so ownership checks are only as strong as nickname identity (the same as the other games); a modified client can still post any result that passes the plausibility checks; sessions that are never completed stay `active` (they're ignored, and indexed for a future cleanup job).

## Database

Migrations are plain FluentMigrator classes in `GretasGame.Migrations`:

- `M202609150001_CreatePlayers` - `players(id uuid, nickname, nickname_normalized unique, character, created_at)`
- `M202609150002_CreateScores` - `scores(id uuid, player_id fk, mode, score, duration_seconds, created_at)` + indexes for leaderboard queries
- `M202609150003_AddColorLevelAndDailyChallenge` - `players.color`, `scores.level`, `scores.challenge_date` (+ check constraints and a daily-board index)
- `M202609150004_AddWordleMode` - allows `wordle` scores
- `M202610190001_AddGameSessions` - `game_sessions(id, player_id fk, mode, status active/completed, challenge_date, started_at, completed_at, final_score, highest_level, levels_completed, lives_lost, duration_seconds)` with a player/mode/completed index and a partial index on open sessions; `scores.session_id` (unique, fk); allows `frogger` scores and levels up to 99. Player stats are aggregated from `game_sessions` rather than stored.

Add a new migration by dropping a class with the next `[Migration(yyyyMMddNNNN)]` number into that project; the migration service applies it on the next start.

## Build and test

```sh
dotnet build
dotnet test
```

`dotnet test` includes integration tests that start a throwaway `postgres:17-alpine` container, apply every migration and call the real API through `WebApplicationFactory`, so Docker must be running (it already is for Aspire, and GitHub's `ubuntu-latest` runners have it).

## Deploy

See [`deploy/README.md`](deploy/README.md) for the two supported paths:

- `deploy/docker-compose.prod.yml` + Caddy (automatic HTTPS) on any VPS using the images published to GHCR by `.github/workflows/publish-image.yml`
- Azure Container Apps via Aspire; the Azure-specific runbook lives at [`docs/deploy/azure.md`](docs/deploy/azure.md)
- the current Azure environment handoff lives at [`docs/deploy/azure-environment-handoff.md`](docs/deploy/azure-environment-handoff.md)
- CI/CD follow-up planning lives at [`docs/deploy/cicd-next-steps.md`](docs/deploy/cicd-next-steps.md)
