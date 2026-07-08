using System.Security.Claims;
using System.Text.Encodings.Web;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Freeboard.Auth;

/// <summary>
/// Per-collector machine-credential bearer authentication for the Evidence ingest route. A SECOND
/// scheme beside <see cref="BearerAuthenticationHandler"/> (untouched): it shares the wire format
/// <c>v&lt;keyId&gt;.&lt;secret&gt;</c> but queries only <c>collector_credentials</c>, so a human session
/// token presented here misses the lookup and a collector token presented at a session endpoint misses
/// there - each scheme rejects the other's token with 401.
///
/// A handler that returns <see cref="AuthenticateResult.Fail"/> yields 401, never 403: ASP.NET Core only
/// emits 403 when authentication SUCCEEDS and a later authorization requirement denies. So:
/// - missing / malformed / unknown-key / hash-not-found / key-version-mismatch -> <c>Fail</c> (401);
/// - a RECOGNISED credential authenticates SUCCESSFULLY, carrying the collector-id claim, plus an
///   <see cref="ActiveClaim"/> ONLY when it is neither revoked nor expired. A revoked/expired credential
///   still authenticates but WITHOUT the active claim, so the named ingest policy (which requires both
///   claims) denies it -> Forbid -> 403.
/// On success it best-effort touches last-seen so operators see real collector activity; a failure there
/// never fails the already-authenticated request.
/// </summary>
public sealed class CollectorBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ITokenHasher tokenHasher,
    ICollectorCredentialStore credentialStore)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>The collector bearer authentication scheme name.</summary>
    public const string SchemeName = "FreeboardCollectorBearer";

    /// <summary>The named authorization policy the ingest route binds to this scheme.</summary>
    public const string IngestPolicyName = "FreeboardEvidenceIngest";

    /// <summary>Claim type carrying the authenticated collector's id.</summary>
    public const string CollectorIdClaim = "freeboard:collector_id";

    /// <summary>Claim type carrying the authenticating credential's id.</summary>
    public const string CredentialIdClaim = "freeboard:collector_credential_id";

    /// <summary>Claim type present only when the credential is currently usable (not revoked, not expired).</summary>
    public const string ActiveClaim = "freeboard:collector_active";

    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrEmpty(header) || !header.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            // No bearer presented: NoResult so other schemes/anonymous endpoints still work.
            return AuthenticateResult.NoResult();
        }

        var token = header[BearerPrefix.Length..].Trim();

        // Malformed/unknown-key -> uniform 401, NO DB lookup.
        if (!tokenHasher.TryHashPrefixed(token, out var hash, out var keyVersion))
        {
            return Fail();
        }

        var credential = await credentialStore.FindByTokenHashAsync(hash, Context.RequestAborted).ConfigureAwait(false);
        if (credential is null)
        {
            return Fail();
        }

        // Integrity: the parsed key id must equal the stored version.
        if (credential.TokenKeyVersion != keyVersion)
        {
            return Fail();
        }

        // Best-effort last-seen update. A failure here must never fail the request (the auth decision is
        // already made), so swallow it (same pattern as BearerAuthenticationHandler).
        try
        {
            await credentialStore.TouchLastSeenAsync(credential.Id, DateTime.UtcNow, Context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-fatal: last-seen is informational, not part of the auth decision.
        }

        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(CollectorIdClaim, credential.CollectorId));
        identity.AddClaim(new Claim(CredentialIdClaim, credential.Id));

        // The active claim gates the ingest policy: a revoked or expired credential authenticates but
        // omits it, so the policy Forbids (403) rather than the handler faking a 403 from Fail (which
        // can only produce 401).
        if (IsActive(credential, DateTime.UtcNow))
        {
            identity.AddClaim(new Claim(ActiveClaim, "true"));
        }

        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private static bool IsActive(CollectorCredentialRow credential, DateTime now) =>
        credential.RevokedAt is null && (credential.ExpiresAt is null || credential.ExpiresAt > now);

    /// <summary>Uniform 401: never reveals which condition failed.</summary>
    private static AuthenticateResult Fail() => AuthenticateResult.Fail("Invalid or expired collector token.");
}
