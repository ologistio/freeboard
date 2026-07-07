namespace Freeboard.CLI;

/// <summary>
/// Shared HTTP-command plumbing for the CLI groups that read/write through the Freeboard API
/// (<see cref="UserCommands"/>, <see cref="VendorCommands"/>). Owns the client construction
/// (base URL + token resolution, with a malformed URL mapped to an operational failure) and the
/// single definition of the 0/1/3 exit-code contract (0 success, 1 validation, 3 operational).
/// </summary>
internal static class ApiCommandRunner
{
    /// <summary>
    /// Resolves the base URL and token, builds the client, runs <paramref name="action"/>, and
    /// returns its exit code. A missing base URL or a malformed URL is an operational failure (exit 3).
    /// </summary>
    public static int Run(string? apiUrl, string? token, Func<IFreeboardApiClient, CancellationToken, Task<int>> action)
    {
        var baseUrl = ApiClientFactory.ResolveApiUrl(apiUrl);
        if (baseUrl is null)
        {
            Console.Error.WriteLine(
                $"No API URL. Pass --api-url or set {ApiClientFactory.ApiUrlEnvVar}.");
            return 3;
        }

        var resolvedToken = ApiClientFactory.ResolveToken(token);

        // Building the client parses the base URL (and may otherwise fail to construct). A
        // malformed --api-url throws UriFormatException here; map it to an operational failure (exit
        // 3) instead of letting it escape the 0/1/3 exit-code contract as an unhandled exception.
        IFreeboardApiClient client;
        try
        {
            client = ApiClientFactory.Create(baseUrl, resolvedToken);
        }
        catch (Exception ex) when (ex is UriFormatException or FormatException or ArgumentException)
        {
            Console.Error.WriteLine($"Invalid API URL '{baseUrl}': {ex.Message}");
            return 3;
        }

        using var disposableClient = client as IDisposable;
        return action(client, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>Maps an API outcome to an exit code, running <paramref name="onSuccess"/> on 2xx.</summary>
    public static int Translate<T>(ApiResult<T> result, Action<T> onSuccess)
    {
        switch (result.Outcome)
        {
            case ApiOutcome.Success:
                onSuccess(result.Payload!);
                return 0;
            case ApiOutcome.Validation:
                Console.Error.WriteLine(result.Message);
                return 1;
            default:
                // Conflict / Unauthorized / Failure are all operational from the CLI's view.
                Console.Error.WriteLine(result.Message);
                return 3;
        }
    }
}
