using Microsoft.Extensions.Hosting;
using Npgsql;

namespace GretasGame.Api.Tests;

public class AzurePostgresConnectionTests
{
    [Fact]
    public void Azure_host_requires_tls_and_disables_gss()
    {
        var updated = AzurePostgresConnection.EnsureTls(
            "Host=example.postgres.database.azure.com;Username=app;Password=secret;Database=gretasgame");

        var builder = new NpgsqlConnectionStringBuilder(updated);
        Assert.Equal(SslMode.Require, builder.SslMode);
        Assert.Equal(GssEncryptionMode.Disable, builder.GssEncryptionMode);
        Assert.Equal("gretasgame", builder.Database);
        Assert.Equal("secret", builder.Password);
    }

    [Fact]
    public void Local_host_is_left_unchanged()
    {
        const string local = "Host=localhost;Username=app;Password=secret;Database=gretasgame";
        Assert.Equal(local, AzurePostgresConnection.EnsureTls(local));
    }
}
