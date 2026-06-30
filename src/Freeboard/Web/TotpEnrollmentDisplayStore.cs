using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace Freeboard.Web;

/// <summary>
/// Holds the staged TOTP provisioning URI server-side, keyed by an opaque nonce, for the duration of
/// one enrollment attempt. Enrollment is begun by an explicit POST so a bare GET has no side effect:
/// generating the secret on a GET would re-stage (rotate) it on every page load. Once begun, the URI
/// is read back unchanged on each activation retry, so a wrong confirming code does NOT rotate the
/// staged secret and invalidate the QR the user already scanned. The store only PEEKS on read (it does
/// not consume), unlike the one-time recovery-codes display; the entry is cleared explicitly when
/// activation succeeds or the user restarts.
///
/// Only the nonce sits in a short-lived cookie; the provisioning URI (which embeds the base32 secret)
/// never travels in a URL, a hidden field, or a client-readable field. In-process and backed by
/// <see cref="IMemoryCache"/> with a short TTL, mirroring <c>RecoveryCodeDisplayStore</c>.
/// </summary>
public sealed class TotpEnrollmentDisplayStore(IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    /// <summary>The TOTP enroll page route, also the cookie path so the nonce only rides that page.</summary>
    public const string EnrollPath = "/account/mfa/totp";

    /// <summary>Stashes the provisioning URI, sets the path-scoped nonce cookie, and returns the nonce.</summary>
    public void Begin(HttpResponse response, string provisioningUri)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        cache.Set(Key(nonce), provisioningUri, Ttl);
        SessionCookie.SetTransient(response, SessionCookie.TotpEnrollName, nonce, EnrollPath, Ttl);
    }

    /// <summary>Reads the staged provisioning URI without consuming it, or null when absent/expired.</summary>
    public string? Peek(string? nonce)
        => string.IsNullOrEmpty(nonce) ? null : cache.Get<string>(Key(nonce));

    /// <summary>Clears the staged entry and the nonce cookie once enrollment ends (success or restart).</summary>
    public void End(HttpRequest request, HttpResponse response)
    {
        var nonce = SessionCookie.ReadTransient(request, SessionCookie.TotpEnrollName);
        if (!string.IsNullOrEmpty(nonce))
        {
            cache.Remove(Key(nonce));
        }

        SessionCookie.ClearTransient(response, SessionCookie.TotpEnrollName, EnrollPath);
    }

    private static string Key(string nonce) => $"totp-enroll:{nonce}";
}
