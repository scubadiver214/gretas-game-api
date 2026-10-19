using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Azure Database for PostgreSQL rejects unencrypted logins. Aspire's password
/// connection string does not set SSL, and Npgsql's default GSS mode negotiates
/// encryption in a way Azure treats as cleartext.
/// </summary>
public static class AzurePostgresConnection
{
    public static void RequireTls(IConfiguration configuration, string connectionName)
    {
        var current = configuration.GetConnectionString(connectionName);
        if (current is null)
        {
            return;
        }

        var updated = EnsureTls(current);
        if (!string.Equals(updated, current, StringComparison.Ordinal))
        {
            configuration[$"ConnectionStrings:{connectionName}"] = updated;
        }
    }

    public static string EnsureTls(string connectionString)
    {
        if (!connectionString.Contains(".postgres.database.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SslMode = SslMode.Require,
            GssEncryptionMode = GssEncryptionMode.Disable,
        };
        return builder.ConnectionString;
    }
}
