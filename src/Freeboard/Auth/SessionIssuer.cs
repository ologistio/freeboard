using Freeboard.Persistence.Auth;
using Microsoft.Extensions.Options;

namespace Freeboard.Auth;

/// <summary>
/// Mints an opaque bearer session and persists it. The token is prefix-bearing
/// (<c>v&lt;keyId&gt;.&lt;secret&gt;</c>) via <see cref="ITokenHasher"/>; only its keyed-HMAC hash
/// is stored. Used by login, the force-reset password set, and bootstrap so token issuance is
/// in one place.
/// </summary>
public sealed class SessionIssuer(
    ITokenHasher tokenHasher,
    ISessionStore sessionStore,
    IOptions<WebAuthOptions> options)
{
    private readonly WebAuthOptions _options = options.Value;

    /// <summary>
    /// Creates a session for the user and returns the once-only wire token plus the row. The caller
    /// passes the credential epoch that was actually VERIFIED in this flow - NOT re-read here -
    /// so a password change between verify and issue invalidates this session via the bearer-handler
    /// epoch check rather than silently being accepted under the current epoch.
    /// </summary>
    public async Task<(string Token, SessionRow Session)> IssueAsync(
        string userId, SessionAuthState authState, int credentialVersion, CancellationToken cancellationToken = default)
    {
        var minted = tokenHasher.MintPrefixed();
        var expiresAt = DateTime.UtcNow + _options.SessionLifetime;
        var session = await sessionStore.CreateAsync(
            userId, minted.Hash, minted.KeyVersion, authState, credentialVersion, expiresAt, cancellationToken)
            .ConfigureAwait(false);
        return (minted.Token, session);
    }
}
