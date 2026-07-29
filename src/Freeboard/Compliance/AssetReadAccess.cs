using Freeboard.Persistence;

namespace Freeboard.Compliance;

/// <summary>
/// The single read-access rule: close a caller's accessible-organisation union over the asset tree.
/// Pure (no I/O), so both the authz-backed seam and its test double share one rule and the surfaces that
/// consume it cannot each invent their own.
///
/// The branches key off which EDGE an asset carries, never its type - <c>parent</c> and <c>owner</c> are
/// mutually exclusive - which is what makes a vendor fall out through its <c>owner</c> rather than
/// through a vendor-specific test. An asset whose edge is missing or dangling is unreachable, so an
/// ownerless vendor is visible to nobody in any rollout mode: the mode widens the organisation union
/// handed in here, it does not relax these edges.
/// </summary>
public static class AssetReadAccess
{
    public static IReadOnlySet<string> AccessibleAssetIds(
        IReadOnlyList<AssetNode> assets, IReadOnlySet<string> organisationUnion)
    {
        var byId = assets.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var ancestryCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        var accessible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in assets)
        {
            // A retired discovered asset is not a live authorization anchor, so it anchors no read.
            if (string.Equals(asset.Source, "discovered", StringComparison.Ordinal)
                && string.Equals(asset.State, "Retired", StringComparison.Ordinal))
            {
                continue;
            }

            var reachable = asset.Parent is not null
                ? AssetAncestry.InclusiveAncestors(asset.Id, byId, ancestryCache).Any(organisationUnion.Contains)
                : asset.Owner is not null
                    ? organisationUnion.Contains(asset.Owner)
                    : organisationUnion.Contains(asset.Id);
            if (reachable)
            {
                accessible.Add(asset.Id);
            }
        }

        return accessible;
    }
}
