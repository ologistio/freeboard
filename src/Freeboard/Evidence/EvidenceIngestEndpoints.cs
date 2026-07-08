using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Freeboard.Api;
using Freeboard.Auth;
using Freeboard.Compliance;
using Freeboard.Persistence;
using Microsoft.AspNetCore.Http.Metadata;

namespace Freeboard.Evidence;

/// <summary>
/// The runtime Evidence ingest endpoint: <c>POST /api/v1/freeboard/evidence</c>, authenticated by a
/// per-collector machine credential (the collector bearer scheme via the named ingest policy). The body
/// is handled MANUALLY, not via <c>[FromBody]</c> binding, so (a) the exact bytes are hashed for the
/// idempotency key before any deserialization and (b) a JSON type mismatch on ANY field is a 422, not a
/// framework 400. The one rule: parse the untrusted body into a <see cref="JsonElement"/> tree (which
/// throws only on malformed JSON -> 400) and read every field from that tree in the semantic pass
/// (every value/type problem -> 422); project into the typed store input only after validation passes.
/// </summary>
public static class EvidenceIngestEndpoints
{
    /// <summary>The only accepted contract version string.</summary>
    public const string SchemaVersion = "freeboard.evidence.v1";

    /// <summary>Per-endpoint request body cap (1 MiB). Evidence JSON is small; this is generous.</summary>
    public const long MaxBodyBytes = 1_048_576;

    private const int MaxIdLength = 190;

    private static readonly HashSet<string> Severities = new(StringComparer.Ordinal) { "hard", "soft" };

    private static readonly HashSet<string> Statuses =
        new(StringComparer.Ordinal) { "pass", "fail", "unknown", "not_applicable" };

    public static void MapEvidenceIngestEndpoints(this WebApplication app)
    {
        // Own route group: its ONLY authorization is the named ingest policy, which binds the collector
        // scheme. It deliberately does NOT use the parameterless .RequireAuthorization() that every other
        // API module applies (that binds the default session scheme and would 401 every collector token).
        var group = app.MapGroup(ApiRoutes.ApiRoutePrefix)
            .RequireAuthorization(CollectorBearerAuthenticationHandler.IngestPolicyName);

        group.MapPost("/evidence", IngestAsync)
            .MarkIngestEndpoint()
            .WithMetadata(new RequestSizeLimit(MaxBodyBytes));
    }

    private static async Task<IResult> IngestAsync(
        HttpContext context, IComplianceStore reads, IEvidenceIngestStore store)
    {
        var ct = context.RequestAborted;

        // 1-2. Read the raw body once, bounded by the size limit (413 on overflow).
        byte[] body;
        try
        {
            body = await ReadBoundedBodyAsync(context, MaxBodyBytes, ct).ConfigureAwait(false);
        }
        catch (BodyTooLargeException)
        {
            return TooLarge();
        }

        // 3. Hash the EXACT bytes for the idempotency key, before any deserialization.
        var bodyHash = SHA256.HashData(body);

        // 4. Parse to a JsonElement tree. Throws ONLY on malformed JSON (-> 400); a JSON type mismatch on
        // any field is data the parser accepts and is caught as a 422 in step 5.
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return MalformedJson();
        }

        using (document)
        {
            var root = document.RootElement;

            // 5. Semantic validation. Any wrong ValueKind or value is 422 (never 400).
            if (Validate(root, out var validated, out var error) is false)
            {
                return Semantic(error!);
            }

            // The credential is authoritative: the body collector_id must match the authenticated collector.
            var credentialCollector = context.User.FindFirst(CollectorBearerAuthenticationHandler.CollectorIdClaim)?.Value;
            if (!string.Equals(credentialCollector, validated!.CollectorId, StringComparison.Ordinal))
            {
                return Semantic("collector_id does not match the authenticated credential.");
            }

            // Snapshot the collector identity from the register; unknown collector is 422.
            EvidenceCollectorRow? collector;
            try
            {
                collector = (await reads.GetEvidenceCollectorsAsync(ct).ConfigureAwait(false))
                    .FirstOrDefault(c => string.Equals(c.Id, validated.CollectorId, StringComparison.Ordinal));
            }
            catch (Exception ex) when (ComplianceEndpoints.IsStoreFailure(ex))
            {
                return Unreachable();
            }

            if (collector is null)
            {
                return Semantic($"collector_id '{validated.CollectorId}' is not a registered evidence-collector.");
            }

            // 6. Project into the store input, snapshotting identity and deriving the raw counts.
            var run = new EvidenceRunInput(
                CollectorId: validated.CollectorId,
                CollectorTitle: collector.Title,
                ControlId: collector.Control,
                VendorId: collector.Vendor,
                CollectorType: collector.Type,
                RunId: validated.RunId,
                SchemaVersion: validated.SchemaVersion,
                CollectorVersion: validated.CollectorVersion,
                StartedAt: validated.StartedAt,
                FinishedAt: validated.FinishedAt,
                RequestBodySha256: bodyHash,
                HardFailCount: validated.Checks.Count(c =>
                    c.Severity == "hard" && c.Status == "fail"),
                SoftFailCount: validated.Checks.Count(c =>
                    c.Severity == "soft" && c.Status == "fail"),
                TotalCount: validated.Checks.Count,
                Metadata: validated.Metadata,
                Checks: validated.Checks);

            EvidenceAppendResult result;
            try
            {
                result = await store.TryAppendAsync(run, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ComplianceEndpoints.IsStoreFailure(ex))
            {
                return Unreachable();
            }

            if (!result.WasNew && !result.BodyMatches)
            {
                return Conflict();
            }

            var payload = new
            {
                evidence_id = result.EvidenceId,
                collector_id = run.CollectorId,
                run_id = run.RunId,
                received_at = DateTime.SpecifyKind(result.ReceivedAt, DateTimeKind.Utc),
                hard_fail_count = result.HardFailCount,
                soft_fail_count = result.SoftFailCount,
                total_count = result.TotalCount,
            };

            // 201 for a new landing, 200 for an identical replay (same stored received_at/counts).
            return result.WasNew
                ? Results.Json(payload, statusCode: StatusCodes.Status201Created)
                : Results.Json(payload, statusCode: StatusCodes.Status200OK);
        }
    }

    /// <summary>
    /// Reads every externally-supplied field from the parsed tree and validates its ValueKind and value.
    /// Returns false with a message on the FIRST failure; true with the projected typed values otherwise.
    /// </summary>
    private static bool Validate(JsonElement root, out ValidatedRequest? validated, out string? error)
    {
        validated = null;
        error = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "The request body must be a JSON object.";
            return false;
        }

        if (!TryRequiredString(root, "schema_version", out var schemaVersion, out error))
        {
            return false;
        }

        if (!string.Equals(schemaVersion, SchemaVersion, StringComparison.Ordinal))
        {
            error = $"schema_version must be exactly '{SchemaVersion}'.";
            return false;
        }

        if (!TryRequiredString(root, "collector_id", out var collectorId, out error)
            || !CheckId("collector_id", collectorId, out error))
        {
            return false;
        }

        if (!TryRequiredString(root, "run_id", out var runId, out error)
            || !CheckId("run_id", runId, out error))
        {
            return false;
        }

        if (!TryOptionalString(root, "collector_version", out var collectorVersion, out error))
        {
            return false;
        }

        if (!TryRequiredUtcTimestamp(root, "started_at", out var startedAt, out error)
            || !TryRequiredUtcTimestamp(root, "finished_at", out var finishedAt, out error))
        {
            return false;
        }

        if (finishedAt < startedAt)
        {
            error = "finished_at must be at or after started_at.";
            return false;
        }

        if (!TryValidateChecks(root, out var checks, out error))
        {
            return false;
        }

        if (!TryOptionalObject(root, "metadata", out var metadata, out error))
        {
            return false;
        }

        validated = new ValidatedRequest(
            schemaVersion, collectorId, runId, collectorVersion, startedAt, finishedAt, checks, metadata);
        return true;
    }

    private static bool TryValidateChecks(
        JsonElement root, out IReadOnlyList<EvidenceCheckInput> checks, out string? error)
    {
        checks = [];
        error = null;

        if (!root.TryGetProperty("checks", out var checksElement) || checksElement.ValueKind != JsonValueKind.Array)
        {
            error = "checks must be a non-empty JSON array.";
            return false;
        }

        var list = new List<EvidenceCheckInput>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var seq = 0;
        foreach (var element in checksElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                error = "Each check must be a JSON object.";
                return false;
            }

            if (!TryRequiredString(element, "name", out var name, out error))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxIdLength)
            {
                error = "Each check name must be non-blank and at most 190 characters.";
                return false;
            }

            if (!names.Add(name))
            {
                error = $"Duplicate check name '{name}'.";
                return false;
            }

            if (!TryRequiredString(element, "severity", out var severity, out error))
            {
                return false;
            }

            if (!Severities.Contains(severity))
            {
                error = "Each check severity must be 'hard' or 'soft'.";
                return false;
            }

            if (!TryRequiredString(element, "status", out var status, out error))
            {
                return false;
            }

            if (!Statuses.Contains(status))
            {
                error = "Each check status must be 'pass', 'fail', 'unknown', or 'not_applicable'.";
                return false;
            }

            if (!TryOptionalString(element, "detail", out var detail, out error)
                || !TryOptionalObject(element, "data", out var data, out error))
            {
                return false;
            }

            list.Add(new EvidenceCheckInput(name, severity, status, detail, data, seq));
            seq++;
        }

        if (list.Count == 0)
        {
            error = "checks must be a non-empty JSON array.";
            return false;
        }

        checks = list;
        return true;
    }

    private static bool TryRequiredString(JsonElement parent, string name, out string value, out string? error)
    {
        value = string.Empty;
        error = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            error = $"{name} is required and must be a JSON string.";
            return false;
        }

        value = element.GetString()!;
        return true;
    }

    /// <summary>Optional string: absent or JSON null yields null; a non-string present value is 422.</summary>
    private static bool TryOptionalString(JsonElement parent, string name, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            error = $"{name} must be a JSON string when present.";
            return false;
        }

        value = element.GetString();
        return true;
    }

    /// <summary>Optional object: absent or JSON null yields null; any non-object present value is 422.</summary>
    private static bool TryOptionalObject(JsonElement parent, string name, out string? rawJson, out string? error)
    {
        rawJson = null;
        error = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"{name} must be a JSON object when present.";
            return false;
        }

        rawJson = element.GetRawText();
        return true;
    }

    private static bool TryRequiredUtcTimestamp(JsonElement parent, string name, out DateTime value, out string? error)
    {
        value = default;
        error = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            error = $"{name} is required and must be a UTC ISO 8601 timestamp string.";
            return false;
        }

        if (!TryParseUtc(element.GetString()!, out value))
        {
            error = $"{name} must be a UTC ISO 8601 timestamp (for example 2026-01-01T00:00:00Z).";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parses a UTC ISO 8601 instant. Requires an explicit UTC designator (<c>Z</c> or <c>+00:00</c>) so a
    /// bare local-style or offset-bearing timestamp is rejected deterministically as non-UTC.
    /// </summary>
    private static bool TryParseUtc(string value, out DateTime utc)
    {
        utc = default;
        if (!DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            return false;
        }

        var trimmed = value.TrimEnd();
        var utcDesignated = trimmed.EndsWith('Z') || trimmed.EndsWith("+00:00", StringComparison.Ordinal);
        if (!utcDesignated || dto.Offset != TimeSpan.Zero)
        {
            return false;
        }

        utc = dto.UtcDateTime;
        return true;
    }

    private static bool CheckId(string name, string value, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"{name} must not be blank.";
            return false;
        }

        if (value.Length > MaxIdLength)
        {
            error = $"{name} must be at most 190 characters.";
            return false;
        }

        error = null;
        return true;
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(HttpContext context, long limit, CancellationToken ct)
    {
        if (context.Request.ContentLength is long declared && declared > limit)
        {
            throw new BodyTooLargeException();
        }

        using var buffer = new MemoryStream();
        var rented = new byte[8192];
        var stream = context.Request.Body;
        int read;
        long total = 0;
        while ((read = await stream.ReadAsync(rented, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new BodyTooLargeException();
            }

            buffer.Write(rented, 0, read);
        }

        return buffer.ToArray();
    }

    private static IResult MalformedJson() => Results.Problem(
        title: "Malformed JSON",
        detail: "The request body is not well-formed JSON.",
        statusCode: StatusCodes.Status400BadRequest,
        type: "https://freeboard.dev/problems/malformed-json");

    private static IResult Semantic(string detail) => Results.Problem(
        title: "Validation failed",
        detail: detail,
        statusCode: StatusCodes.Status422UnprocessableEntity,
        type: "https://freeboard.dev/problems/validation");

    private static IResult Conflict() => Results.Problem(
        title: "Conflicting evidence",
        detail: "An evidence run already exists for this collector and run id with a different body.",
        statusCode: StatusCodes.Status409Conflict,
        type: "https://freeboard.dev/problems/conflict");

    private static IResult TooLarge() => Results.Problem(
        title: "Payload too large",
        detail: "The evidence payload exceeds the 1 MiB limit.",
        statusCode: StatusCodes.Status413PayloadTooLarge,
        type: "https://freeboard.dev/problems/payload-too-large");

    private static IResult Unreachable() => Results.Problem(
        title: "Evidence store unreachable",
        detail: "The evidence store could not be reached. Check the database connection.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private sealed record ValidatedRequest(
        string SchemaVersion,
        string CollectorId,
        string RunId,
        string? CollectorVersion,
        DateTime StartedAt,
        DateTime FinishedAt,
        IReadOnlyList<EvidenceCheckInput> Checks,
        string? Metadata);

    private sealed class BodyTooLargeException : Exception;

    /// <summary>Declares the per-endpoint body cap so Kestrel enforces it in production deployments.</summary>
    private sealed class RequestSizeLimit(long bytes) : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize { get; } = bytes;
    }
}
