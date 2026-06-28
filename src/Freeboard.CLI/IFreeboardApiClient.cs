namespace Freeboard.CLI;

/// <summary>
/// HTTP client seam for the Freeboard admin/bootstrap API. The CLI <c>user</c> group
/// administers users through these calls only; it never touches the database or the
/// persistence user store. Each method returns an <see cref="ApiResult{T}"/> so commands can
/// map an HTTP outcome (success / validation-422 / conflict-409 / auth-401-403 / other) to the
/// CLI exit-code convention without knowing about HttpClient.
/// </summary>
internal interface IFreeboardApiClient
{
    /// <summary>POST /users - create a user; returns the user and a one-time temp password.</summary>
    Task<ApiResult<CreatedUser>> CreateUserAsync(string email, string name, string role, CancellationToken ct);

    /// <summary>GET /users - list users (no credentials).</summary>
    Task<ApiResult<IReadOnlyList<ApiUser>>> ListUsersAsync(CancellationToken ct);

    /// <summary>POST /users/{id}/disable.</summary>
    Task<ApiResult<Unit>> DisableUserAsync(string id, CancellationToken ct);

    /// <summary>POST /users/{id}/enable.</summary>
    Task<ApiResult<Unit>> EnableUserAsync(string id, CancellationToken ct);

    /// <summary>POST /users/{id}/reset-password - returns a new one-time temp password.</summary>
    Task<ApiResult<ResetPassword>> ResetPasswordAsync(string id, CancellationToken ct);

    /// <summary>POST /setup - first-admin bootstrap; returns the admin and an admin token.</summary>
    Task<ApiResult<BootstrapResult>> BootstrapAsync(
        string email, string name, string? password, string bootstrapSecret, CancellationToken ct);
}

/// <summary>The public user fields returned by the API (snake_case on the wire).</summary>
internal sealed record ApiUser(
    string Id, string Email, string Name, string GlobalRole, bool Enabled);

/// <summary>POST /users response: the created user plus the one-time temp password.</summary>
internal sealed record CreatedUser(ApiUser User, string TemporaryPassword);

/// <summary>POST /users/{id}/reset-password response: the new one-time temp password.</summary>
internal sealed record ResetPassword(string TemporaryPassword);

/// <summary>POST /setup response: the created admin and an admin bearer token.</summary>
internal sealed record BootstrapResult(ApiUser User, string Token);

/// <summary>A void success payload for calls that return no body of interest.</summary>
internal sealed record Unit
{
    public static readonly Unit Value = new();
}
