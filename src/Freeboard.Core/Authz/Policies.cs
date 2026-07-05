namespace Freeboard.Core.Authz;

/// <summary>
/// Hard-deny input evaluated first: an unauthenticated principal, or one whose session is
/// force-reset-limited, is denied for any action outside the limited-session allowlist. Because a
/// deny wins the deny-overrides combine, a limited or anonymous caller can never be permitted by a
/// later policy. The allowlist is a seam; v1 ships it empty (no authz action is part of the
/// force-reset funnel).
/// </summary>
public sealed class SessionGuardPolicy(IReadOnlySet<string>? limitedSessionAllowlist = null) : IAuthzPolicy
{
    private readonly IReadOnlySet<string> _allowlist =
        limitedSessionAllowlist ?? new HashSet<string>(StringComparer.Ordinal);

    public string Name => "session-guard";

    public AuthzPolicyOutcome Evaluate(AuthzRequest request)
    {
        if (!request.Principal.IsAuthenticated)
        {
            return AuthzPolicyOutcome.Deny;
        }

        if (request.Principal.IsLimitedSession && !_allowlist.Contains(request.Action))
        {
            return AuthzPolicyOutcome.Deny;
        }

        return AuthzPolicyOutcome.NotApplicable;
    }
}

/// <summary>
/// Attribute policy: a principal holding <see cref="AuthzActions.SystemAdmin"/> is permitted every
/// action. The break-glass super-admin; it preserves every legacy admin gate.
/// </summary>
public sealed class SystemAdminPolicy : IAuthzPolicy
{
    public string Name => "system-admin";

    public AuthzPolicyOutcome Evaluate(AuthzRequest request)
        => request.Principal.SystemPermissions.Contains(AuthzActions.SystemAdmin)
            ? AuthzPolicyOutcome.Permit
            : AuthzPolicyOutcome.NotApplicable;
}

/// <summary>
/// The ordered slot for self-service ABAC rules (a principal acting on its own <c>user</c>
/// resource). v1 ships it inert - it always abstains - because current self-service endpoints gate
/// on session state, not authz; a rule can be added later with no seam change.
/// </summary>
public sealed class SelfAccessPolicy : IAuthzPolicy
{
    public string Name => "self-access";

    public AuthzPolicyOutcome Evaluate(AuthzRequest request) => AuthzPolicyOutcome.NotApplicable;
}

/// <summary>
/// Relationship policy: permit when the principal holds a grant whose organisation is in the
/// resource's inclusive ancestry and whose effective permission is the requested action. A grant on
/// an ancestor covers descendant resources (the subtree rule).
/// </summary>
public sealed class OrgRbacPolicy : IAuthzPolicy
{
    public string Name => "org-rbac";

    public AuthzPolicyOutcome Evaluate(AuthzRequest request)
    {
        var ancestry = request.Resource.OrgAncestryInclusive;
        if (ancestry.Count == 0 || request.Principal.OrgGrants.Count == 0)
        {
            return AuthzPolicyOutcome.NotApplicable;
        }

        var ancestrySet = new HashSet<string>(ancestry, StringComparer.Ordinal);
        var permitted = request.Principal.OrgGrants.Any(grant =>
            string.Equals(grant.PermissionKey, request.Action, StringComparison.Ordinal)
            && ancestrySet.Contains(grant.OrganisationId));

        return permitted ? AuthzPolicyOutcome.Permit : AuthzPolicyOutcome.NotApplicable;
    }
}
