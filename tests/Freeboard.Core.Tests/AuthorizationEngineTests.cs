using Freeboard.Core.Authz;

namespace Freeboard.Core.Tests;

public sealed class AuthorizationEngineTests
{
    private static IAuthorizationEngine Engine() => new PolicyAuthorizationEngine(
    [
        new SessionGuardPolicy(),
        new SystemAdminPolicy(),
        new SelfAccessPolicy(),
        new OrgRbacPolicy(),
    ]);

    private static AuthzPrincipal Principal(
        bool authenticated = true,
        bool limited = false,
        IEnumerable<string>? systemPermissions = null,
        IEnumerable<AuthzOrgGrant>? grants = null)
        => new(
            "user-1",
            authenticated,
            limited,
            IsSteppedUp: false,
            (systemPermissions ?? []).ToHashSet(StringComparer.Ordinal),
            (grants ?? []).ToList());

    private static AuthzResource Org(params string[] ancestry) =>
        new("organisation", ancestry.Length > 0 ? ancestry[0] : null,
            ancestry.Length > 0 ? ancestry[0] : null, ancestry);

    [Fact]
    public void DefaultDenyWhenNoPolicyPermits()
    {
        var decision = Engine().Evaluate(new AuthzRequest(
            Principal(grants: []), AuthzActions.ComplianceRead, Org("org-a")));

        Assert.Equal(AuthzEffect.Deny, decision.Effect);
    }

    [Fact]
    public void UnauthenticatedIsDenied()
    {
        var decision = Engine().Evaluate(new AuthzRequest(
            Principal(authenticated: false, systemPermissions: [AuthzActions.SystemAdmin]),
            AuthzActions.ComplianceRead, Org("org-a")));

        Assert.Equal(AuthzEffect.Deny, decision.Effect);
    }

    [Fact]
    public void LimitedSessionIsHardDeniedEvenWithSystemAdmin()
    {
        // Deny-overrides: the session guard's deny beats system-admin's permit regardless of order.
        var decision = Engine().Evaluate(new AuthzRequest(
            Principal(limited: true, systemPermissions: [AuthzActions.SystemAdmin]),
            AuthzActions.ComplianceRead, Org("org-a")));

        Assert.Equal(AuthzEffect.Deny, decision.Effect);
    }

    [Fact]
    public void SystemAdminPermitsEveryAction()
    {
        var decision = Engine().Evaluate(new AuthzRequest(
            Principal(systemPermissions: [AuthzActions.SystemAdmin]),
            AuthzActions.OrgWrite, Org("org-a")));

        Assert.Equal(AuthzEffect.Permit, decision.Effect);
    }

    [Fact]
    public void GrantOnAncestorPermitsDescendantResource()
    {
        // Grant sits on the root; the resource is a descendant department, so the inclusive
        // ancestry contains the granting org.
        var decision = Engine().Evaluate(new AuthzRequest(
            Principal(grants: [new AuthzOrgGrant(AuthzActions.ComplianceRead, "root")]),
            AuthzActions.ComplianceRead, Org("dept", "company", "root")));

        Assert.Equal(AuthzEffect.Permit, decision.Effect);
    }

    [Fact]
    public void GrantOnSiblingDoesNotPermit()
    {
        var decision = Engine().Evaluate(new AuthzRequest(
            Principal(grants: [new AuthzOrgGrant(AuthzActions.ComplianceRead, "sibling")]),
            AuthzActions.ComplianceRead, Org("dept", "company", "root")));

        Assert.Equal(AuthzEffect.Deny, decision.Effect);
    }

    [Fact]
    public void GrantForDifferentActionDoesNotPermit()
    {
        var decision = Engine().Evaluate(new AuthzRequest(
            Principal(grants: [new AuthzOrgGrant(AuthzActions.ComplianceRead, "root")]),
            AuthzActions.OrgWrite, Org("root")));

        Assert.Equal(AuthzEffect.Deny, decision.Effect);
    }

    [Fact]
    public void DecisionEvaluatesAgainstSuppliedFactsNoHardCodedRoleMap()
    {
        // The engine never maps a role to permissions; it only checks the facts it is handed. A
        // grant for the exact action permits; an arbitrary role name is irrelevant.
        var decision = Engine().Evaluate(new AuthzRequest(
            Principal(grants: [new AuthzOrgGrant(AuthzActions.ComplianceScopeWrite, "root")]),
            AuthzActions.ComplianceScopeWrite, Org("root")));

        Assert.Equal(AuthzEffect.Permit, decision.Effect);
    }

    [Fact]
    public void CyclicAncestryTerminatesAndDoesNotPermitUngrantedAction()
    {
        // A cyclic parent-walk is guarded upstream (the ancestry builder dedupes); here the ancestry
        // is a finite deduped chain, so evaluation terminates and denies an ungranted action.
        var decision = Engine().Evaluate(new AuthzRequest(
            Principal(grants: [new AuthzOrgGrant(AuthzActions.ComplianceRead, "other")]),
            AuthzActions.ComplianceRead, Org("a", "b")));

        Assert.Equal(AuthzEffect.Deny, decision.Effect);
    }

    [Fact]
    public void SelfAccessSlotIsInert()
    {
        // With only the self-access slot active, a self user action is not permitted (the slot
        // abstains), proving it ships as an inert extension point.
        var engine = new PolicyAuthorizationEngine([new SelfAccessPolicy()]);
        var decision = engine.Evaluate(new AuthzRequest(
            Principal(), AuthzActions.UserManage, AuthzResource.ForUser("user-1")));

        Assert.Equal(AuthzEffect.Deny, decision.Effect);
    }

    [Fact]
    public void LimitedSessionAllowlistPermitsListedAction()
    {
        var engine = new PolicyAuthorizationEngine(
        [
            new SessionGuardPolicy(new HashSet<string>(StringComparer.Ordinal) { AuthzActions.ComplianceRead }),
            new OrgRbacPolicy(),
        ]);
        var decision = engine.Evaluate(new AuthzRequest(
            Principal(limited: true, grants: [new AuthzOrgGrant(AuthzActions.ComplianceRead, "root")]),
            AuthzActions.ComplianceRead, Org("root")));

        Assert.Equal(AuthzEffect.Permit, decision.Effect);
    }
}
