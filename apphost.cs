#:sdk Aspire.AppHost.Sdk@13.5.4
#:package Aspire.Hosting.PostgreSQL@13.5.4
#:package Aspire.Hosting.JavaScript@13.5.4
#:project src/GretasGame.Api
#:project src/GretasGame.MigrationService
#:property AspireUseCliBundle=true
#pragma warning disable ASPIREJAVASCRIPT001 // AddNextJsApp is experimental in Aspire 13.x

// Greta's Game for fun - local orchestration.
//
//   postgres  -> PostgreSQL container with a persistent volume (+ pgweb UI)
//   migrations-> FluentMigrator worker; runs to completion before the API starts
//   api       -> .NET Minimal API (scores + leaderboard)
//   web       -> Next.js app from the sibling gretas-game-web repo
//
// Run with `aspire run` from this directory.

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("gretas-game-postgres")
    .WithPgWeb();

var db = postgres.AddDatabase("gretasgame");

var migrations = builder.AddProject<Projects.GretasGame_MigrationService>("migrations")
    .WithReference(db)
    .WaitFor(db);

var api = builder.AddProject<Projects.GretasGame_Api>("api")
    .WithReference(db)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// The web app lives in its own repo next to this one.
builder.AddNextJsApp("web", "../gretas-game-web")
    .WithPnpm()
    .WithReference(api)
    .WaitFor(api)
    // next.config.ts rewrites /api/* to this URL so the browser never crosses origins.
    .WithEnvironment("API_URL", api.GetEndpoint("http"))
    .WithExternalHttpEndpoints();

builder.Build().Run();
