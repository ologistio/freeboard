using System.Globalization;
using System.Text;
using System.Text.Json;
using Freeboard.Api;
using Freeboard.Auth;
using Freeboard.Compliance;
using Freeboard.Core.GitOps;
using Freeboard.Persistence;
using Microsoft.AspNetCore.Http.Metadata;

namespace Freeboard.Evidence;

/// <summary>
/// The runtime Evidence ingest endpoint: <c>POST /api/v1/freeboard/evidence</c>, authenticated by a
/// per-collector machine credential (the collector bearer scheme via the named ingest policy). The body
/// is handled MANUALLY, not via <c>[FromBody]</c> binding, so a JSON type mismatch on ANY field is a 422,
/// not a framework 400. The one rule: parse the untrusted body into a <see cref="JsonElement"/> tree
/// (which throws only on malformed JSON -> 400) and read every field from that tree in the semantic pass
/// (every value/type problem -> 422); project into the typed store input only after validation passes.
///
/// The write path is the shared append-only <see cref="IEvidenceWriteStore"/>. The contract's
/// <c>organisation_id</c> and <c>requirement_id</c> are payload-declared and validated against the
/// collector's registration: the collector's control must map to the requirement, and the organisation
/// must resolve <c>In</c> for the requirement's standard in the Statement of Applicability. A store
/// conflict (the <c>(vendor, collector_ref)</c> idempotency key) is an accepted replay -> 200, not 409.
/// </summary>
public static class EvidenceIngestEndpoints
{
    /// <summary>The only accepted contract version string.</summary>
    public const string SchemaVersion = "freeboard.evidence.v1";

    /// <summary>Per-endpoint request body cap (1 MiB). Evidence JSON is small; this is generous.</summary>
    public const long MaxBodyBytes = 1_048_576;

    private const int MaxIdLength = 190;

    /// <summary>
    /// Per-check detail cap. main's evidence_checks.detail is MySQL TEXT (~64 KB), so bound the value
    /// here to reject an over-long detail as a clean 422 rather than letting it fail at the DB as a 503.
    /// </summary>
    private const int MaxDetailLength = 4096;

    private static readonly HashSet<string> Severities = new(StringComparer.Ordinal) { "hard", "soft" };

    private static readonly HashSet<string> CheckResults = new(StringComparer.Ordinal) { "pass", "fail" };

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
        HttpContext context, IComplianceStore reads, IEvidenceWriteStore store)
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

        // 3. Parse to a JsonElement tree. Throws ONLY on malformed JSON (-> 400); a JSON type mismatch on
        // any field is data the parser accepts and is caught as a 422 in step 4.
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
            // 4. Semantic validation. Any wrong ValueKind or value is 422 (never 400).
            if (Validate(document.RootElement, out var validated, out var error) is false)
            {
                return Semantic(error!);
            }

            // The credential is authoritative: the body collector_id must match the authenticated collector.
            var credentialCollector = context.User.FindFirst(CollectorBearerAuthenticationHandler.CollectorIdClaim)?.Value;
            if (!string.Equals(credentialCollector, validated!.CollectorId, StringComparison.Ordinal))
            {
                return Semantic("collector_id does not match the authenticated credential.");
            }

            // 5. Resolve the registration and scope. Every well-formed request that cannot be satisfied
            // against the collector's current registration is a 422.
            EvidenceCollectorRow collector;
            string vendor;
            try
            {
                var collectors = await reads.GetEvidenceCollectorsAsync(ct).ConfigureAwait(false);
                var found = collectors.FirstOrDefault(c =>
                    string.Equals(c.Id, validated.CollectorId, StringComparison.Ordinal));
                if (found is null)
                {
                    return Semantic($"collector_id '{validated.CollectorId}' is not a registered evidence-collector.");
                }

                collector = found;

                // main's evidence_runs.vendor is NOT NULL and half the idempotency key; a collector with
                // no vendor cannot ingest. Reject with an operator-actionable detail; do NOT synthesise one.
                if (string.IsNullOrEmpty(collector.Vendor))
                {
                    return MissingVendor(validated.CollectorId);
                }

                vendor = collector.Vendor;

                // requirement_id must be one of the collector control's resolved requirement ids.
                var controls = await reads.GetControlsAsync(ct).ConfigureAwait(false);
                var control = controls.FirstOrDefault(c =>
                    string.Equals(c.Id, collector.Control, StringComparison.Ordinal));
                if (control is null || !control.MapsTo.Contains(validated.RequirementId, StringComparer.Ordinal))
                {
                    return Semantic(
                        $"requirement_id '{validated.RequirementId}' is not mapped by the collector's control.");
                }

                // (organisation_id, requirement_id) must resolve In in the Statement of Applicability for
                // the requirement's owning standard.
                var soa = await reads.GetStatementOfApplicabilityInputsAsync(ct).ConfigureAwait(false);
                var requirement = soa.Requirements.FirstOrDefault(r =>
                    string.Equals(r.Id, validated.RequirementId, StringComparison.Ordinal));
                if (requirement is null)
                {
                    return Semantic($"requirement_id '{validated.RequirementId}' is not a known requirement.");
                }

                if (!IsOrganisationInScope(soa, requirement.Standard, validated.OrganisationId, validated.RequirementId))
                {
                    return Semantic(
                        $"organisation_id '{validated.OrganisationId}' is not in scope for requirement "
                        + $"'{validated.RequirementId}'.");
                }
            }
            catch (Exception ex) when (ComplianceEndpoints.IsStoreFailure(ex))
            {
                return Unreachable();
            }

            // 6. Namespace the run under the collector so idempotency is collector-scoped; the column is
            // VARCHAR(190). collector_id and run_id are validated to exclude ':' so this composition is
            // unambiguous (no two distinct tuples can collapse to the same collector_ref).
            var collectorRef = $"{validated.CollectorId}:{validated.RunId}";
            if (collectorRef.Length > MaxIdLength)
            {
                return Semantic($"collector_id and run_id together must be at most {MaxIdLength} characters.");
            }

            var hardFailCount = validated.Checks.Count(c => c.Severity == "hard" && c.Result == "fail");
            var softFailCount = validated.Checks.Count(c => c.Severity == "soft" && c.Result == "fail");
            var totalCount = validated.Checks.Count;

            // Run-level verdict is derived, not posted: Fail iff any Hard check Fails.
            var runResult = hardFailCount > 0 ? "Fail" : "Pass";

            var run = new NewEvidenceRun(
                OrganisationId: validated.OrganisationId,
                RequirementId: validated.RequirementId,
                Vendor: vendor,
                CollectorRef: collectorRef,
                Result: runResult,
                CollectedAt: validated.CollectedAt,
                ReceivedAt: DateTime.UtcNow,
                RawPayload: Encoding.UTF8.GetString(body),
                Checks: validated.Checks
                    .Select(c => new NewEvidenceCheck(c.Name, MapSeverity(c.Severity), MapResult(c.Result), c.Detail))
                    .ToList(),
                CollectorId: validated.CollectorId,
                Frequency: string.IsNullOrWhiteSpace(collector.Frequency) ? null : collector.Frequency);

            WriteResult result;
            try
            {
                result = await store.AppendEvidenceAsync(run, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ComplianceEndpoints.IsStoreFailure(ex))
            {
                return Unreachable();
            }

            var payload = new
            {
                collector_id = validated.CollectorId,
                run_id = validated.RunId,
                hard_fail_count = hardFailCount,
                soft_fail_count = softFailCount,
                total_count = totalCount,
            };

            if (result.Ok)
            {
                return Results.Json(payload, statusCode: StatusCodes.Status201Created);
            }

            // A duplicate (vendor, collector_ref) is an accepted replay (200); the store readback carries
            // no evidence id, so the body echoes only request-derived values. Any other store error is a
            // value problem the pre-validation did not catch -> 422.
            return result.IsConflict
                ? Results.Json(payload, statusCode: StatusCodes.Status200OK)
                : Semantic(result.Error!);
        }
    }

    /// <summary>
    /// The organisation node for <paramref name="organisationId"/> must exist, resolve <c>In</c> for the
    /// standard, and the requirement must not be an <c>Out</c> deviation on that node.
    /// </summary>
    private static bool IsOrganisationInScope(
        SoaInputs soa, string standardId, string organisationId, string requirementId)
    {
        var nodes = StatementOfApplicability.Resolve(
            soa.Organisations, soa.Scopes, soa.Requirements, soa.RequirementScopes, standardId);
        var node = nodes.FirstOrDefault(n => string.Equals(n.Id, organisationId, StringComparison.Ordinal));
        if (node is null || !string.Equals(node.Disposition, nameof(ScopeDisposition.In), StringComparison.Ordinal))
        {
            return false;
        }

        var deviation = node.Requirements.FirstOrDefault(r =>
            string.Equals(r.Requirement, requirementId, StringComparison.Ordinal));
        return deviation is null
            || !string.Equals(deviation.Disposition, nameof(ScopeDisposition.Out), StringComparison.Ordinal);
    }

    private static string MapSeverity(string wire) =>
        string.Equals(wire, "hard", StringComparison.Ordinal) ? "Hard" : "Soft";

    private static string MapResult(string wire) =>
        string.Equals(wire, "pass", StringComparison.Ordinal) ? "Pass" : "Fail";

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
            || !CheckId("collector_id", collectorId, out error)
            || !CheckNoRefDelimiter("collector_id", collectorId, out error))
        {
            return false;
        }

        if (!TryRequiredString(root, "organisation_id", out var organisationId, out error)
            || !CheckId("organisation_id", organisationId, out error))
        {
            return false;
        }

        if (!TryRequiredString(root, "requirement_id", out var requirementId, out error)
            || !CheckId("requirement_id", requirementId, out error))
        {
            return false;
        }

        if (!TryRequiredString(root, "run_id", out var runId, out error)
            || !CheckId("run_id", runId, out error)
            || !CheckNoRefDelimiter("run_id", runId, out error))
        {
            return false;
        }

        if (!TryOptionalString(root, "collector_version", out _, out error))
        {
            return false;
        }

        if (!TryRequiredUtcTimestamp(root, "collected_at", out var collectedAt, out error))
        {
            return false;
        }

        if (!TryValidateChecks(root, out var checks, out error))
        {
            return false;
        }

        if (!TryOptionalObject(root, "metadata", out error))
        {
            return false;
        }

        validated = new ValidatedRequest(collectorId, organisationId, requirementId, runId, collectedAt, checks);
        return true;
    }

    private static bool TryValidateChecks(
        JsonElement root, out IReadOnlyList<ValidatedCheck> checks, out string? error)
    {
        checks = [];
        error = null;

        if (!root.TryGetProperty("checks", out var checksElement) || checksElement.ValueKind != JsonValueKind.Array)
        {
            error = "checks must be a non-empty JSON array.";
            return false;
        }

        var list = new List<ValidatedCheck>();
        var names = new HashSet<string>(StringComparer.Ordinal);
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

            if (!TryRequiredString(element, "result", out var result, out error))
            {
                return false;
            }

            if (!CheckResults.Contains(result))
            {
                error = "Each check result must be 'pass' or 'fail'.";
                return false;
            }

            if (!TryOptionalString(element, "detail", out var detail, out error))
            {
                return false;
            }

            if (detail is not null && detail.Length > MaxDetailLength)
            {
                error = $"Each check detail must be at most {MaxDetailLength} characters.";
                return false;
            }

            list.Add(new ValidatedCheck(name, severity, result, detail));
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

    /// <summary>Optional object: absent or JSON null is accepted; any non-object present value is 422.</summary>
    private static bool TryOptionalObject(JsonElement parent, string name, out string? error)
    {
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

    /// <summary>
    /// Rejects the ':' character in a value that composes <c>collector_ref</c> = "collector_id:run_id".
    /// Without this, a ':' inside either field makes the composition ambiguous - e.g. ("a:b","c") and
    /// ("a","b:c") both yield "a:b:c" - so two distinct runs would collide on the (vendor, collector_ref)
    /// idempotency key and the second would be dropped as a 200 replay.
    /// </summary>
    private static bool CheckNoRefDelimiter(string name, string value, out string? error)
    {
        if (value.Contains(':', StringComparison.Ordinal))
        {
            error = $"{name} must not contain ':' (it is the collector_id:run_id delimiter).";
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

    private static IResult MissingVendor(string collectorId) => Results.Problem(
        title: "Collector not configured for ingest",
        detail: $"Evidence-collector '{collectorId}' has no vendor set. Set its vendor in GitOps config before ingest.",
        statusCode: StatusCodes.Status422UnprocessableEntity,
        type: "https://freeboard.dev/problems/collector-missing-vendor");

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
        string CollectorId,
        string OrganisationId,
        string RequirementId,
        string RunId,
        DateTime CollectedAt,
        IReadOnlyList<ValidatedCheck> Checks);

    private sealed record ValidatedCheck(string Name, string Severity, string Result, string? Detail);

    private sealed class BodyTooLargeException : Exception;

    /// <summary>Declares the per-endpoint body cap so Kestrel enforces it in production deployments.</summary>
    private sealed class RequestSizeLimit(long bytes) : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize { get; } = bytes;
    }
}
