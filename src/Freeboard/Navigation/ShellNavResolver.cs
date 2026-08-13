using System.Security.Claims;
using Freeboard.Authz;
using Freeboard.Compliance;
using Freeboard.Core.Assets;
using Freeboard.Core.Authz;
using Freeboard.Core.Enterprise;
using Freeboard.Web;
using Microsoft.Extensions.Options;

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
/// that prefixes the current path), and attaches a badge count where a source exists.
///
/// The Vendors item is the one badged source: how many readable vendors hold an expiring or already
/// expired certification. A vendor holding no certification is never counted, because a badge that is
/// permanently non-zero stops being read (N6). The count reads the request's memoized snapshot on
/// <see cref="AuthzRequestCache"/> rather than taking its own: the accessible set that narrows it is
/// resolved once per request from whichever asset list reaches the seam first, so a second read would
/// narrow these assurance rows with another surface's owner edges. The count is memoized on the instance
/// because the layout resolves the navigation up to three times per render, and it is computed inside the
/// store-failure catch so an outage leaves the item unbadged rather than failing every page in the app.
/// Request-scoped, mirroring the per-request authz/entitlement calls the layout already made.
/// </summary>
public sealed class ShellNavResolver(
    IAuthzFactProvider facts,
    IEnterpriseEntitlements entitlements,
    AuthzRequestCache cache,
    IAssetAccess assetAccess,
    TimeProvider clock,
    IOptions<AssuranceOptions> assurance)
{
    private bool _countResolved;
    private int? _lapsingVendorCount;

    public async Task<ShellNavView> ResolveAsync(
        ClaimsPrincipal user, string currentPath, string? activeKey, CancellationToken cancellationToken = default)
    {
        var path = (currentPath ?? "/").ToLowerInvariant();
        var lapsingVendors = await LapsingVendorCountAsync(user, cancellationToken).ConfigureAwait(false);

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
                        Count: string.Equals(v.Item.Key, "vendors", StringComparison.Ordinal) ? lapsingVendors : null))
                    .ToList()))
            .Where(g => g.Items.Count > 0)
            .ToList();

        return new ShellNavView(groups);
    }

    // Null when nothing lapses and null when the store fails: the shell renders an item with no count
    // source unbadged either way, which is the safe reading of a number nobody could compute. The failed
    // result is memoized too, so an outage costs one attempt per request rather than three.
    private async Task<int?> LapsingVendorCountAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (_countResolved)
        {
            return _lapsingVendorCount;
        }

        _countResolved = true;
        try
        {
            var inputs = await cache.GetVendorAssuranceInputsAsync(cancellationToken).ConfigureAwait(false);
            var accessible = await assetAccess
                .AccessibleAssetIdsAsync(user, inputs.Assets, cancellationToken).ConfigureAwait(false);
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var warnWindowDays = assurance.Value.WarnWindowDays;

            // Narrowed the same way the register narrows, on both tests: an accessible id AND a Vendor
            // row. The badge and the page must not be able to disagree about which vendors count.
            var vendorIds = inputs.Assets
                .Where(a => a.Type is "Vendor")
                .Select(a => a.Id)
                .ToHashSet(StringComparer.Ordinal);

            var count = inputs.Assurances
                .Where(a => vendorIds.Contains(a.VendorId)
                    && accessible.Contains(a.VendorId)
                    && VendorAssurance.Evaluate(a.Expires, a.WarnDays ?? warnWindowDays, today) is not AssuranceStatus.Valid)
                .Select(a => a.VendorId)
                .Distinct(StringComparer.Ordinal)
                .Count();

            return _lapsingVendorCount = count > 0 ? count : null;
        }
        catch (Exception ex) when (ComplianceEndpoints.IsStoreFailure(ex))
        {
            return _lapsingVendorCount = null;
        }
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
