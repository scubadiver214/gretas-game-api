#:sdk Aspire.AppHost.Sdk@13.5.4
#:package Aspire.Hosting.PostgreSQL@13.5.4
#:package Aspire.Hosting.JavaScript@13.5.4
#:package Aspire.Hosting.Azure.AppContainers@13.5.4
#:package Aspire.Hosting.Azure.PostgreSQL@13.5.4
#:package Aspire.Hosting.Azure.ApplicationInsights@13.5.4
#:project src/GretasGame.Api
#:project src/GretasGame.MigrationService
#:property AspireUseCliBundle=true
#pragma warning disable ASPIREJAVASCRIPT001 // AddNextJsApp is experimental in Aspire 13.x

// Greta's Game for fun - local orchestration and Azure deployment.
//
//   postgres  -> Azure Database for PostgreSQL in publish/deploy, local container in run mode
//   migrations-> FluentMigrator worker; runs to completion before the API starts
//   api       -> .NET Minimal API (scores + leaderboard)
//   web       -> Next.js app from the sibling gretas-game-web repo
//
// Run with `aspire run` from this directory.

var builder = DistributedApplication.CreateBuilder(args);

var hasCustomDomainBinding =
    !string.IsNullOrWhiteSpace(builder.Configuration["Parameters:customDomain"])
    && !string.IsNullOrWhiteSpace(builder.Configuration["Parameters:certificateName"]);

var customDomain = hasCustomDomainBinding ? builder.AddParameter("customDomain") : null;
var certificateName = hasCustomDomainBinding ? builder.AddParameter("certificateName") : null;

var appInsights = builder.AddAzureApplicationInsights("appinsights");
builder.AddAzureContainerAppEnvironment("aca-env");

var postgres = builder.AddAzurePostgresFlexibleServer("postgres")
    .RunAsContainer(container => container
        .WithDataVolume("gretas-game-postgres")
        .WithPgWeb())
    .WithPasswordAuthentication();

var db = postgres.AddDatabase("gretasgame");

var migrations = builder.AddProject<Projects.GretasGame_MigrationService>("migrations")
    .WithReference(db)
    .WithReference(appInsights)
    .WaitFor(db);

if (builder.ExecutionContext.IsPublishMode)
{
    migrations.PublishAsAzureContainerAppJob();
}

var api = builder.AddProject<Projects.GretasGame_Api>("api")
    .WithReference(db)
    .WithReference(appInsights)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck("/health")
    .WithEnvironment("HealthChecks__ExposeHttpEndpoints", "true");

if (builder.ExecutionContext.IsRunMode)
{
    api.WithExternalHttpEndpoints();
}

// The web app lives in its own repo next to this one.
var web = builder.AddNextJsApp("web", "../gretas-game-web")
    .WithPnpm()
    .WithReference(api)
    .WaitFor(api)
    // next.config.ts rewrites /api/* to this URL so the browser never crosses origins.
    .WithEnvironment("API_URL", api.GetEndpoint("http"))
    .WithExternalHttpEndpoints();

if (builder.ExecutionContext.IsPublishMode)
{
    web.PublishAsAzureContainerApp((_, app) =>
    {
        if (customDomain is not null && certificateName is not null)
        {
            app.ConfigureCustomDomain(customDomain, certificateName);
        }

        app.Template.Scale.MinReplicas = 0;
    });
}

builder.Build().Run();
