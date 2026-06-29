using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Freeboard.Auth;

/// <summary>
/// Freeboard opaque-bearer authentication. Reads
/// <c>Authorization: Bearer &lt;token&gt;</c>, parses the key id from the <c>v&lt;keyId&gt;.</c>
/// prefix, and:
/// - rejects a missing/malformed/unknown-key token with a uniform 401 and NO DB lookup (no
///   valid hash can be computed);
/// - HMACs the token via <see cref="ITokenHasher"/> and looks the session up by token_hash;
/// - integrity-asserts the stored token_key_version equals the parsed key id (mismatch -> 401);
/// - rejects a missing/expired session and a disabled user with 401;
/// - rejects a session whose stored credential epoch is stale, so a password change
///   invalidates prior-epoch sessions race-free;
/// - rejects an MFA challenge token presented as a bearer (it is not a session row, so the
///   session lookup misses -> 401);
/// - on success builds a principal carrying the user id, session id, role, and auth-state.
/// The force-reset (limited) allowlist is enforced after auth by
/// <see cref="LimitedSessionGuardMiddleware"/>.
/// </summary>
public sealed class BearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ITokenHasher tokenHasher,
    ISessionStore sessionStore,
    IUserStore userStore,
    IPasswordCredentialStore credentialStore)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
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

        var session = await sessionStore.FindByTokenHashAsync(hash, Context.RequestAborted).ConfigureAwait(false);
        if (session is null)
        {
            return Fail();
        }

        // Integrity: the parsed key id must equal the stored version.
        if (session.TokenKeyVersion != keyVersion)
        {
            return Fail();
        }

        if (session.ExpiresAt <= DateTime.UtcNow)
        {
            return Fail();
        }

        var user = await userStore.GetByIdAsync(session.UserId, Context.RequestAborted).ConfigureAwait(false);
        if (user is null || !user.Enabled)
        {
            return Fail();
        }

        // Reject a session whose stored credential epoch is stale. A password change bumps
        // user_password_credentials.credential_version; any session minted under the prior epoch is
        // invalidated here, even one a racing login inserted just after a revoke DELETE.
        var credential = await credentialStore.GetAsync(session.UserId, Context.RequestAborted).ConfigureAwait(false);
        if (credential is null || credential.CredentialVersion != session.CredentialVersion)
        {
            return Fail();
        }

        // Best-effort update of sessions.last_seen_at so session listings show real activity.
        // A failure here must never fail the request (the auth decision is already made), so swallow.
        try
        {
            await sessionStore.TouchLastSeenAsync(session.Id, DateTime.UtcNow, Context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-fatal: last-seen is informational, not part of the auth decision.
        }

        var identity = new ClaimsIdentity(AuthClaims.Scheme);
        identity.AddClaim(new Claim(AuthClaims.UserId, user.Id));
        identity.AddClaim(new Claim(AuthClaims.SessionId, session.Id));
        identity.AddClaim(new Claim(AuthClaims.Role, user.GlobalRole));
        identity.AddClaim(new Claim(
            AuthClaims.AuthState, ((int)session.AuthState).ToString(CultureInfo.InvariantCulture)));

        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, AuthClaims.Scheme));
    }

    /// <summary>Uniform 401: never reveals which condition failed.</summary>
    private static AuthenticateResult Fail() => AuthenticateResult.Fail("Invalid or expired token.");
}
