using System.Web;
using Npgsql;

namespace Birooni.Licensing.Api.Common;

public static class ConnectionStringHelper
{
    public static string? ResolveConnectionString(IConfiguration configuration)
    {
        // 1. Check environment variable DATABASE_URL
        var envDatabaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (!string.IsNullOrWhiteSpace(envDatabaseUrl))
        {
            return NormalizePostgresConnectionString(envDatabaseUrl.Trim());
        }

        // 2. Check configuration for DATABASE_URL key
        var configDatabaseUrl = configuration["DATABASE_URL"];
        if (!string.IsNullOrWhiteSpace(configDatabaseUrl))
        {
            return NormalizePostgresConnectionString(configDatabaseUrl.Trim());
        }

        // 3. Fall back to ConnectionStrings:Database
        var fallbackConnection = configuration.GetConnectionString("Database");
        if (!string.IsNullOrWhiteSpace(fallbackConnection))
        {
            return NormalizePostgresConnectionString(fallbackConnection.Trim());
        }

        return null;
    }

    public static string NormalizePostgresConnectionString(string connectionOrUrl)
    {
        if (string.IsNullOrWhiteSpace(connectionOrUrl))
        {
            return connectionOrUrl;
        }

        // Check if string begins with postgres:// or postgresql://
        if (connectionOrUrl.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            connectionOrUrl.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return ConvertUriToNpgsqlConnectionString(connectionOrUrl);
        }

        return connectionOrUrl;
    }

    private static readonly System.Text.RegularExpressions.Regex PostgresUriRegex = new(
        @"^(?i)postgres(?:ql)?://(?:(?<user>[^:]+)(?::(?<password>.*))?@)?(?<host>[^:/?#]+)(?::(?<port>\d+))?(?:/(?<db>[^?#]*))?(?:\?(?<query>[^#]*))?$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string ConvertUriToNpgsqlConnectionString(string postgresUriString)
    {
        var match = PostgresUriRegex.Match(postgresUriString);
        if (!match.Success)
        {
            return postgresUriString;
        }

        var host = match.Groups["host"].Value;
        var portStr = match.Groups["port"].Value;
        var port = int.TryParse(portStr, out var p) && p > 0 ? p : 5432;
        var db = match.Groups["db"].Value.Trim('/');
        var user = match.Groups["user"].Success ? Uri.UnescapeDataString(match.Groups["user"].Value) : null;
        var pass = match.Groups["password"].Success ? Uri.UnescapeDataString(match.Groups["password"].Value) : null;

        var npgsqlBuilder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = string.IsNullOrEmpty(db) ? "postgres" : db
        };

        if (!string.IsNullOrEmpty(user))
        {
            npgsqlBuilder.Username = user;
        }

        if (!string.IsNullOrEmpty(pass))
        {
            npgsqlBuilder.Password = pass;
        }

        var query = match.Groups["query"].Value;
        if (!string.IsNullOrEmpty(query))
        {
            var queryParams = HttpUtility.ParseQueryString(query);
            foreach (string? key in queryParams.AllKeys)
            {
                if (string.IsNullOrEmpty(key)) continue;
                var value = queryParams[key];

                switch (key.ToLowerInvariant())
                {
                    case "sslmode":
                        if (Enum.TryParse<SslMode>(value, true, out var sslMode))
                        {
                            npgsqlBuilder.SslMode = sslMode;
                        }
                        break;
                    case "pooling":
                        if (bool.TryParse(value, out var pooling))
                        {
                            npgsqlBuilder.Pooling = pooling;
                        }
                        break;
                    case "timeout":
                        if (int.TryParse(value, out var timeout))
                        {
                            npgsqlBuilder.Timeout = timeout;
                        }
                        break;
                }
            }
        }

        return npgsqlBuilder.ConnectionString;
    }
}
