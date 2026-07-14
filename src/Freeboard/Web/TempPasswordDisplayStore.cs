using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace Freeboard.Web;

/// <summary>
/// Holds a freshly generated one-time temporary password server-side, keyed by an opaque nonce, so an
/// admin can be shown it exactly once after a user create or a reset-password. The plaintext never
/// travels in a URL, a client-readable field, or a client-held cookie: only the nonce sits in a
/// short-lived cookie, and the display page reads-and-clears the entry, so a refresh shows nothing.
///
/// In-process and backed by <see cref="IMemoryCache"/> with a short TTL, mirroring
/// <see cref="RecoveryCodeDisplayStore"/>. A multi-instance non-sticky deployment can miss the entry
/// on the redirect to the display page; the page then shows nothing, which is the safe failure (the
/// admin re-runs the action rather than seeing a stale value).
/// </summary>
public sealed class TempPasswordDisplayStore(IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    // Browser cookie paths are case-sensitive, so the redirect target, the cookie Path, and the
    // route the display page declares must all be this exact lowercase string.
    /// <summary>The display page route, also the cookie path so the nonce only rides that one page.</summary>
    public const string DisplayPath = "/settings/usercredential";

    /// <summary>Stashes the temp password and returns the nonce to put in the client cookie.</summary>
    public string Stash(string temporaryPassword)
    {
        var nonce = OpaqueHandle.New();
        // A StrongBox lets Take claim the value with a single atomic Interlocked.Exchange, so two
        // concurrent same-nonce reads cannot both walk away with the one-time password.
        cache.Set(Key(nonce), new StrongBox<string?>(temporaryPassword), Ttl);
        return nonce;
    }

    /// <summary>
    /// Stashes the temp password, sets the path-scoped nonce cookie on <paramref name="response"/>,
    /// and returns the display-page route to redirect to.
    /// </summary>
    public string StashAndRedirectTarget(HttpResponse response, string temporaryPassword)
    {
        var nonce = Stash(temporaryPassword);
        SessionCookie.SetTransient(response, SessionCookie.AdminTempPasswordName, nonce, DisplayPath, Ttl);
        return DisplayPath;
    }

    /// <summary>
    /// Reads and removes the temp password for a nonce, or null when absent/expired/already shown. The
    /// read and remove are atomic, so two concurrent requests with the same nonce cannot both observe
    /// the value: exactly one wins the removal and the other sees nothing.
    /// </summary>
    public string? Take(string? nonce)
        => string.IsNullOrEmpty(nonce) ? null
            : cache.TryGetValue(Key(nonce), out var holder)
              && holder is StrongBox<string?> box
              && Interlocked.Exchange(ref box.Value, null) is { } value
                ? value
                : null;

    private static string Key(string nonce) => $"temp-password-display:{nonce}";
}
