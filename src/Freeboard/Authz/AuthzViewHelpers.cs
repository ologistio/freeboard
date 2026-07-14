using System.Security.Claims;
using Freeboard.Auth;
using Freeboard.Core.Authz;

namespace Freeboard.Authz;

/// <summary>
/// View-only affordance helpers. The admin nav link follows authorization, not the legacy
/// <c>freeboard:role=admin</c> claim (which no longer grants admin access): it shows when the
/// principal can reach the admin surface (holds <c>user.manage</c> or <c>system.admin</c>). This is a
/// cosmetic control - the admin pages force-enforce regardless - so it fails SAFE (hidden) on any
/// error, and it reads the loaded facts rather than the authorizer so a non-admin render is not
/// audited on every page view.
/// </summary>
public static class AuthzViewHelpers
{
    public static async ValueTask<bool> CanReachAdminAsync(
        IAuthzFactProvider facts, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var userId = user.FindFirst(AuthClaims.UserId)?.Value;
        if (userId is null)
        {
            return false;
        }

        try
        {
            var loaded = await facts.LoadFactsAsync(userId, cancellationToken).ConfigureAwait(false);
            return loaded.SystemPermissions.Contains(AuthzActions.SystemAdmin)
                || loaded.SystemPermissions.Contains(AuthzActions.UserManage)
                || loaded.OrgGrants.Any(g => string.Equals(g.PermissionKey, AuthzActions.UserManage, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the principal holds <c>system.admin</c>, the permission the custom-role surface
    /// force-enforces. Gates the nav link on the same check the page and API use, so a link never leads
    /// to a 403/404. Cosmetic and fail-safe (hidden on any error), reading the request-cached facts.
    /// </summary>
    public static async ValueTask<bool> CanAdministerSystemAsync(
        IAuthzFactProvider facts, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var userId = user.FindFirst(AuthClaims.UserId)?.Value;
        if (userId is null)
        {
            return false;
        }

        try
        {
            var loaded = await facts.LoadFactsAsync(userId, cancellationToken).ConfigureAwait(false);
            return loaded.SystemPermissions.Contains(AuthzActions.SystemAdmin);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the principal can write role assignments somewhere: holds <c>authz.assignment.write</c>
    /// in any organisation, or the system-wide <c>authz.assignment.write</c>, or break-glass
    /// <c>system.admin</c>. The role-assignments page force-enforces the write permission per organisation,
    /// so this shows the rail link only to a user who can use it in at least one org. Cosmetic and
    /// fail-safe (hidden on any error), reading the request-cached facts.
    /// </summary>
    public static async ValueTask<bool> CanReachRoleAssignmentsAsync(
        IAuthzFactProvider facts, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var userId = user.FindFirst(AuthClaims.UserId)?.Value;
        if (userId is null)
        {
            return false;
        }

        try
        {
            var loaded = await facts.LoadFactsAsync(userId, cancellationToken).ConfigureAwait(false);
            return loaded.SystemPermissions.Contains(AuthzActions.SystemAdmin)
                || loaded.SystemPermissions.Contains(AuthzActions.AuthzAssignmentWrite)
                || loaded.OrgGrants.Any(g => string.Equals(g.PermissionKey, AuthzActions.AuthzAssignmentWrite, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }
}
