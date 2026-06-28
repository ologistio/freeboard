namespace Freeboard.CLI;

/// <summary>
/// The <c>user</c> command group. Administers users through the Freeboard HTTP API ONLY -
/// it never touches the database or the persistence user store. Base URL via
/// <c>--api-url</c>/<c>FREEBOARD_API_URL</c>; admin token via <c>--token</c>/<c>FREEBOARD_ADMIN_TOKEN</c>.
/// Exit codes follow the CLI convention: 0 success, 1 input/validation (the API's 422), 3
/// operational/HTTP failure (401/403/5xx/connection refused).
/// </summary>
public sealed class UserCommands
{
    /// <summary>Create a user. Prints the one-time temporary password once.</summary>
    /// <param name="email">-e, the new user's email.</param>
    /// <param name="name">-n, the new user's display name.</param>
    /// <param name="role">-r, the global role (admin or member). Defaults to member.</param>
    /// <param name="apiUrl">Base URL of the Freeboard API. Overrides FREEBOARD_API_URL.</param>
    /// <param name="token">Admin bearer token. Overrides FREEBOARD_ADMIN_TOKEN.</param>
    public int Create(
        string email, string name, string role = "member", string? apiUrl = null, string? token = null)
    {
        return Run(apiUrl, token, async (client, ct) =>
        {
            var result = await client.CreateUserAsync(email, name, role, ct).ConfigureAwait(false);
            return Translate(result, payload =>
            {
                Console.WriteLine($"Created user {payload.User.Email} (id {payload.User.Id}).");
                PrintTempPassword(payload.TemporaryPassword);
            });
        });
    }

    /// <summary>List users.</summary>
    /// <param name="apiUrl">Base URL of the Freeboard API. Overrides FREEBOARD_API_URL.</param>
    /// <param name="token">Admin bearer token. Overrides FREEBOARD_ADMIN_TOKEN.</param>
    public int List(string? apiUrl = null, string? token = null)
    {
        return Run(apiUrl, token, async (client, ct) =>
        {
            var result = await client.ListUsersAsync(ct).ConfigureAwait(false);
            return Translate(result, users =>
            {
                foreach (var user in users)
                {
                    var state = user.Enabled ? "enabled" : "disabled";
                    Console.WriteLine($"{user.Id}  {user.Email}  {user.GlobalRole}  {state}");
                }
            });
        });
    }

    /// <summary>Disable a user (revokes their sessions).</summary>
    /// <param name="emailOrId">-i, the user's email or ULID id.</param>
    /// <param name="apiUrl">Base URL of the Freeboard API. Overrides FREEBOARD_API_URL.</param>
    /// <param name="token">Admin bearer token. Overrides FREEBOARD_ADMIN_TOKEN.</param>
    public int Disable(string emailOrId, string? apiUrl = null, string? token = null)
        => ResolveAndAct(apiUrl, token, emailOrId, "disabled",
            (client, id, ct) => client.DisableUserAsync(id, ct));

    /// <summary>Enable a user.</summary>
    /// <param name="emailOrId">-i, the user's email or ULID id.</param>
    /// <param name="apiUrl">Base URL of the Freeboard API. Overrides FREEBOARD_API_URL.</param>
    /// <param name="token">Admin bearer token. Overrides FREEBOARD_ADMIN_TOKEN.</param>
    public int Enable(string emailOrId, string? apiUrl = null, string? token = null)
        => ResolveAndAct(apiUrl, token, emailOrId, "enabled",
            (client, id, ct) => client.EnableUserAsync(id, ct));

    /// <summary>Reset a user's password. Prints the one-time temporary password once.</summary>
    /// <param name="emailOrId">-i, the user's email or ULID id.</param>
    /// <param name="apiUrl">Base URL of the Freeboard API. Overrides FREEBOARD_API_URL.</param>
    /// <param name="token">Admin bearer token. Overrides FREEBOARD_ADMIN_TOKEN.</param>
    public int ResetPassword(string emailOrId, string? apiUrl = null, string? token = null)
    {
        return Run(apiUrl, token, async (client, ct) =>
        {
            var idResult = await ResolveIdAsync(client, emailOrId, ct).ConfigureAwait(false);
            if (idResult.Code is not null)
            {
                return idResult.Code.Value;
            }

            var result = await client.ResetPasswordAsync(idResult.Id!, ct).ConfigureAwait(false);
            return Translate(result, payload =>
            {
                Console.WriteLine($"Reset password for {emailOrId}.");
                PrintTempPassword(payload.TemporaryPassword);
            });
        });
    }

    /// <summary>Bootstrap the first admin via POST /setup. Prints the returned admin token once.</summary>
    /// <param name="email">-e, the first admin's email.</param>
    /// <param name="name">-n, the first admin's display name.</param>
    /// <param name="bootstrapSecret">The one-time bootstrap secret. Overrides FREEBOARD_BOOTSTRAP_SECRET.</param>
    /// <param name="password">-p, an optional initial password.</param>
    /// <param name="apiUrl">Base URL of the Freeboard API. Overrides FREEBOARD_API_URL.</param>
    public int Bootstrap(
        string email,
        string name,
        string? bootstrapSecret = null,
        string? password = null,
        string? apiUrl = null)
    {
        var secret = bootstrapSecret ?? Environment.GetEnvironmentVariable("FREEBOARD_BOOTSTRAP_SECRET");
        if (string.IsNullOrWhiteSpace(secret))
        {
            Console.Error.WriteLine(
                "No bootstrap secret. Pass --bootstrap-secret or set FREEBOARD_BOOTSTRAP_SECRET.");
            return 3;
        }

        // No admin token exists yet during bootstrap, so the client carries none.
        return Run(apiUrl, token: null, async (client, ct) =>
        {
            var result = await client.BootstrapAsync(email, name, password, secret, ct).ConfigureAwait(false);
            return Translate(result, payload =>
            {
                Console.WriteLine($"Created first admin {payload.User.Email} (id {payload.User.Id}).");
                Console.WriteLine($"Admin token: {payload.Token}");
            });
        });
    }

    /// <summary>
    /// Resolves an email-or-id to a user id, then runs a single-result action. Disable/enable take a
    /// user id; an email is resolved client-side via GET /users. A value containing '@' is
    /// treated as an email; otherwise it is used as a ULID id directly.
    /// </summary>
    private int ResolveAndAct(
        string? apiUrl, string? token, string emailOrId, string verb,
        Func<IFreeboardApiClient, string, CancellationToken, Task<ApiResult<Unit>>> act)
    {
        return Run(apiUrl, token, async (client, ct) =>
        {
            var idResult = await ResolveIdAsync(client, emailOrId, ct).ConfigureAwait(false);
            if (idResult.Code is not null)
            {
                return idResult.Code.Value;
            }

            var result = await act(client, idResult.Id!, ct).ConfigureAwait(false);
            return Translate(result, _ => Console.WriteLine($"User {emailOrId} {verb}."));
        });
    }

    /// <summary>
    /// Maps an email-or-id to a user id. An id is returned as-is; an email is matched against the
    /// listing. Returns an exit code instead of an id when the lookup itself fails (operational) or
    /// the email is unknown (validation).
    /// </summary>
    private static async Task<(string? Id, int? Code)> ResolveIdAsync(
        IFreeboardApiClient client, string emailOrId, CancellationToken ct)
    {
        if (!emailOrId.Contains('@', StringComparison.Ordinal))
        {
            return (emailOrId, null);
        }

        var list = await client.ListUsersAsync(ct).ConfigureAwait(false);
        if (list.Outcome != ApiOutcome.Success)
        {
            // The lookup failed (auth/operational); surface that, not a spurious validation error.
            return (null, Translate(list, _ => { }));
        }

        var match = list.Payload!
            .FirstOrDefault(u => string.Equals(u.Email, emailOrId, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            Console.Error.WriteLine($"No user found with email {emailOrId}.");
            return (null, 1);
        }

        return (match.Id, null);
    }

    private static int Run(string? apiUrl, string? token, Func<IFreeboardApiClient, CancellationToken, Task<int>> action)
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

        try
        {
            return action(client, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    /// <summary>Maps an API outcome to an exit code, running <paramref name="onSuccess"/> on 2xx.</summary>
    private static int Translate<T>(ApiResult<T> result, Action<T> onSuccess)
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

    private static void PrintTempPassword(string tempPassword)
    {
        // Printed exactly once; it is never re-retrievable from the API.
        Console.WriteLine($"Temporary password (shown once): {tempPassword}");
    }
}
