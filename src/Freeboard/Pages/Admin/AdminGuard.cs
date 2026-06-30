using System.Security.Claims;
using Freeboard.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Freeboard.Pages.Admin;

/// <summary>
/// The in-page admin-role gate for the /admin pages. The /admin folder is authorized with the page
/// challenge scheme (so an unauthenticated request 302s to /login), but the admin-role check must NOT
/// run as a folder authorize policy: a Forbid under that scheme redirects to /account/sudo, which would
/// misrepresent an admin-role denial as a missing step-up. So each admin handler calls this at the top
/// and, when the caller is not an admin, returns a bare 403 (StatusCodeResult) that invokes no scheme.
/// </summary>
internal static class AdminGuard
{
    /// <summary>
    /// Null when the caller is an admin (proceed); otherwise a bare 403 the handler returns before
    /// reading or mutating any data.
    /// </summary>
    public static IActionResult? Check(ClaimsPrincipal user)
        => string.Equals(user.FindFirst(AuthClaims.Role)?.Value, GlobalRoles.Admin, StringComparison.Ordinal)
            ? null
            : new StatusCodeResult(StatusCodes.Status403Forbidden);
}
