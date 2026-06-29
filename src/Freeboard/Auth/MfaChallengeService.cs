using Freeboard.Persistence.Auth;
using Microsoft.Extensions.Options;

namespace Freeboard.Auth;

/// <summary>
/// Orchestrates the MFA login challenge: mint a keyed-hashed challenge token with the user's
/// available factors (stashing WebAuthn assertion options on the row when passkey is offered),
/// then on a verify resolve the challenge, enforce the attempt cap, consume it, and issue a full
/// session. Wraps <see cref="IMfaChallengeStore"/> so the verify endpoints share one flow.
/// </summary>
public sealed class MfaChallengeService(
    IMfaChallengeStore challenges,
    MfaFactorService factors,
    WebAuthnCeremony webAuthn,
    SessionIssuer sessions,
    IPasswordCredentialStore credentials,
    IOptions<WebAuthOptions> options)
{
    private readonly WebAuthOptions _options = options.Value;

    /// <summary>
    /// Mints a challenge for a freshly-password-authenticated MFA user, recording the credential
    /// epoch VERIFIED at the password step. Returns the body-only token and factor list.
    /// </summary>
    public async Task<(string MfaToken, IReadOnlyList<string> Factors)> BeginChallengeAsync(
        UserRow user, int verifiedCredentialVersion, CancellationToken ct = default)
    {
        var available = await factors.AvailableAsync(user, ct).ConfigureAwait(false);

        // If passkey is offered, generate the assertion options now and store them on the row so the
        // /auth/mfa/passkey/options + verify pair correlate to this challenge (multi-instance safe).
        string? webAuthnOptions = null;
        if (available.Contains(MfaFactors.Passkey))
        {
            webAuthnOptions = await webAuthn.BeginAssertionAsync(user.Id, ct).ConfigureAwait(false);
        }

        var expiresAt = DateTime.UtcNow + _options.MfaChallengeLifetime;
        var minted = await challenges
            .CreateAsync(user.Id, verifiedCredentialVersion, string.Join(",", available), webAuthnOptions, expiresAt, ct)
            .ConfigureAwait(false);
        return (minted.Token, available);
    }

    /// <summary>Resolves an unconsumed, unexpired challenge by its token, or null.</summary>
    public Task<MfaChallengeRow?> ResolveAsync(string mfaToken, CancellationToken ct = default)
        => challenges.FindByTokenAsync(mfaToken, DateTime.UtcNow, ct);

    /// <summary>
    /// Records a failed verify attempt. Returns true when the cap was reached and the challenge is
    /// now consumed (the caller restarts at login).
    /// </summary>
    public Task<bool> RegisterFailureAsync(string challengeId, CancellationToken ct = default)
        => challenges.RegisterFailedAttemptAsync(challengeId, _options.MfaMaxAttempts, ct);

    /// <summary>
    /// Re-checks the credential epoch, consumes the challenge, and issues a FULL session under
    /// the VERIFIED epoch. If the user's password changed after the 202 (current epoch differs from
    /// the challenge's stored epoch), the challenge is consumed and null is returned (the caller maps
    /// to 401 - the password proof is stale). Returns null if the challenge was already consumed.
    /// </summary>
    public async Task<string?> CompleteAsync(MfaChallengeRow challenge, CancellationToken ct = default)
    {
        if (!await challenges.ConsumeAsync(challenge.Id, DateTime.UtcNow, ct).ConfigureAwait(false))
        {
            return null;
        }

        // The consume above guarantees single-use; now reject a mid-flow password change. The
        // challenge is already consumed, so a retry restarts at login.
        var credential = await credentials.GetAsync(challenge.UserId, ct).ConfigureAwait(false);
        if (credential is null || credential.CredentialVersion != challenge.CredentialVersion)
        {
            return null;
        }

        var (token, _) = await sessions
            .IssueAsync(challenge.UserId, SessionAuthState.Full, challenge.CredentialVersion, ct).ConfigureAwait(false);
        return token;
    }

    public int MaxSends => _options.MagicLinkMaxSends;

    public TimeSpan MagicLinkLifetime => _options.MagicLinkLifetime;
}
