namespace Freeboard.CLI;

/// <summary>
/// Resolves the MySQL connection string for the persistence-backed CLI commands.
/// Precedence: an explicit <c>--connection-string</c> option overrides the
/// <c>FREEBOARD_DB</c> environment variable. Returns null when neither is supplied.
/// </summary>
internal static class ConnectionStringResolver
{
    public const string EnvVar = "FREEBOARD_DB";

    public static string? Resolve(string? option)
    {
        if (!string.IsNullOrWhiteSpace(option))
        {
            return option;
        }

        var env = Environment.GetEnvironmentVariable(EnvVar);
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }
}
