namespace Freeboard.CLI;

/// <summary>
/// The <c>connections</c> command group. Reads the integration-connection list through the Freeboard HTTP
/// API ONLY - it never touches the database. Base URL via <c>--api-url</c>/<c>FREEBOARD_API_URL</c>; admin
/// token via <c>--token</c>/<c>FREEBOARD_ADMIN_TOKEN</c>. Exit codes follow the CLI convention: 0 success,
/// 1 input/validation, 3 operational/HTTP failure (401/403/5xx/connection refused).
/// </summary>
public sealed class ConnectionCommands
{
    /// <summary>List integration connections with their provider, base URL, cadence, and token health.</summary>
    /// <param name="apiUrl">Base URL of the Freeboard API. Overrides FREEBOARD_API_URL.</param>
    /// <param name="token">Admin bearer token. Overrides FREEBOARD_ADMIN_TOKEN.</param>
    public int List(string? apiUrl = null, string? token = null)
    {
        return ApiCommandRunner.Run(apiUrl, token, async (client, ct) =>
        {
            var result = await client.ListIntegrationConnectionsAsync(ct).ConfigureAwait(false);
            return ApiCommandRunner.Translate(result, Print);
        });
    }

    private static void Print(IReadOnlyList<ApiIntegrationConnection> connections)
    {
        foreach (var connection in connections)
        {
            var vendor = string.IsNullOrEmpty(connection.Vendor) ? "-" : connection.Vendor;
            // The API returns only a health flag, never the token value; the CLI prints the flag.
            var health = connection.TokenResolvable ? "resolvable" : "unresolvable";
            Console.WriteLine(
                $"{connection.Id}  {connection.Provider}  {connection.BaseUrl}  {connection.DiscoveryCadence}  "
                + $"vendor {vendor}  token {health}");
        }
    }
}
