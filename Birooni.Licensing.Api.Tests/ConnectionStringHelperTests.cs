using Birooni.Licensing.Api.Common;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Birooni.Licensing.Api.Tests;

public class ConnectionStringHelperTests
{
    [Fact]
    public void NormalizePostgresConnectionString_ParsesPostgresPrefix()
    {
        var input = "postgres://usr:secret%40pass@myhost.db:5432/licensing_prod";
        var result = ConnectionStringHelper.NormalizePostgresConnectionString(input);

        Assert.Contains("Host=myhost.db", result);
        Assert.Contains("Port=5432", result);
        Assert.Contains("Database=licensing_prod", result);
        Assert.Contains("Username=usr", result);
        Assert.Contains("Password=secret@pass", result);
    }

    [Fact]
    public void NormalizePostgresConnectionString_ParsesPostgresqlPrefix_WithQueryParams()
    {
        var input = "postgresql://admin:mypassword@supabase-host.com:6543/postgres?sslmode=require&timeout=30";
        var result = ConnectionStringHelper.NormalizePostgresConnectionString(input);

        Assert.Contains("Host=supabase-host.com", result);
        Assert.Contains("Port=6543", result);
        Assert.Contains("Database=postgres", result);
        Assert.Contains("Username=admin", result);
        Assert.Contains("Password=mypassword", result);
        Assert.Contains("SSL Mode=Require", result);
        Assert.Contains("Timeout=30", result);
    }

    [Fact]
    public void NormalizePostgresConnectionString_PreservesStandardNpgsqlFormat()
    {
        var standard = "Host=localhost;Port=5432;Database=birooni_licensing;Username=postgres;Password=postgres;";
        var result = ConnectionStringHelper.NormalizePostgresConnectionString(standard);

        Assert.Equal(standard, result);
    }

    [Fact]
    public void ResolveConnectionString_FallsBackToConfig_WhenNoEnvVar()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "ConnectionStrings:Database", "Host=config-host;Database=fallback_db;Username=usr;Password=pwd;" }
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var result = ConnectionStringHelper.ResolveConnectionString(configuration);

        Assert.Equal("Host=config-host;Database=fallback_db;Username=usr;Password=pwd;", result);
    }
}
