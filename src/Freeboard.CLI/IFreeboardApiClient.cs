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

    /// <summary>GET /vendors - list vendors.</summary>
    Task<ApiResult<IReadOnlyList<ApiVendor>>> ListVendorsAsync(CancellationToken ct);

    /// <summary>GET /vendor-scopes - list vendor-scopes (per-vendor requirement/control exceptions).</summary>
    Task<ApiResult<IReadOnlyList<ApiVendorScope>>> ListVendorScopesAsync(CancellationToken ct);

    /// <summary>GET /controls - list controls with their resolved maps_to and optional evaluation rule.</summary>
    Task<ApiResult<IReadOnlyList<ApiControl>>> ListControlsAsync(CancellationToken ct);

    /// <summary>GET /evidence-collectors - list evidence-collectors attached to controls.</summary>
    Task<ApiResult<IReadOnlyList<ApiEvidenceCollector>>> ListEvidenceCollectorsAsync(CancellationToken ct);

    /// <summary>GET /attestation-templates - list attestation-templates attached to controls.</summary>
    Task<ApiResult<IReadOnlyList<ApiAttestationTemplate>>> ListAttestationTemplatesAsync(CancellationToken ct);
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

/// <summary>A vendor as returned by the API (single-word fields).</summary>
internal sealed record ApiVendor(string Id, string Title);

/// <summary>
/// A vendor-scope as returned by the API. Exactly one of <see cref="Requirement"/> or
/// <see cref="Control"/> is set (the other null). <see cref="Justification"/> is null when unset and
/// always present for an <c>Out</c> exception.
/// </summary>
internal sealed record ApiVendorScope(
    string Id, string Title, string Vendor, string? Requirement, string? Control, string Disposition, string? Justification);

/// <summary>A control as returned by the API, with its resolved maps_to and optional evaluation rule.</summary>
internal sealed record ApiControl(string Id, string Title, IReadOnlyList<string> MapsTo, string? Evaluation);

/// <summary>
/// An evidence-collector as returned by the API. <see cref="Vendor"/> and <see cref="Threshold"/> are
/// null when unset; <see cref="Config"/> is the type-specific settings map (empty when unset).
/// </summary>
internal sealed record ApiEvidenceCollector(
    string Id,
    string Title,
    string Control,
    string? Vendor,
    string Type,
    string Frequency,
    int? Threshold,
    IReadOnlyDictionary<string, string> Config);

/// <summary>An attestation form field as returned by the API.</summary>
internal sealed record ApiAttestationField(string Id, string Label, string Type, IReadOnlyList<string> Options);

/// <summary>
/// A quiz item as returned by the API: prompt and options only. It has NO answer - the API redacts the
/// correct answer, so the CLI never receives or prints it.
/// </summary>
internal sealed record ApiQuizItem(string Id, string Prompt, IReadOnlyList<string> Options);

/// <summary>
/// An attestation-template as returned by the API. <see cref="Body"/> and <see cref="PassMark"/> are null
/// when unset; <see cref="Fields"/> and <see cref="Quiz"/> are the ordered lists (empty when unset). The
/// quiz carries no answer.
/// </summary>
internal sealed record ApiAttestationTemplate(
    string Id,
    string Title,
    string Control,
    string Type,
    string? Body,
    IReadOnlyList<ApiAttestationField> Fields,
    int? PassMark,
    IReadOnlyList<ApiQuizItem> Quiz);

/// <summary>A void success payload for calls that return no body of interest.</summary>
internal sealed record Unit
{
    public static readonly Unit Value = new();
}
