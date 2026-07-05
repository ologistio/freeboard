using System.Security.Claims;
using Freeboard.Auth;
using Freeboard.Authz;
using Freeboard.Core.Authz;
using Freeboard.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Freeboard.Web.Tests;

/// <summary>
/// Exercises the web authorizer and authz-backed IOrgAccess through the real DI graph with the
/// in-memory authz fakes, resolving the scoped seam directly (no route needed - routes are gated in
/// later task groups).
/// </summary>
public sealed class AuthorizerTests
{
    private static ClaimsPrincipal Principal(string userId, bool limited = false)
    {
        var claims = new List<Claim> { new(AuthClaims.UserId, userId) };
        if (limited)
        {
            claims.Add(new Claim(AuthClaims.AuthState, "1"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static AuthzResource Org(string id) => new("organisation", id, id, []);

    private static AuthWebFactory Build(FakeAuthzStore authz, string? mode = null, IReadOnlyList<OrganisationRow>? orgs = null)
        => new()
        {
            Authz = authz,
            AuthzMode = mode,
            Compliance = new FakeComplianceStore { Organisations = orgs ?? [] },
        };

    private static (IServiceScope Scope, IAuthorizer Authorizer) Resolve(AuthWebFactory factory)
    {
        var scope = factory.Services.CreateScope();
        return (scope, scope.ServiceProvider.GetRequiredService<IAuthorizer>());
    }

    [Fact]
    public async Task PermittedCallerPasses()
    {
        using var factory = Build(new FakeAuthzStore().GrantOrg("u1", AuthzActions.OrgWrite, "org-a"),
            orgs: [new OrganisationRow("org-a", "A", "Company", null)]);
        var (scope, authorizer) = Resolve(factory);
        using (scope)
        {
            var decision = await authorizer.AuthorizeAsync(Principal("u1"), AuthzActions.OrgWrite, Org("org-a"), true);
            Assert.True(decision.IsPermitted);
        }
    }

    [Fact]
    public async Task UnpermittedIsDenied()
    {
        using var factory = Build(new FakeAuthzStore(), orgs: [new OrganisationRow("org-a", "A", "Company", null)]);
        var (scope, authorizer) = Resolve(factory);
        using (scope)
        {
            var decision = await authorizer.AuthorizeAsync(Principal("u1"), AuthzActions.OrgWrite, Org("org-a"), true);
            Assert.False(decision.IsPermitted);
        }
    }

    [Fact]
    public async Task SuperAdminBypassesEverything()
    {
        using var factory = Build(new FakeAuthzStore().GrantSuperAdmin("u1"),
            orgs: [new OrganisationRow("org-a", "A", "Company", null)]);
        var (scope, authorizer) = Resolve(factory);
        using (scope)
        {
            var decision = await authorizer.AuthorizeAsync(Principal("u1"), AuthzActions.UserManage, AuthzResource.ForUser("other"), true);
            Assert.True(decision.IsPermitted);
        }
    }

    [Fact]
    public async Task FailsClosedOnStoreOutage()
    {
        using var factory = Build(new FakeAuthzStore { Unreachable = true },
            orgs: [new OrganisationRow("org-a", "A", "Company", null)]);
        var (scope, authorizer) = Resolve(factory);
        using (scope)
        {
            var decision = await authorizer.AuthorizeAsync(Principal("u1"), AuthzActions.OrgWrite, Org("org-a"), true);
            Assert.False(decision.IsPermitted);
        }
    }

    [Fact]
    public async Task LimitedSessionIsDeniedEvenAsSuperAdmin()
    {
        using var factory = Build(new FakeAuthzStore().GrantSuperAdmin("u1"),
            orgs: [new OrganisationRow("org-a", "A", "Company", null)]);
        var (scope, authorizer) = Resolve(factory);
        using (scope)
        {
            var decision = await authorizer.AuthorizeAsync(Principal("u1", limited: true), AuthzActions.OrgWrite, Org("org-a"), true);
            Assert.False(decision.IsPermitted);
        }
    }

    [Fact]
    public async Task DeniedDecisionWritesAuditRow()
    {
        using var factory = Build(new FakeAuthzStore(), orgs: [new OrganisationRow("org-a", "A", "Company", null)]);
        var (scope, authorizer) = Resolve(factory);
        using (scope)
        {
            await authorizer.AuthorizeAsync(Principal("u1"), AuthzActions.OrgWrite, Org("org-a"), true);
        }

        Assert.Contains(factory.AuthzAdmin.Events, e => e.EventType == "authz.decision.denied" && e.ActorUserId == "u1");
    }

    [Fact]
    public async Task ObserveDoesNotBlockAReadDeny()
    {
        using var factory = Build(new FakeAuthzStore(), mode: "Observe", orgs: [new OrganisationRow("org-a", "A", "Company", null)]);
        var (scope, authorizer) = Resolve(factory);
        using (scope)
        {
            var decision = await authorizer.AuthorizeAsync(Principal("u1"), AuthzActions.ComplianceRead, Org("org-a"), false);
            Assert.True(decision.IsPermitted);
        }
    }

    [Fact]
    public async Task EnforceBlocksAReadDeny()
    {
        using var factory = Build(new FakeAuthzStore(), mode: "Enforce", orgs: [new OrganisationRow("org-a", "A", "Company", null)]);
        var (scope, authorizer) = Resolve(factory);
        using (scope)
        {
            var decision = await authorizer.AuthorizeAsync(Principal("u1"), AuthzActions.ComplianceRead, Org("org-a"), false);
            Assert.False(decision.IsPermitted);
        }
    }

    [Fact]
    public async Task ObserveStillBlocksAlwaysEnforceWrite()
    {
        using var factory = Build(new FakeAuthzStore(), mode: "Observe", orgs: [new OrganisationRow("org-a", "A", "Company", null)]);
        var (scope, authorizer) = Resolve(factory);
        using (scope)
        {
            var decision = await authorizer.AuthorizeAsync(Principal("u1"), AuthzActions.OrgWrite, Org("org-a"), true);
            Assert.False(decision.IsPermitted);
        }
    }

    [Fact]
    public async Task ReadsAreNotNarrowedUnderObserve()
    {
        // A caller with only a partial-subtree grant gets the FULL accessible set under Observe.
        var orgs = new List<OrganisationRow>
        {
            new("org-a", "A", "Company", null),
            new("org-b", "B", "Company", null),
        };
        using var factory = Build(new FakeAuthzStore().GrantComplianceReader("u1", "org-a"), mode: "Observe", orgs: orgs);
        using var scope = factory.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<IOrgAccess>();

        var accessible = await access.AccessibleOrgIdsAsync(Principal("u1"), orgs);
        Assert.Contains("org-a", accessible);
        Assert.Contains("org-b", accessible);
    }

    [Fact]
    public async Task EnforceNarrowsReadsToGrantedSubtree()
    {
        var orgs = new List<OrganisationRow>
        {
            new("org-a", "A", "Company", null),
            new("child", "C", "Department", "org-a"),
            new("org-b", "B", "Company", null),
        };
        using var factory = Build(new FakeAuthzStore().GrantComplianceReader("u1", "org-a"), mode: "Enforce", orgs: orgs);
        using var scope = factory.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<IOrgAccess>();

        var accessible = await access.AccessibleOrgIdsAsync(Principal("u1"), orgs);
        Assert.Contains("org-a", accessible);
        Assert.Contains("child", accessible); // subtree covered
        Assert.DoesNotContain("org-b", accessible);
    }

    [Fact]
    public async Task ScopedCacheDoesNotLeakGrantsAcrossRequests()
    {
        using var factory = Build(new FakeAuthzStore().GrantOrg("u1", AuthzActions.OrgWrite, "org-a"),
            orgs: [new OrganisationRow("org-a", "A", "Company", null)]);

        using (var scope1 = factory.Services.CreateScope())
        {
            var a = scope1.ServiceProvider.GetRequiredService<IAuthorizer>();
            Assert.True((await a.AuthorizeAsync(Principal("u1"), AuthzActions.OrgWrite, Org("org-a"), true)).IsPermitted);
        }

        using (var scope2 = factory.Services.CreateScope())
        {
            // A different caller in a fresh request scope must not inherit u1's grants.
            var a = scope2.ServiceProvider.GetRequiredService<IAuthorizer>();
            Assert.False((await a.AuthorizeAsync(Principal("u2"), AuthzActions.OrgWrite, Org("org-a"), true)).IsPermitted);
        }
    }
}
