using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Freeboard.CLI;

/// <summary>
/// HttpClient-backed <see cref="IFreeboardApiClient"/> against <c>/api/v1/freeboard/*</c>.
/// Base URL comes from <c>--api-url</c>/<c>FREEBOARD_API_URL</c>; the admin token from
/// <c>--token</c>/<c>FREEBOARD_ADMIN_TOKEN</c>, sent as <c>Authorization: Bearer</c>. HTTP outcomes
/// are mapped to <see cref="ApiResult{T}"/>; transport failures (connection refused, timeout)
/// become <see cref="ApiOutcome.Failure"/>. Request bodies use snake_case field names
/// (global_role, bootstrap_secret) to match the API contract.
/// </summary>
internal sealed class HttpFreeboardApiClient : IFreeboardApiClient, IDisposable
{
    private const string ApiRoutePrefix = "/api/v1/freeboard";

    private readonly HttpClient http;

    public HttpFreeboardApiClient(string baseUrl, string? token)
    {
        http = new HttpClient { BaseAddress = new Uri(baseUrl, UriKind.Absolute) };
        if (!string.IsNullOrWhiteSpace(token))
        {
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>Test seam: inject a pre-built HttpClient (e.g. over a stub handler) to exercise the response mapping.</summary>
    internal HttpFreeboardApiClient(HttpClient httpClient) => http = httpClient;

    public Task<ApiResult<CreatedUser>> CreateUserAsync(string email, string name, string role, CancellationToken ct)
        => SendAsync(
            HttpMethod.Post, $"{ApiRoutePrefix}/users", new { email, name, global_role = role },
            json => new CreatedUser(ReadUser(json), json.GetProperty("temporary_password").GetString()!),
            ct);

    public Task<ApiResult<IReadOnlyList<ApiUser>>> ListUsersAsync(CancellationToken ct)
        => SendAsync<IReadOnlyList<ApiUser>>(
            HttpMethod.Get, $"{ApiRoutePrefix}/users", body: null,
            json => json.EnumerateArray().Select(ReadUser).ToList(),
            ct);

    public Task<ApiResult<Unit>> DisableUserAsync(string id, CancellationToken ct)
        => SendAsync(
            HttpMethod.Post, $"{ApiRoutePrefix}/users/{Uri.EscapeDataString(id)}/disable", body: null,
            _ => Unit.Value, ct);

    public Task<ApiResult<Unit>> EnableUserAsync(string id, CancellationToken ct)
        => SendAsync(
            HttpMethod.Post, $"{ApiRoutePrefix}/users/{Uri.EscapeDataString(id)}/enable", body: null,
            _ => Unit.Value, ct);

    public Task<ApiResult<ResetPassword>> ResetPasswordAsync(string id, CancellationToken ct)
        => SendAsync(
            HttpMethod.Post, $"{ApiRoutePrefix}/users/{Uri.EscapeDataString(id)}/reset-password", body: null,
            json => new ResetPassword(json.GetProperty("temporary_password").GetString()!),
            ct);

    public Task<ApiResult<BootstrapResult>> BootstrapAsync(
        string email, string name, string? password, string bootstrapSecret, CancellationToken ct)
        => SendAsync(
            HttpMethod.Post, $"{ApiRoutePrefix}/setup",
            new { email, name, password, bootstrap_secret = bootstrapSecret },
            json => new BootstrapResult(ReadUser(json), json.GetProperty("token").GetString()!),
            ct);

    public Task<ApiResult<IReadOnlyList<ApiVendor>>> ListVendorsAsync(CancellationToken ct)
        => SendAsync<IReadOnlyList<ApiVendor>>(
            HttpMethod.Get, $"{ApiRoutePrefix}/vendors", body: null,
            json => json.EnumerateArray().Select(ReadVendor).ToList(),
            ct);

    public Task<ApiResult<IReadOnlyList<ApiVendorScope>>> ListVendorScopesAsync(CancellationToken ct)
        => SendAsync<IReadOnlyList<ApiVendorScope>>(
            HttpMethod.Get, $"{ApiRoutePrefix}/vendor-scopes", body: null,
            json => json.EnumerateArray().Select(ReadVendorScope).ToList(),
            ct);

    public Task<ApiResult<IReadOnlyList<ApiControl>>> ListControlsAsync(CancellationToken ct)
        => SendAsync<IReadOnlyList<ApiControl>>(
            HttpMethod.Get, $"{ApiRoutePrefix}/controls", body: null,
            json => json.EnumerateArray().Select(ReadControl).ToList(),
            ct);

    public Task<ApiResult<IReadOnlyList<ApiEvidenceCollector>>> ListEvidenceCollectorsAsync(CancellationToken ct)
        => SendAsync<IReadOnlyList<ApiEvidenceCollector>>(
            HttpMethod.Get, $"{ApiRoutePrefix}/evidence-collectors", body: null,
            json => json.EnumerateArray().Select(ReadEvidenceCollector).ToList(),
            ct);

    public Task<ApiResult<IReadOnlyList<ApiAttestationTemplate>>> ListAttestationTemplatesAsync(CancellationToken ct)
        => SendAsync<IReadOnlyList<ApiAttestationTemplate>>(
            HttpMethod.Get, $"{ApiRoutePrefix}/attestation-templates", body: null,
            json => json.EnumerateArray().Select(ReadAttestationTemplate).ToList(),
            ct);

    public Task<ApiResult<IReadOnlyList<ApiIntegrationConnection>>> ListIntegrationConnectionsAsync(CancellationToken ct)
        => SendAsync<IReadOnlyList<ApiIntegrationConnection>>(
            HttpMethod.Get, $"{ApiRoutePrefix}/integration-connections", body: null,
            json => json.EnumerateArray().Select(ReadIntegrationConnection).ToList(),
            ct);

    public Task<ApiResult<IssuedCredential>> IssueCollectorCredentialAsync(
        string collectorId, string? expiresAt, CancellationToken ct)
        => SendAsync(
            HttpMethod.Post,
            $"{ApiRoutePrefix}/evidence-collectors/{Uri.EscapeDataString(collectorId)}/credentials",
            string.IsNullOrWhiteSpace(expiresAt) ? new { } : new { expires_at = expiresAt },
            json => new IssuedCredential(
                json.GetProperty("credential_id").GetString()!,
                json.GetProperty("collector_id").GetString()!,
                json.GetProperty("token").GetString()!,
                OptionalString(json, "expires_at")),
            ct);

    public Task<ApiResult<Unit>> RevokeCollectorCredentialAsync(
        string collectorId, string credentialId, CancellationToken ct)
        => SendAsync(
            HttpMethod.Delete,
            $"{ApiRoutePrefix}/evidence-collectors/{Uri.EscapeDataString(collectorId)}/credentials/{Uri.EscapeDataString(credentialId)}",
            body: null,
            _ => Unit.Value,
            ct);

    private async Task<ApiResult<T>> SendAsync<T>(
        HttpMethod method, string path, object? body, Func<JsonElement, T> map, CancellationToken ct)
    {
        string content;
        HttpStatusCode status;
        string? reason;
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            status = response.StatusCode;
            reason = response.ReasonPhrase;
            content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Connection refused, DNS, or timeout: an operational failure, not a validation error.
            return ApiResult<T>.Failure($"Could not reach the API at {http.BaseAddress}: {ex.Message}");
        }

        return status switch
        {
            HttpStatusCode.OK or HttpStatusCode.Created
                => MapSuccess(content, map),
            HttpStatusCode.NoContent
                => ApiResult<T>.Success(map(default)),
            HttpStatusCode.UnprocessableEntity
                => ApiResult<T>.Validation(ValidationMessage(content)),
            HttpStatusCode.Conflict
                => ApiResult<T>.Conflict("The operation conflicts with the current state."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                => ApiResult<T>.Unauthorized(
                    "Not authorized. Check the admin token (--token / FREEBOARD_ADMIN_TOKEN)."),
            _ => ApiResult<T>.Failure($"API returned {(int)status} {reason}."),
        };
    }

    /// <summary>
    /// Parses and maps a 2xx body. An unparseable or unexpectedly-shaped success body is an
    /// operational failure (exit 3), NOT a thrown exception that escapes the exit-code contract.
    /// </summary>
    private static ApiResult<T> MapSuccess<T>(string content, Func<JsonElement, T> map)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return ApiResult<T>.Success(map(document.RootElement));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return ApiResult<T>.Failure($"The API returned a success status with an unreadable body: {ex.Message}");
        }
    }

    private static ApiUser ReadUser(JsonElement json)
    {
        var user = json.TryGetProperty("user", out var u) ? u : json;
        return new ApiUser(
            user.GetProperty("id").GetString()!,
            user.GetProperty("email").GetString()!,
            user.GetProperty("name").GetString()!,
            user.GetProperty("global_role").GetString()!,
            user.TryGetProperty("enabled", out var e) && e.GetBoolean());
    }

    private static ApiVendor ReadVendor(JsonElement json) =>
        new(json.GetProperty("id").GetString()!, json.GetProperty("title").GetString()!);

    private static ApiVendorScope ReadVendorScope(JsonElement json) =>
        new(
            json.GetProperty("id").GetString()!,
            json.GetProperty("title").GetString()!,
            json.GetProperty("vendor").GetString()!,
            OptionalString(json, "requirement"),
            OptionalString(json, "control"),
            json.GetProperty("disposition").GetString()!,
            OptionalString(json, "justification"));

    private static ApiControl ReadControl(JsonElement json) =>
        new(
            json.GetProperty("id").GetString()!,
            json.GetProperty("title").GetString()!,
            json.TryGetProperty("maps_to", out var mapsTo) && mapsTo.ValueKind == JsonValueKind.Array
                ? mapsTo.EnumerateArray().Select(e => e.GetString()!).ToList()
                : [],
            OptionalString(json, "evaluation"));

    private static ApiEvidenceCollector ReadEvidenceCollector(JsonElement json) =>
        new(
            json.GetProperty("id").GetString()!,
            json.GetProperty("title").GetString()!,
            json.GetProperty("control").GetString()!,
            OptionalString(json, "vendor"),
            json.GetProperty("type").GetString()!,
            json.GetProperty("frequency").GetString()!,
            json.TryGetProperty("threshold", out var threshold) && threshold.ValueKind == JsonValueKind.Number
                ? threshold.GetInt32()
                : null,
            ReadConfig(json));

    private static ApiAttestationTemplate ReadAttestationTemplate(JsonElement json) =>
        new(
            json.GetProperty("id").GetString()!,
            json.GetProperty("title").GetString()!,
            json.GetProperty("control").GetString()!,
            json.GetProperty("type").GetString()!,
            OptionalString(json, "body"),
            ReadArray(json, "fields", ReadAttestationField),
            json.TryGetProperty("pass_mark", out var passMark) && passMark.ValueKind == JsonValueKind.Number
                ? passMark.GetInt32()
                : null,
            ReadArray(json, "quiz", ReadQuizItem));

    private static ApiIntegrationConnection ReadIntegrationConnection(JsonElement json) =>
        new(
            json.GetProperty("id").GetString()!,
            json.GetProperty("provider").GetString()!,
            json.GetProperty("base_url").GetString()!,
            json.GetProperty("discovery_cadence").GetString()!,
            OptionalString(json, "vendor"),
            json.GetProperty("token_resolvable").GetBoolean());

    private static ApiAttestationField ReadAttestationField(JsonElement json) =>
        new(
            json.GetProperty("id").GetString()!,
            json.GetProperty("label").GetString()!,
            json.GetProperty("type").GetString()!,
            ReadStringArray(json, "options"));

    private static ApiQuizItem ReadQuizItem(JsonElement json) =>
        new(
            json.GetProperty("id").GetString()!,
            json.GetProperty("prompt").GetString()!,
            ReadStringArray(json, "options"));

    private static IReadOnlyList<T> ReadArray<T>(JsonElement json, string name, Func<JsonElement, T> map) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(map).ToList()
            : [];

    private static IReadOnlyList<string> ReadStringArray(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
            : [];

    private static IReadOnlyDictionary<string, string> ReadConfig(JsonElement json)
    {
        var config = new Dictionary<string, string>(StringComparer.Ordinal);
        if (json.TryGetProperty("config", out var value) && value.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in value.EnumerateObject())
            {
                config[entry.Name] = entry.Value.GetString() ?? string.Empty;
            }
        }

        return config;
    }

    /// <summary>Reads a property that may be JSON null or absent, returning null in either case.</summary>
    private static string? OptionalString(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    /// <summary>
    /// Pulls a human-readable message out of an RFC 7807 validation-problem body (the API's 422
    /// shape: a title plus per-field error arrays). Falls back to a generic line.
    /// </summary>
    private static string ValidationMessage(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var messages = errors.EnumerateObject()
                    .SelectMany(p => p.Value.EnumerateArray().Select(v => v.GetString()))
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .ToList();
                if (messages.Count > 0)
                {
                    return string.Join(" ", messages);
                }
            }

            if (root.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } t)
            {
                return t;
            }
        }
        catch (JsonException)
        {
            // Non-JSON body; fall through to the generic message.
        }

        return "Validation failed.";
    }

    public void Dispose() => http.Dispose();
}
