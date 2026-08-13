using System.Security.Claims;
using Freeboard.Auth;
using Freeboard.Authz;
using Freeboard.Compliance;
using Freeboard.Core.Authz;
using Freeboard.Core.Enterprise;
using Freeboard.Navigation;
using Freeboard.Persistence;
using Microsoft.Extensions.Options;

namespace Freeboard.Web.Tests;

/// <summary>
/// The app-shell nav catalog and its request-scoped resolver: the catalog is well-formed (N2 - each
/// destination under exactly one group, no duplicate key or route), and the resolver gates the EE/admin
/// items (including Role Assignments on assignment.write-in-any-org), resolves exactly one active item
/// (explicit key first, else longest route match), and badges the Vendors item with the owner-narrowed
/// count of vendors holding a lapsing certification - once per request, never on a store failure, and
/// never for a vendor holding nothing at all (N6).
/// </summary>
public sealed class ShellNavCatalogTests
{
    private static readonly string[] MovedSettingsRoutes =
    [
        "/settings/collectors",
        "/settings/users", "/settings/custom-roles",
    ];

    [Fact]
    public void EachDestinationSitsUnderExactlyOneGroupWithNoDuplicateKeyOrRoute()
    {
        var items = ShellNavCatalog.Items;

        Assert.Equal(items.Count, items.Select(i => i.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(items.Count, items.Select(i => i.Route).Distinct(StringComparer.Ordinal).Count());

        // Every item belongs to the group it is listed under (the group label matches, or null = top set).
        foreach (var group in ShellNavCatalog.Groups)
        {
            Assert.All(group.Items, item => Assert.Equal(group.Label, item.Group));
        }
    }

    [Fact]
    public void MovedDestinationsUseTheirNewSettingsRoutes()
    {
        var routes = ShellNavCatalog.Items.Select(i => i.Route).ToHashSet(StringComparer.Ordinal);
        foreach (var route in MovedSettingsRoutes)
        {
            Assert.Contains(route, routes);
        }
    }

    [Fact]
    public void RoleAssignmentsIsAPlatformRailItem()
    {
        var item = Assert.Single(
            ShellNavCatalog.Items, i => string.Equals(i.Key, "role-assignments", StringComparison.Ordinal));
        Assert.Equal("/settings/role-assignments", item.Route);
        Assert.Equal("Platform", item.Group);
        Assert.Equal(ShellNavAccess.CanReachRoleAssignments, item.Access);
    }

    [Fact]
    public async Task ResolverGatesTheEnterpriseItemByEntitlementAndEmitsNothingWhenGated()
    {
        // Entitlement off: the custom-roles item is dropped entirely (no view survives), so its label
        // and href never render. The admin (super-admin) fact is present, isolating the entitlement gate.
        var resolver = Resolver(SuperAdminFacts(), Entitlements(customPolicies: false));

        var nav = await resolver.ResolveAsync(User("admin"), "/home", activeKey: null);

        Assert.DoesNotContain(AllItems(nav), i => string.Equals(i.Key, "custom-roles", StringComparison.Ordinal));

        // Entitlement on plus super-admin: it appears.
        var entitled = Resolver(SuperAdminFacts(), Entitlements(customPolicies: true));
        var navOn = await entitled.ResolveAsync(User("admin"), "/home", activeKey: null);
        Assert.Contains(AllItems(navOn), i => string.Equals(i.Key, "custom-roles", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolverDropsAdminItemsForANonAdmin()
    {
        var resolver = Resolver(NoPermissionsFacts(), Entitlements(customPolicies: true));

        var nav = await resolver.ResolveAsync(User("member"), "/home", activeKey: null);
        var keys = AllItems(nav).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("users", keys);
        Assert.DoesNotContain("custom-roles", keys);
        Assert.DoesNotContain("role-assignments", keys);
        // The authenticated items still show.
        Assert.Contains("home", keys);
        Assert.Contains("soa", keys);
    }

    [Fact]
    public async Task ResolverShowsRoleAssignmentsToAUserWithAssignmentWriteOnSomeOrg()
    {
        // An org-scoped authz.assignment.write grant (no system permission) surfaces the item, since the
        // page's per-org write permission is satisfiable in at least one org.
        var resolver = Resolver(AssignmentWriteOnOrgFacts(), Entitlements(customPolicies: true));

        var nav = await resolver.ResolveAsync(User("member"), "/home", activeKey: null);
        var keys = AllItems(nav).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("role-assignments", keys);
        // The org grant does not unlock the system-admin-gated custom-roles item.
        Assert.DoesNotContain("custom-roles", keys);
    }

    [Fact]
    public async Task ResolverShowsRoleAssignmentsToASystemAdmin()
    {
        var resolver = Resolver(SuperAdminFacts(), Entitlements(customPolicies: true));

        var nav = await resolver.ResolveAsync(User("admin"), "/home", activeKey: null);
        var keys = AllItems(nav).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("role-assignments", keys);
    }

    [Fact]
    public async Task ResolverMarksExactlyOneActiveItemByLongestRouteMatch()
    {
        var resolver = Resolver(SuperAdminFacts(), Entitlements(customPolicies: true));

        // A nested route under custom-roles lights the custom-roles item, and only it.
        var nav = await resolver.ResolveAsync(User("admin"), "/settings/custom-roles/designer/auditor", activeKey: null);
        var active = AllItems(nav).Where(i => i.IsActive).ToList();

        Assert.Single(active);
        Assert.Equal("custom-roles", active[0].Key);
    }

    [Fact]
    public async Task ResolverPrefersTheExplicitKeyOverTheRouteMatch()
    {
        var resolver = Resolver(SuperAdminFacts(), Entitlements(customPolicies: true));

        // The path matches "users", but the page declares "custom-roles" - the explicit key wins.
        var nav = await resolver.ResolveAsync(User("admin"), "/settings/users", activeKey: "custom-roles");
        var active = AllItems(nav).Where(i => i.IsActive).ToList();

        Assert.Single(active);
        Assert.Equal("custom-roles", active[0].Key);
    }

    [Fact]
    public async Task ResolverBadgesTheOwnerNarrowedLapsingVendorCount()
    {
        // Two vendors lapse; only one is in the caller's accessible set, so the badge must read 1.
        var store = new FakeComplianceStore
        {
            Assets = [TestAssets.Vendor("vendor-a", "org-a"), TestAssets.Vendor("vendor-b", "org-b")],
            Assurances = [Assurance("vendor-a", Today.AddDays(10)), Assurance("vendor-b", Today.AddDays(10))],
        };
        var resolver = Resolver(
            SuperAdminFacts(), Entitlements(customPolicies: true), store, Access("vendor-a"));

        var nav = await resolver.ResolveAsync(User("admin"), "/home", activeKey: null);

        Assert.Equal(1, VendorsItem(nav).Count);
        Assert.All(AllItems(nav).Where(i => i.Key != "vendors"), i => Assert.Null(i.Count));
    }

    [Fact]
    public async Task ResolverBadgesAVendorWhoseOnlyAssuranceIsExpired()
    {
        // An expiring-only fixture cannot satisfy this: a lapsed certificate is at least as actionable as
        // one about to lapse, so it must raise the badge too.
        var store = new FakeComplianceStore
        {
            Assets = [TestAssets.Vendor("vendor-a", "org-a")],
            Assurances = [Assurance("vendor-a", Today.AddDays(-3))],
        };
        var resolver = Resolver(SuperAdminFacts(), Entitlements(customPolicies: true), store, Access("vendor-a"));

        var nav = await resolver.ResolveAsync(User("admin"), "/home", activeKey: null);

        Assert.Equal(1, VendorsItem(nav).Count);
    }

    [Fact]
    public async Task ResolverCountsOnlyVendorRowsEvenWhenAnotherAssetIsAccessible()
    {
        // The badge narrows on the asset being a Vendor as well as on the caller reaching it, matching
        // the register. Without the type test an accessible organisation carrying an assurance row would
        // raise a badge the page does not raise.
        var store = new FakeComplianceStore
        {
            Assets = [TestAssets.Org("org-a"), TestAssets.Vendor("vendor-a", "org-a")],
            Assurances = [Assurance("org-a", Today.AddDays(10))],
        };
        var resolver = Resolver(
            SuperAdminFacts(), Entitlements(customPolicies: true), store, Access("org-a", "vendor-a"));

        var nav = await resolver.ResolveAsync(User("admin"), "/home", activeKey: null);

        Assert.All(AllItems(nav), i => Assert.Null(i.Count));
    }

    [Fact]
    public async Task ResolverBadgesNothingWhenNothingLapses()
    {
        // A vendor with a valid certification and a vendor with none at all both count for nothing: a
        // badge that is permanently non-zero stops being read (N6).
        var store = new FakeComplianceStore
        {
            Assets = [TestAssets.Vendor("vendor-a", "org-a"), TestAssets.Vendor("vendor-b", "org-a")],
            Assurances = [Assurance("vendor-a", Today.AddDays(365))],
        };
        var resolver = Resolver(
            SuperAdminFacts(), Entitlements(customPolicies: true), store, Access("vendor-a", "vendor-b"));

        var nav = await resolver.ResolveAsync(User("admin"), "/home", activeKey: null);

        Assert.All(AllItems(nav), i => Assert.Null(i.Count));
    }

    [Fact]
    public async Task ResolverBadgesNothingWhenTheStoreThrows()
    {
        var resolver = Resolver(
            SuperAdminFacts(), Entitlements(customPolicies: true), new FakeComplianceStore { Unreachable = true });

        var nav = await resolver.ResolveAsync(User("admin"), "/home", activeKey: null);

        Assert.All(AllItems(nav), i => Assert.Null(i.Count));
    }

    [Fact]
    public async Task ResolvingTheNavThreeTimesReadsTheStoreOnce()
    {
        // The layout resolves the nav three times per render (rail, palette, breadcrumbs).
        var store = new CountingComplianceStore
        {
            Assets = [TestAssets.Vendor("vendor-a", "org-a")],
            Assurances = [Assurance("vendor-a", Today.AddDays(10))],
        };
        var resolver = Resolver(SuperAdminFacts(), Entitlements(customPolicies: true), store, Access("vendor-a"));

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(1, VendorsItem(await resolver.ResolveAsync(User("admin"), "/home", activeKey: null)).Count);
        }

        Assert.Equal(1, store.SnapshotReads);
    }

    [Fact]
    public async Task ResolvingTheNavThreeTimesAgainstAThrowingStoreAttemptsOnce()
    {
        // The failed result is memoized too, so an outage costs one attempt per request rather than three.
        var store = new CountingComplianceStore { Fails = true };
        var resolver = Resolver(SuperAdminFacts(), Entitlements(customPolicies: true), store);

        for (var i = 0; i < 3; i++)
        {
            Assert.All(AllItems(await resolver.ResolveAsync(User("admin"), "/home", activeKey: null)),
                item => Assert.Null(item.Count));
        }

        Assert.Equal(1, store.SnapshotReads);
    }

    [Fact]
    public async Task TheRailAndAnEarlierReaderShareOneSnapshot()
    {
        // The register reads the snapshot before the rail does. The rail must read the SAME one, and must
        // narrow by an accessible set resolved from that snapshot's asset rows - which is what the memo on
        // the access seam guarantees once both go through the cache.
        var store = new CountingComplianceStore
        {
            Assets = [TestAssets.Vendor("vendor-a", "org-a"), TestAssets.Vendor("vendor-b", "org-b")],
            Assurances = [Assurance("vendor-a", Today.AddDays(10)), Assurance("vendor-b", Today.AddDays(10))],
        };
        var cache = new AuthzRequestCache(new FakeAuthzStore(), store);
        var access = Access("vendor-a");

        var inputs = await cache.GetVendorAssuranceInputsAsync();
        await access.AccessibleAssetIdsAsync(User("admin"), inputs.Assets);

        var resolver = Resolver(SuperAdminFacts(), Entitlements(customPolicies: true), store, access, cache);
        var nav = await resolver.ResolveAsync(User("admin"), "/home", activeKey: null);

        Assert.Equal(1, VendorsItem(nav).Count);
        Assert.Equal(1, store.SnapshotReads);
    }

    private static readonly DateOnly Today = new(2026, 3, 1);

    private static VendorAssuranceRow Assurance(string vendorId, DateOnly expires) =>
        new(vendorId, "std-soc2", expires, null);

    private static ShellNavItemView VendorsItem(ShellNavView nav) =>
        AllItems(nav).Single(i => string.Equals(i.Key, "vendors", StringComparison.Ordinal));

    private static IAssetAccess Access(params string[] accessible) =>
        new FixedAssetAccess(new HashSet<string>(accessible, StringComparer.Ordinal));

    // Fixed date and fixed window: a fixture dated against the wall clock changes state as the wall clock
    // moves, and the expired-only case and the nothing-lapses case are the two that flip silently.
    private static ShellNavResolver Resolver(
        IAuthzFactProvider facts,
        IEnterpriseEntitlements entitlements,
        IComplianceStore? store = null,
        IAssetAccess? access = null,
        AuthzRequestCache? cache = null)
    {
        store ??= new FakeComplianceStore();
        return new ShellNavResolver(
            facts,
            entitlements,
            cache ?? new AuthzRequestCache(new FakeAuthzStore(), store),
            access ?? new AllAssetAccess(),
            new FakeTimeProvider(Today),
            Options.Create(new AssuranceOptions { WarnWindowDays = 90 }));
    }

    private sealed class FixedAssetAccess(IReadOnlySet<string> accessible) : IAssetAccess
    {
        public ValueTask<IReadOnlySet<string>> AccessibleAssetIdsAsync(
            ClaimsPrincipal user, IReadOnlyList<AssetNode> assets, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(accessible);
    }

    private sealed class FakeTimeProvider(DateOnly today) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }

    private sealed class CountingComplianceStore : FakeComplianceStore
    {
        public int SnapshotReads { get; private set; }

        public bool Fails { get; init; }

        public override Task<VendorAssuranceInputs> GetVendorAssuranceInputsAsync(CancellationToken cancellationToken = default)
        {
            SnapshotReads++;
            return Fails
                ? throw new InvalidOperationException("store unreachable")
                : base.GetVendorAssuranceInputsAsync(cancellationToken);
        }
    }

    private static IEnumerable<ShellNavItemView> AllItems(ShellNavView nav) => nav.Groups.SelectMany(g => g.Items);

    private static ClaimsPrincipal User(string id)
        => new(new ClaimsIdentity([new Claim(AuthClaims.UserId, id)], "test"));

    private static IEnterpriseEntitlements Entitlements(bool customPolicies) => new FakeEntitlements(customPolicies);

    private static IAuthzFactProvider SuperAdminFacts()
        => new FakeFacts(new HashSet<string>(StringComparer.Ordinal) { AuthzActions.SystemAdmin, AuthzActions.UserManage });

    private static IAuthzFactProvider NoPermissionsFacts()
        => new FakeFacts(new HashSet<string>(StringComparer.Ordinal));

    private static IAuthzFactProvider AssignmentWriteOnOrgFacts()
        => new FakeFacts(
            new HashSet<string>(StringComparer.Ordinal),
            [new AuthzOrgGrant(AuthzActions.AuthzAssignmentWrite, "org-1")]);

    private sealed class FakeEntitlements(bool customPolicies) : IEnterpriseEntitlements
    {
        public bool IsEntitled(EnterpriseEntitlement entitlement)
            => entitlement == EnterpriseEntitlement.CustomPolicies && customPolicies;
    }

    private sealed class FakeFacts(
        IReadOnlySet<string> systemPermissions, IReadOnlyCollection<AuthzOrgGrant>? orgGrants = null)
        : IAuthzFactProvider
    {
        public ValueTask<AuthzPrincipalFacts> LoadFactsAsync(string userId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AuthzPrincipalFacts(systemPermissions, orgGrants ?? []));
    }
}
