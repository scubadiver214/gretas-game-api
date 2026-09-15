using System.Diagnostics;
using FluentMigrator.Runner;
using Npgsql;

namespace GretasGame.MigrationService;

/// <summary>
/// Runs once on startup: makes sure the target database exists, applies all
/// pending FluentMigrator migrations, then stops the host. Aspire's
/// <c>WaitForCompletion</c> and docker-compose's <c>service_completed_successfully</c>
/// both key off the process exit code.
/// </summary>
public sealed class MigrationWorker(
    IServiceProvider services,
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    ILogger<MigrationWorker> logger) : BackgroundService
{
    public const string ActivitySourceName = "GretasGame.Migrations";
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private const int MaxAttempts = 15;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = ActivitySource.StartActivity("Migrating database", ActivityKind.Client);
        try
        {
            var connectionString = configuration.GetConnectionString("gretasgame")
                ?? throw new InvalidOperationException("Connection string 'gretasgame' is not configured.");

            await EnsureDatabaseAsync(connectionString, stoppingToken);
            await RunMigrationsAsync(stoppingToken);

            logger.LogInformation("Database is up to date.");
            Environment.ExitCode = 0;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down; nothing to report.
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            logger.LogCritical(ex, "Database migration failed.");
            Environment.ExitCode = 1;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    /// <summary>Creates the database if the server is reachable but the database is missing.</summary>
    private async Task EnsureDatabaseAsync(string connectionString, CancellationToken ct)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var databaseName = target.Database ?? throw new InvalidOperationException("Connection string has no Database.");
        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(admin.ConnectionString);
                await conn.OpenAsync(ct);

                await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", conn);
                exists.Parameters.AddWithValue("name", databaseName);
                if (await exists.ExecuteScalarAsync(ct) is null)
                {
                    logger.LogInformation("Creating database {Database}.", databaseName);
                    // Identifiers cannot be parameterised; quote defensively.
                    await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName.Replace("\"", "\"\"")}\"", conn);
                    await create.ExecuteNonQueryAsync(ct);
                }
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                logger.LogWarning("Postgres not ready yet (attempt {Attempt}/{Max}): {Message}", attempt, MaxAttempts, ex.Message);
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    private async Task RunMigrationsAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = services.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
                runner.ListMigrations();
                runner.MigrateUp();
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                logger.LogWarning("Migration attempt {Attempt}/{Max} failed transiently: {Message}", attempt, MaxAttempts, ex.Message);
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is NpgsqlException { IsTransient: true }
        || ex is System.Net.Sockets.SocketException
        || ex is TimeoutException
        || ex is System.IO.IOException
        || (ex is NpgsqlException npg && npg.InnerException is System.Net.Sockets.SocketException)
        || ex.Message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("the database system is starting up", StringComparison.OrdinalIgnoreCase);
}
