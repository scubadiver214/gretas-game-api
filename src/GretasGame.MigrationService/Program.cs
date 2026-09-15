using FluentMigrator.Runner;
using GretasGame.MigrationService;
using GretasGame.Migrations;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Aspire (or docker-compose) injects ConnectionStrings__gretasgame.
builder.AddNpgsqlDataSource("gretasgame");

builder.Services
    .AddFluentMigratorCore()
    .ConfigureRunner(rb => rb
        .AddPostgres()
        // Read the raw connection string (NpgsqlDataSource.ConnectionString redacts the password).
        .WithGlobalConnectionString(sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("gretasgame")
            ?? throw new InvalidOperationException("Connection string 'gretasgame' is not configured."))
        .ScanIn(MigrationsAssembly.Assembly).For.Migrations())
    .AddLogging(lb => lb.AddFluentMigratorConsole());

builder.Services.AddHostedService<MigrationWorker>();

var host = builder.Build();
host.Run();
