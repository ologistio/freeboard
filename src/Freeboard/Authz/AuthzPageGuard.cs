using System.Security.Claims;
using Freeboard.Core.Authz;
using Microsoft.AspNetCore.Mvc;

namespace Freeboard.Authz;

/// <summary>
/// The in-handler authorization gate for Razor Page handlers, replacing the legacy claim-reading
/// <c>AdminGuard</c>. Pipeline policies do not run for in-process page handlers, so a sensitive
/// handler calls this at the top. It ALWAYS blocks a deny (mode-independent), returning a bare 403 (or
/// 404 for an invisible resource) that invokes no scheme, so a denial is not misrendered as a
/// missing step-up redirect. Scoped: it uses the scoped <see cref="IAuthorizer"/>.
/// </summary>
public sealed class AuthzPageGuard(IAuthorizer authorizer)
{
    /// <summary>Null when permitted (proceed); otherwise the bare result the handler returns first.</summary>
    public async Task<IActionResult?> CheckAsync(
        ClaimsPrincipal user, string action, AuthzResource resource, CancellationToken cancellationToken = default)
    {
        var decision = await authorizer
            .AuthorizeAsync(user, action, resource, alwaysEnforce: true, cancellationToken).ConfigureAwait(false);
        return decision.IsPermitted ? null : new StatusCodeResult(StatusCodes.Status403Forbidden);
    }
}
