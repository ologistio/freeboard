using Microsoft.AspNetCore.Http;

namespace Freeboard.Web;

/// <summary>
/// The browser cookies that carry the page funnel's session and short-lived landing state.
/// The session cookie transports the same opaque bearer token the API issues; only its HMAC is
/// stored server-side, and <c>BearerAuthenticationHandler</c> still fully validates every request.
///
/// Two cookie shapes:
/// - The session cookie uses the <c>__Host-</c> prefix, which the browser only accepts when the
///   cookie is Secure, Path=/, and has no Domain - so a misconfigured deployment cannot weaken
///   those attributes. It is SameSite=Strict because it never needs to ride a cross-site navigation.
/// - The transient landing/nonce cookies are SameSite=Lax (not Strict) and NOT <c>__Host-</c>
///   prefixed: an emailed link is a cross-site top-level GET, and a Strict cookie is not sent on
///   that navigation or its scrub-redirect chain, so the cookie must be Lax to ride it. They keep
///   Secure + HttpOnly + a scoped path + a short TTL.
/// </summary>
public static class SessionCookie
{
    /// <summary>The session cookie name. The <c>__Host-</c> prefix binds Secure + Path=/ + no Domain.</summary>
    public const string Name = "__Host-freeboard-session";

    /// <summary>The login-MFA nonce cookie name (holds only the nonce, never the mfa_token).</summary>
    public const string MfaNonceName = "freeboard-mfa";

    /// <summary>The scrubbed password-reset token cookie name (path-scoped to /reset-password).</summary>
    public const string ResetTokenName = "freeboard-reset";

    /// <summary>The scrubbed login-MFA magic-link token cookie name (path-scoped to /auth/magic-link).</summary>
    public const string MagicLinkTokenName = "freeboard-magic-link";

    /// <summary>The one-time recovery-codes display nonce cookie name (path-scoped to the display page).</summary>
    public const string RecoveryDisplayName = "freeboard-recovery";

    /// <summary>The TOTP-enrollment display nonce cookie name (path-scoped to the TOTP enroll page).</summary>
    public const string TotpEnrollName = "freeboard-totp-enroll";

    /// <summary>The one-time temp-password display nonce cookie name (path-scoped to the display page).</summary>
    public const string AdminTempPasswordName = "freeboard-admin-temp-password";

    /// <summary>Sets the session cookie to <paramref name="token"/>, expiring with the session.</summary>
    public static void Set(HttpResponse response, string token, DateTimeOffset expiresAt)
        => response.Cookies.Append(Name, token, SessionOptions(expiresAt));

    /// <summary>Clears the session cookie. The delete attributes must match the set attributes.</summary>
    public static void Clear(HttpResponse response)
        => response.Cookies.Delete(Name, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
        });

    /// <summary>Reads the session cookie token, or null when absent.</summary>
    public static string? Read(HttpRequest request)
        => request.Cookies.TryGetValue(Name, out var token) && !string.IsNullOrEmpty(token) ? token : null;

    /// <summary>Sets a short-lived, path-scoped, Lax transient cookie (scrub token or login-MFA nonce).</summary>
    public static void SetTransient(HttpResponse response, string name, string value, string path, TimeSpan ttl)
        => response.Cookies.Append(name, value, TransientOptions(path, DateTimeOffset.UtcNow.Add(ttl)));

    /// <summary>Clears a transient cookie. The delete attributes must match the set attributes.</summary>
    public static void ClearTransient(HttpResponse response, string name, string path)
        => response.Cookies.Delete(name, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = path,
        });

    /// <summary>Reads a transient cookie value, or null when absent.</summary>
    public static string? ReadTransient(HttpRequest request, string name)
        => request.Cookies.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    private static CookieOptions SessionOptions(DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        // No Domain: required by the __Host- prefix and keeps the cookie host-only.
        Expires = expiresAt,
    };

    private static CookieOptions TransientOptions(string path, DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = path,
        Expires = expiresAt,
    };
}
