using FluentMigrator.Runner;
using GretasGame.Migrations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace GretasGame.Api.Tests.Integration;

/// <summary>
/// A throwaway PostgreSQL container with every FluentMigrator migration
/// applied, plus the real API pointed at it. Needs Docker (Aspire already does).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("gretasgame")
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public string ConnectionString => container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        MigrateUp(ConnectionString);

        // Program reads the connection string while building, so set it as an
        // environment variable too (host settings alone arrive too late for some readers).
        Environment.SetEnvironmentVariable("ConnectionStrings__gretasgame", ConnectionString);
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:gretasgame", ConnectionString));
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__gretasgame", null);
        await container.DisposeAsync();
    }

    /// <summary>A client with its own X-Forwarded-For address, so tests never share a rate-limit bucket.</summary>
    public HttpClient CreateClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", $"10.{Random.Shared.Next(256)}.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
        return client;
    }

    public async Task ExecuteAsync(string sql, object? args = null)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (args is not null)
        {
            foreach (var p in args.GetType().GetProperties()) cmd.Parameters.AddWithValue(p.Name, p.GetValue(args)!);
        }
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> CountAsync(string sql, object? args = null)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (args is not null)
        {
            foreach (var p in args.GetType().GetProperties()) cmd.Parameters.AddWithValue(p.Name, p.GetValue(args)!);
        }
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static void MigrateUp(string connectionString)
    {
        using var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(MigrationsAssembly.Assembly).For.Migrations())
            .BuildServiceProvider(false);
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
