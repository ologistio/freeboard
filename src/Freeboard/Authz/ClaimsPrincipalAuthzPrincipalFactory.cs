using System.Globalization;
using System.Security.Claims;
using Freeboard.Core.Authz;
using Freeboard.Auth;
using Freeboard.Persistence.Auth;

namespace Freeboard.Authz;

/// <summary>
/// Builds the immutable <see cref="AuthzPrincipal"/> from the bearer <see cref="ClaimsPrincipal"/>
/// and its loaded facts. The id comes from <c>freeboard:user_id</c>; the force-reset-limited flag
/// (<c>freeboard:auth_state</c>) is a hard-deny attribute. No new identity concept is introduced.
/// </summary>
public static class ClaimsPrincipalAuthzPrincipalFactory
{
    public static AuthzPrincipal Build(ClaimsPrincipal user, AuthzPrincipalFacts facts)
    {
        var userId = user.FindFirst(AuthClaims.UserId)?.Value;
        var authenticated = userId is not null && (user.Identity?.IsAuthenticated ?? false);
        return new AuthzPrincipal(
            userId,
            authenticated,
            IsLimitedSession(user),
            IsSteppedUp: false,
            facts.SystemPermissions,
            facts.OrgGrants);
    }

    private static bool IsLimitedSession(ClaimsPrincipal user)
    {
        var raw = user.FindFirst(AuthClaims.AuthState)?.Value;
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var state)
            && state == (int)SessionAuthState.ForceResetLimited;
    }
}
