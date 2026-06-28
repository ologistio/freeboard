namespace Freeboard.CLI;

/// <summary>
/// Builds the <see cref="IFreeboardApiClient"/> for the <c>user</c> command group from a base URL
/// and admin token. A static seam (mirroring <see cref="PersistenceFactory"/>) so CLI tests can
/// substitute a fake client and run with no live API. The default builds the real
/// HttpClient-backed implementation.
/// </summary>
internal static class ApiClientFactory
{
    public const string ApiUrlEnvVar = "FREEBOARD_API_URL";
    public const string TokenEnvVar = "FREEBOARD_ADMIN_TOKEN";

    /// <summary>(baseUrl, token) -> client. Settable so tests inject a fake without a live API.</summary>
    public static Func<string, string?, IFreeboardApiClient> Create { get; set; } =
        (baseUrl, token) => new HttpFreeboardApiClient(baseUrl, token);

    /// <summary>Base URL precedence: <c>--api-url</c> overrides <c>FREEBOARD_API_URL</c>.</summary>
    public static string? ResolveApiUrl(string? option) => Resolve(option, ApiUrlEnvVar);

    /// <summary>Admin token precedence: <c>--token</c> overrides <c>FREEBOARD_ADMIN_TOKEN</c>.</summary>
    public static string? ResolveToken(string? option) => Resolve(option, TokenEnvVar);

    private static string? Resolve(string? option, string envVar)
    {
        if (!string.IsNullOrWhiteSpace(option))
        {
            return option;
        }

        var env = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }
}
