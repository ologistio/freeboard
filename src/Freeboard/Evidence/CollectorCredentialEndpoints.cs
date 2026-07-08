using System.Globalization;
using System.Text.Json.Serialization;
using Freeboard.Api;
using Freeboard.Authz;
using Freeboard.Compliance;
using Freeboard.Core.Authz;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;

namespace Freeboard.Evidence;

/// <summary>
/// Admin API to issue and revoke per-collector machine credentials. Gated by
/// <c>RequirePermission(system.admin, alwaysEnforce: true)</c> (the same gate as the custom-role
/// designer), so it reuses the existing system-admin action with no new authz wiring. The routes carry
/// NO ingest marker, so GitOps read-only mode 409s them: minting a credential is an admin config action.
/// Issuance reuses <see cref="ITokenHasher.MintPrefixed"/>; the raw token is returned exactly once.
/// </summary>
public static class CollectorCredentialEndpoints
{
    public sealed record IssueRequest(
        [property: JsonPropertyName("expires_at")] string? ExpiresAt);

    public static void MapCollectorCredentialEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(ApiRoutes.ApiRoutePrefix).RequireAuthorization();
        group.RequirePermission(AuthzActions.SystemAdmin, AuthzSelectors.System, alwaysEnforce: true);

        group.MapPost("/evidence-collectors/{id}/credentials", IssueAsync);
        group.MapDelete("/evidence-collectors/{id}/credentials/{credId}", RevokeAsync);
    }

    private static async Task<IResult> IssueAsync(
        string id, IssueRequest? body, IComplianceStore reads, ICollectorCredentialStore credentials,
        ITokenHasher tokenHasher, CancellationToken ct)
    {
        DateTime? expiresAt = null;
        if (body?.ExpiresAt is { Length: > 0 } raw)
        {
            if (!DateTimeOffset.TryParse(
                    raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return ApiResponses.ValidationProblem("expires_at", "expires_at must be an ISO 8601 timestamp.");
            }

            expiresAt = parsed.UtcDateTime;
        }

        bool exists;
        try
        {
            exists = (await reads.GetEvidenceCollectorsAsync(ct).ConfigureAwait(false))
                .Any(c => string.Equals(c.Id, id, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ComplianceEndpoints.IsStoreFailure(ex))
        {
            return Unreachable();
        }

        if (!exists)
        {
            return ApiResponses.ValidationProblem(
                "collector_id", $"Evidence-collector '{id}' does not exist.");
        }

        var minted = tokenHasher.MintPrefixed();
        try
        {
            var credentialId = await credentials
                .IssueAsync(id, minted.Hash, minted.KeyVersion, expiresAt, ct).ConfigureAwait(false);
            return Results.Json(
                new
                {
                    credential_id = credentialId,
                    collector_id = id,
                    token = minted.Token,
                    expires_at = expiresAt is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : (DateTime?)null,
                },
                statusCode: StatusCodes.Status201Created);
        }
        catch (Exception ex) when (ComplianceEndpoints.IsStoreFailure(ex))
        {
            return Unreachable();
        }
    }

    private static async Task<IResult> RevokeAsync(
        string id, string credId, ICollectorCredentialStore credentials, CancellationToken ct)
    {
        try
        {
            var revoked = await credentials.RevokeAsync(id, credId, ct).ConfigureAwait(false);
            return revoked ? Results.NoContent() : Results.NotFound();
        }
        catch (Exception ex) when (ComplianceEndpoints.IsStoreFailure(ex))
        {
            return Unreachable();
        }
    }

    private static IResult Unreachable() => Results.Problem(
        title: "Evidence store unreachable",
        detail: "The evidence store could not be reached. Check the database connection.",
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
