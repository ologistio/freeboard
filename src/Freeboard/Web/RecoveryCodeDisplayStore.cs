using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace Freeboard.Web;

/// <summary>
/// Holds a freshly generated recovery-code set server-side, keyed by an opaque nonce, so the codes
/// can be shown exactly once after a first-factor enrollment or a regenerate. The codes never travel
/// in a URL, a client-readable field, or a client-held cookie: only the nonce sits in a short-lived
/// cookie, and the display page reads-and-clears the entry, so a refresh shows nothing.
///
/// In-process and backed by <see cref="IMemoryCache"/> with a short TTL, mirroring
/// <c>PendingMfaStore</c>/<c>WebAuthnEnrollmentStore</c>. A multi-instance non-sticky deployment can
/// miss the entry on the redirect to the display page; the page then shows nothing, which is the safe
/// failure (the user re-generates rather than seeing stale codes).
/// </summary>
public sealed class RecoveryCodeDisplayStore(IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>The display page route, also the cookie path so the nonce only rides that one page.</summary>
    public const string DisplayPath = "/account/mfa/recovery-codes";

    /// <summary>Stashes the codes and returns the nonce to put in the client cookie.</summary>
    public string Stash(IReadOnlyList<string> codes)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        // A StrongBox lets Take claim the codes with a single atomic Interlocked.Exchange, so two
        // concurrent same-nonce reads cannot both walk away with the one-time codes.
        cache.Set(Key(nonce), new StrongBox<IReadOnlyList<string>?>(codes), Ttl);
        return nonce;
    }

    /// <summary>
    /// Stashes the codes, sets the path-scoped nonce cookie on <paramref name="response"/>, and returns
    /// the display-page route to redirect to. Shared by the passkey/TOTP/recovery handlers so the
    /// one-time display works the same way for every first-factor activation and regenerate.
    /// </summary>
    public string StashAndRedirectTarget(HttpResponse response, IReadOnlyList<string> codes)
    {
        var nonce = Stash(codes);
        SessionCookie.SetTransient(response, SessionCookie.RecoveryDisplayName, nonce, DisplayPath, Ttl);
        return DisplayPath;
    }

    /// <summary>
    /// Reads and removes the codes for a nonce, or null when absent/expired/already shown. The read
    /// and remove are atomic, so two concurrent requests with the same nonce cannot both observe the
    /// codes: exactly one wins the removal and the other sees nothing.
    /// </summary>
    public IReadOnlyList<string>? Take(string? nonce)
        => string.IsNullOrEmpty(nonce) ? null
            : cache.TryGetValue(Key(nonce), out var holder)
              && holder is StrongBox<IReadOnlyList<string>?> box
              && Interlocked.Exchange(ref box.Value, null) is { } codes
                ? codes
                : null;

    private static string Key(string nonce) => $"recovery-display:{nonce}";
}
