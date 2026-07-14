using System.Security.Claims;
using Freeboard.Authz;
using Freeboard.Core.Authz;
using Freeboard.Core.Enterprise;

namespace Freeboard.Navigation;

/// <summary>A resolved rail item: display data plus whether it is the active page and its badge count.</summary>
public sealed record ShellNavItemView(string Key, string Label, string Route, bool IsActive, int? Count);

/// <summary>A resolved rail group: its label (null for the top set) and the items that survived gating.</summary>
public sealed record ShellNavGroupView(string? Label, IReadOnlyList<ShellNavItemView> Items);

/// <summary>The resolved rail: only groups that still have at least one visible item.</summary>
public sealed record ShellNavView(IReadOnlyList<ShellNavGroupView> Groups);

/// <summary>
/// Evaluates <see cref="ShellNavCatalog"/> for the current request: it drops items whose entitlement or
/// authorization gate fails (a dropped item emits no label and no href, so a gated destination is never
/// leaked), marks exactly one item active (an explicit page-declared key first, else the longest route
/// that prefixes the current path), and attaches a badge count where a source exists. No actionable-count
/// source exists yet, so every count stays null and nothing badges (N6 - no fabricated badges).
/// Request-scoped, mirroring the per-request authz/entitlement calls the layout already made.
/// </summary>
public sealed class ShellNavResolver(IAuthzFactProvider facts, IEnterpriseEntitlements entitlements)
{
    public async Task<ShellNavView> ResolveAsync(
        ClaimsPrincipal user, string currentPath, string? activeKey, CancellationToken cancellationToken = default)
    {
        var path = (currentPath ?? "/").ToLowerInvariant();

        var visible = new List<(ShellNavItem Item, ShellNavGroup Group)>();
        foreach (var group in ShellNavCatalog.Groups)
        {
            foreach (var item in group.Items)
            {
                if (await IsVisibleAsync(item, user, cancellationToken).ConfigureAwait(false))
                {
                    visible.Add((item, group));
                }
            }
        }

        var active = ResolveActiveKey(visible.Select(v => v.Item).ToList(), path, activeKey);

        var groups = ShellNavCatalog.Groups
            .Select(g => new ShellNavGroupView(
                g.Label,
                visible.Where(v => ReferenceEquals(v.Group, g))
                    .Select(v => new ShellNavItemView(
                        v.Item.Key, v.Item.Label, v.Item.Route,
                        string.Equals(v.Item.Key, active, StringComparison.Ordinal),
                        Count: null))
                    .ToList()))
            .Where(g => g.Items.Count > 0)
            .ToList();

        return new ShellNavView(groups);
    }

    private async Task<bool> IsVisibleAsync(ShellNavItem item, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (item.Entitlement is { } entitlement && !entitlements.IsEntitled(entitlement))
        {
            return false;
        }

        return item.Access switch
        {
            ShellNavAccess.CanReachAdmin =>
                await AuthzViewHelpers.CanReachAdminAsync(facts, user, cancellationToken).ConfigureAwait(false),
            ShellNavAccess.CanAdministerSystem =>
                await AuthzViewHelpers.CanAdministerSystemAsync(facts, user, cancellationToken).ConfigureAwait(false),
            ShellNavAccess.CanReachRoleAssignments =>
                await AuthzViewHelpers.CanReachRoleAssignmentsAsync(facts, user, cancellationToken).ConfigureAwait(false),
            _ => true,
        };
    }

    private static string? ResolveActiveKey(IReadOnlyList<ShellNavItem> visible, string path, string? activeKey)
    {
        // An explicit page-declared key wins, but only if that item is actually visible.
        if (!string.IsNullOrEmpty(activeKey)
            && visible.Any(i => string.Equals(i.Key, activeKey, StringComparison.Ordinal)))
        {
            return activeKey;
        }

        // Else the longest route that equals or prefixes the current path, so a nested route (e.g. a
        // designer under a list) still lights its parent item.
        return visible
            .Where(i => IsRouteMatch(path, i.Route.ToLowerInvariant()))
            .OrderByDescending(i => i.Route.Length)
            .Select(i => i.Key)
            .FirstOrDefault();
    }

    private static bool IsRouteMatch(string path, string route)
        => path == route || path.StartsWith(route + "/", StringComparison.Ordinal);
}
