using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace Freeboard.Web;

/// <summary>
/// Holds the login MFA token server-side, keyed by an opaque nonce, so it never travels to the
/// browser. Login returns a body-only <c>mfa_token</c> that must never become a bearer; the page
/// funnel stashes it here and puts only the nonce in a short-lived Lax cookie. The page reads the
/// nonce, looks the token up here, and completes the verify. A stolen nonce is useless without this
/// server entry.
///
/// In-process and backed by <see cref="IMemoryCache"/> with a short TTL, mirroring
/// <c>WebAuthnEnrollmentStore</c>. A multi-instance non-sticky deployment can miss the entry on the
/// magic-link round-trip (the landing then shows a restart message), so it needs sticky sessions or
/// a later distributed-cache swap.
/// </summary>
public sealed class PendingMfaStore(IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>The held mfa_token plus the validated local target to resume after MFA completes.</summary>
    public sealed record Pending(string MfaToken, string? ReturnUrl);

    /// <summary>
    /// Stashes the mfa_token (and the validated local returnUrl, so it survives the magic-link email
    /// round-trip the same way the mfa_token does) and returns the nonce to put in the client cookie.
    /// </summary>
    public string Stash(string mfaToken, string? returnUrl)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        cache.Set(Key(nonce), new Pending(mfaToken, returnUrl), Ttl);
        return nonce;
    }

    /// <summary>The held mfa_token for a nonce, or null when absent/expired. Does not consume it.</summary>
    public string? Peek(string? nonce) => PeekPending(nonce)?.MfaToken;

    /// <summary>The held pending state (mfa_token + returnUrl), or null when absent/expired.</summary>
    public Pending? PeekPending(string? nonce)
        => !string.IsNullOrEmpty(nonce) && cache.TryGetValue(Key(nonce), out Pending? pending) ? pending : null;

    /// <summary>Removes the entry for a nonce (after the challenge resolves or restarts).</summary>
    public void Remove(string? nonce)
    {
        if (!string.IsNullOrEmpty(nonce))
        {
            cache.Remove(Key(nonce));
        }
    }

    private static string Key(string nonce) => $"pending-mfa:{nonce}";
}
