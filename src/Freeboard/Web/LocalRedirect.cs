using Microsoft.AspNetCore.Mvc;

namespace Freeboard.Web;

/// <summary>
/// Validates a <c>returnUrl</c> is a local, relative path before a flow resumes to it, so an
/// attacker cannot craft a login/sudo link that bounces the user to an off-site URL. Absolute and
/// protocol-relative URLs are rejected in favour of a safe default.
/// </summary>
public static class LocalRedirect
{
    /// <summary>
    /// Returns <paramref name="returnUrl"/> when it is a local relative path, otherwise
    /// <paramref name="fallback"/>. A page handler exposes <see cref="IUrlHelper.IsLocalUrl"/> via
    /// its <c>Url</c>; this overload takes that helper so the framework's own check is reused.
    /// </summary>
    public static string Sanitize(IUrlHelper url, string? returnUrl, string fallback = "/account")
        => !string.IsNullOrEmpty(returnUrl) && url.IsLocalUrl(returnUrl) ? returnUrl : fallback;

    /// <summary>
    /// Local-URL check without an <see cref="IUrlHelper"/> (used by the page challenge scheme, which
    /// runs in the authentication pipeline before any page/Url helper exists). Mirrors
    /// <see cref="IUrlHelper.IsLocalUrl"/>: a local URL starts with a single <c>/</c> (not <c>//</c>
    /// or <c>/\</c>) or a single <c>~/</c>, and is never absolute or protocol-relative.
    /// </summary>
    public static bool IsLocal(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        if (url[0] == '/')
        {
            // "//" and "/\" are protocol-relative; reject both.
            return url.Length == 1 || (url[1] != '/' && url[1] != '\\');
        }

        if (url[0] == '~' && url.Length > 1 && url[1] == '/')
        {
            return url.Length == 2 || (url[2] != '/' && url[2] != '\\');
        }

        return false;
    }

    /// <summary>Returns <paramref name="returnUrl"/> when local, otherwise <paramref name="fallback"/>.</summary>
    public static string Sanitize(string? returnUrl, string fallback = "/account")
        => IsLocal(returnUrl) ? returnUrl! : fallback;
}
