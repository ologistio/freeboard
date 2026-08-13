using System.Security.Claims;
using Freeboard.Auth;
using Freeboard.Authz;
using Freeboard.Core.Authz;
using Freeboard.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Freeboard.Web.Tests;

/// <summary>
/// Exercises the web authorizer and the authz-backed <see cref="IAssetAccess"/> through the real DI
/// graph with the in-memory authz fakes, resolving the scoped seam directly rather than through a
/// route, plus the organisation-bounded ancestry chain every organisation gate is anchored on.
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

    private static AuthWebFactory Build(FakeAuthzStore authz, string? mode = null, IReadOnlyList<AssetNode>? assets = null)
        => new()
        {
            Authz = authz,
            AuthzMode = mode,
            Compliance = new FakeComplianceStore { Assets = assets ?? [] },
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
            assets: [TestAssets.Org("org-a", title: "A")]);
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
        using var factory = Build(new FakeAuthzStore(), assets: [TestAssets.Org("org-a", title: "A")]);
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
            assets: [TestAssets.Org("org-a", title: "A")]);
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
            assets: [TestAssets.Org("org-a", title: "A")]);
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
            assets: [TestAssets.Org("org-a", title: "A")]);
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
        using var factory = Build(new FakeAuthzStore(), assets: [TestAssets.Org("org-a", title: "A")]);
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
        using var factory = Build(new FakeAuthzStore(), mode: "Observe", assets: [TestAssets.Org("org-a", title: "A")]);
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
        using var factory = Build(new FakeAuthzStore(), mode: "Enforce", assets: [TestAssets.Org("org-a", title: "A")]);
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
        using var factory = Build(new FakeAuthzStore(), mode: "Observe", assets: [TestAssets.Org("org-a", title: "A")]);
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
        var orgs = new List<AssetNode>
        {
            TestAssets.Org("org-a", title: "A"),
            TestAssets.Org("org-b", title: "B"),
        };
        using var factory = Build(new FakeAuthzStore().GrantComplianceReader("u1", "org-a"), mode: "Observe", assets: orgs);
        using var scope = factory.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<IAssetAccess>();

        var accessible = await access.AccessibleAssetIdsAsync(Principal("u1"), orgs);
        Assert.Contains("org-a", accessible);
        Assert.Contains("org-b", accessible);
    }

    [Fact]
    public async Task EnforceNarrowsReadsToGrantedSubtree()
    {
        var orgs = new List<AssetNode>
        {
            TestAssets.Org("org-a", title: "A"),
            TestAssets.Org("child", "org-a", "Department", "C"),
            TestAssets.Org("org-b", title: "B"),
        };
        using var factory = Build(new FakeAuthzStore().GrantComplianceReader("u1", "org-a"), mode: "Enforce", assets: orgs);
        using var scope = factory.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<IAssetAccess>();

        var accessible = await access.AccessibleAssetIdsAsync(Principal("u1"), orgs);
        Assert.Contains("org-a", accessible);
        Assert.Contains("child", accessible); // subtree covered
        Assert.DoesNotContain("org-b", accessible);
    }

    // The rollout mode governs step ONE only - the organisation union. Step two, the closure over the
    // asset edges, is never relaxed, so the fail-closed cases stay out in every mode.
    private static IReadOnlyList<AssetNode> ClosureTree() =>
    [
        TestAssets.Org("org-a"),
        TestAssets.Machine("m-live", "org-a"),
        TestAssets.Machine("m-retired", "org-a", source: "discovered", state: "Retired"),
        TestAssets.Vendor("v-owned", "org-a"),
        TestAssets.Vendor("v-ownerless", null),
    ];

    [Theory]
    [InlineData("Observe")]
    [InlineData("Compat")]
    [InlineData("Enforce")]
    public async Task EveryModeExcludesAnOwnerlessVendorAndARetiredDiscoveredAsset(string mode)
    {
        var assets = ClosureTree();
        using var factory = Build(new FakeAuthzStore().GrantComplianceReader("u1", "org-a"), mode: mode, assets: assets);
        using var scope = factory.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<IAssetAccess>();

        var accessible = await access.AccessibleAssetIdsAsync(Principal("u1"), assets);

        Assert.Contains("m-live", accessible);
        Assert.Contains("v-owned", accessible);
        Assert.DoesNotContain("m-retired", accessible);
        Assert.DoesNotContain("v-ownerless", accessible);
    }

    [Fact]
    public async Task ObserveWidensTheUnionButNotTheClosure()
    {
        // Observe hands the closure EVERY organisation, including one the caller holds no grant on, so
        // whatever hangs off it becomes readable - while the edgeless and retired rows still do not.
        IReadOnlyList<AssetNode> assets = [.. ClosureTree(), TestAssets.Org("org-b"), TestAssets.Machine("m-b", "org-b")];
        using var factory = Build(new FakeAuthzStore().GrantComplianceReader("u1", "org-a"), mode: "Observe", assets: assets);
        using var scope = factory.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<IAssetAccess>();

        var accessible = await access.AccessibleAssetIdsAsync(Principal("u1"), assets);

        Assert.Contains("m-b", accessible);
        Assert.DoesNotContain("m-retired", accessible);
        Assert.DoesNotContain("v-ownerless", accessible);
    }

    [Fact]
    public async Task CompatGivesAZeroGrantCallerTheFullUnionButNotTheClosureExclusions()
    {
        var assets = ClosureTree();
        using var factory = Build(new FakeAuthzStore(), mode: "Compat", assets: assets);
        using var scope = factory.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<IAssetAccess>();

        var accessible = await access.AccessibleAssetIdsAsync(Principal("u1"), assets);

        Assert.Contains("org-a", accessible);
        Assert.Contains("m-live", accessible);
        Assert.DoesNotContain("m-retired", accessible);
        Assert.DoesNotContain("v-ownerless", accessible);
    }

    [Fact]
    public async Task EnforceGivesAZeroGrantCallerNothing()
    {
        var assets = ClosureTree();
        using var factory = Build(new FakeAuthzStore(), mode: "Enforce", assets: assets);
        using var scope = factory.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<IAssetAccess>();

        Assert.Empty(await access.AccessibleAssetIdsAsync(Principal("u1"), assets));
    }

    [Fact]
    public async Task AccessibleSetResolvesOncePerRequestPerAssetList()
    {
        // A page render asks the seam at least twice - the layout's organisation selector, then the page
        // itself - and resolving it is a full pass over the asset tree. Those share one list, so they
        // resolve once. The Compat zero-grant fallback audits each time it runs, so the row count is the
        // observable: one list, one row.
        var assets = ClosureTree();
        using var factory = Build(new FakeAuthzStore(), mode: "Compat", assets: assets);

        using (var scope = factory.Services.CreateScope())
        {
            var access = scope.ServiceProvider.GetRequiredService<IAssetAccess>();
            var first = await access.AccessibleAssetIdsAsync(Principal("u1"), assets);
            var second = await access.AccessibleAssetIdsAsync(Principal("u1"), assets);
            Assert.Same(first, second);
        }

        Assert.Single(factory.AuthzAdmin.Events, e => e.EventType == "authz.compat.read");

        // The memo is per request: a fresh scope resolves again rather than serving the last one.
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAssetAccess>()
                .AccessibleAssetIdsAsync(Principal("u1"), assets);
        }

        Assert.Equal(2, factory.AuthzAdmin.Events.Count(e => e.EventType == "authz.compat.read"));
    }

    [Fact]
    public async Task TwoAssetListsResolveTwoSetsAndNeitherIsServedTheOthers()
    {
        // The memo keys on the list, not the principal alone. Two reads in one request see different owner
        // edges when a sync commits between them, so a set resolved over one is not an answer about the
        // other - serving the first list's answer for the second is the defect this keying removes.
        var wide = ClosureTree();
        var narrow = (IReadOnlyList<AssetNode>)[.. wide.Where(a => a.IsOrganisation)];
        using var factory = Build(new FakeAuthzStore(), mode: "Compat", assets: wide);
        using var scope = factory.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<IAssetAccess>();

        var first = await access.AccessibleAssetIdsAsync(Principal("u1"), wide);
        var second = await access.AccessibleAssetIdsAsync(Principal("u1"), narrow);

        Assert.NotSame(first, second);
        Assert.NotEqual(first.Count, second.Count);
        Assert.Equal(2, factory.AuthzAdmin.Events.Count(e => e.EventType == "authz.compat.read"));
    }

    [Fact]
    public async Task EqualButDistinctAssetListsResolveTwiceAndAMutatedListKeepsItsMemoizedSet()
    {
        // Two properties the key rests on. Lists compare by reference, so equal contents in two instances
        // resolve twice rather than sharing an answer. And because the memo is taken at resolution time, a
        // list mutated afterwards is still served the set it resolved - which is the observable behind the
        // seam's requirement that a caller does not mutate a list it has handed over.
        var assets = ClosureTree();
        var mutable = new List<AssetNode>(assets);
        using var factory = Build(new FakeAuthzStore(), mode: "Compat", assets: assets);
        using var scope = factory.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<IAssetAccess>();

        await access.AccessibleAssetIdsAsync(Principal("u1"), assets);
        var fromCopy = await access.AccessibleAssetIdsAsync(Principal("u1"), mutable);
        Assert.Equal(2, factory.AuthzAdmin.Events.Count(e => e.EventType == "authz.compat.read"));

        mutable.Clear();
        var afterMutation = await access.AccessibleAssetIdsAsync(Principal("u1"), mutable);

        Assert.Same(fromCopy, afterMutation);
        Assert.Equal(2, factory.AuthzAdmin.Events.Count(e => e.EventType == "authz.compat.read"));
    }

    // The organisation-bounded ancestry chain an organisation gate anchors on. The cut keeps the first
    // non-organisation entry and then stops, rather than pinning the supplied id alone.
    private static IReadOnlyList<AssetNode> CutTree() =>
    [
        TestAssets.Org("company"),
        TestAssets.Org("dept", "company"),
        TestAssets.Machine("m-1", "dept"),
        TestAssets.Machine("m-retired", "dept", source: "discovered", state: "Retired"),
        TestAssets.Vendor("v-1", "company"),
        TestAssets.Org("org-dangling", "gone-org"),
        TestAssets.Org("org-c1", "org-c2"),
        TestAssets.Org("org-c2", "org-c1"),
        TestAssets.Org("org-on-machine", "m-1"),
    ];

    [Theory]
    [InlineData("no-such-asset", new[] { "no-such-asset" })]
    [InlineData("m-1", new[] { "m-1" })]
    [InlineData("m-retired", new[] { "m-retired" })]
    [InlineData("v-1", new[] { "v-1" })]
    [InlineData("org-dangling", new[] { "org-dangling", "gone-org" })]
    [InlineData("org-c1", new[] { "org-c1", "org-c2" })]
    [InlineData("org-on-machine", new[] { "org-on-machine", "m-1" })]
    [InlineData("dept", new[] { "dept", "company" })]
    public async Task OrganisationResourceAnchorsOnTheOrganisationBoundedChain(string id, string[] expected)
    {
        using var factory = Build(new FakeAuthzStore(), assets: CutTree());
        using var scope = factory.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<AuthzRequestCache>();

        var resource = await cache.OrganisationResourceAsync("organisation", id, id);

        Assert.Equal(expected, resource.OrgAncestryInclusive);
        Assert.Equal(id, resource.OrganisationId);
    }

    [Fact]
    public async Task ScopedCacheDoesNotLeakGrantsAcrossRequests()
    {
        using var factory = Build(new FakeAuthzStore().GrantOrg("u1", AuthzActions.OrgWrite, "org-a"),
            assets: [TestAssets.Org("org-a", title: "A")]);

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
