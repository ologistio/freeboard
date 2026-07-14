using System.Security.Claims;
using Freeboard.Auth;
using Freeboard.Core.Authz;
using Freeboard.Core.Enterprise;
using Freeboard.Navigation;

namespace Freeboard.Web.Tests;

/// <summary>
/// The app-shell nav catalog and its request-scoped resolver: the catalog is well-formed (N2 - each
/// destination under exactly one group, no duplicate key or route), and the resolver gates the EE/admin
/// items (including Role Assignments on assignment.write-in-any-org), resolves exactly one active item
/// (explicit key first, else longest route match), and never badges (no count source yet, N6).
/// </summary>
public sealed class ShellNavCatalogTests
{
    private static readonly string[] MovedSettingsRoutes =
    [
        "/settings/evidence-collectors", "/settings/attestation-templates",
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
        var resolver = new ShellNavResolver(SuperAdminFacts(), Entitlements(customPolicies: false));

        var nav = await resolver.ResolveAsync(User("admin"), "/home", activeKey: null);

        Assert.DoesNotContain(AllItems(nav), i => string.Equals(i.Key, "custom-roles", StringComparison.Ordinal));

        // Entitlement on plus super-admin: it appears.
        var entitled = new ShellNavResolver(SuperAdminFacts(), Entitlements(customPolicies: true));
        var navOn = await entitled.ResolveAsync(User("admin"), "/home", activeKey: null);
        Assert.Contains(AllItems(navOn), i => string.Equals(i.Key, "custom-roles", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolverDropsAdminItemsForANonAdmin()
    {
        var resolver = new ShellNavResolver(NoPermissionsFacts(), Entitlements(customPolicies: true));

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
        var resolver = new ShellNavResolver(AssignmentWriteOnOrgFacts(), Entitlements(customPolicies: true));

        var nav = await resolver.ResolveAsync(User("member"), "/home", activeKey: null);
        var keys = AllItems(nav).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("role-assignments", keys);
        // The org grant does not unlock the system-admin-gated custom-roles item.
        Assert.DoesNotContain("custom-roles", keys);
    }

    [Fact]
    public async Task ResolverShowsRoleAssignmentsToASystemAdmin()
    {
        var resolver = new ShellNavResolver(SuperAdminFacts(), Entitlements(customPolicies: true));

        var nav = await resolver.ResolveAsync(User("admin"), "/home", activeKey: null);
        var keys = AllItems(nav).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("role-assignments", keys);
    }

    [Fact]
    public async Task ResolverMarksExactlyOneActiveItemByLongestRouteMatch()
    {
        var resolver = new ShellNavResolver(SuperAdminFacts(), Entitlements(customPolicies: true));

        // A nested route under custom-roles lights the custom-roles item, and only it.
        var nav = await resolver.ResolveAsync(User("admin"), "/settings/custom-roles/designer/auditor", activeKey: null);
        var active = AllItems(nav).Where(i => i.IsActive).ToList();

        Assert.Single(active);
        Assert.Equal("custom-roles", active[0].Key);
    }

    [Fact]
    public async Task ResolverPrefersTheExplicitKeyOverTheRouteMatch()
    {
        var resolver = new ShellNavResolver(SuperAdminFacts(), Entitlements(customPolicies: true));

        // The path matches "users", but the page declares "custom-roles" - the explicit key wins.
        var nav = await resolver.ResolveAsync(User("admin"), "/settings/users", activeKey: "custom-roles");
        var active = AllItems(nav).Where(i => i.IsActive).ToList();

        Assert.Single(active);
        Assert.Equal("custom-roles", active[0].Key);
    }

    [Fact]
    public async Task ResolverBadgesNothingBecauseNoCountSourceExists()
    {
        var resolver = new ShellNavResolver(SuperAdminFacts(), Entitlements(customPolicies: true));

        var nav = await resolver.ResolveAsync(User("admin"), "/home", activeKey: null);

        Assert.All(AllItems(nav), i => Assert.Null(i.Count));
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
