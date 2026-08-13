using System.Security.Claims;
using Freeboard.Compliance;
using Freeboard.Persistence;

namespace Freeboard.Web;

/// <summary>
/// The single accessibility seam. A pure function of the already-loaded asset list that returns the
/// subset of asset ids the user may read, so selection and scoping fail closed. It performs NO store
/// read of its own - the caller passes the list it already loaded. Grants stay on organisations; this
/// seam is where the granted organisation union is closed over the asset tree.
/// </summary>
public interface IAssetAccess
{
    /// <summary>
    /// The subset of the supplied assets the user may read. Pass the UNFILTERED list: the authz-backed
    /// default closes an organisation union over the <c>parent</c> and <c>owner</c> edges it walks, so a
    /// narrowed list breaks the closure by hiding the very ancestors and owners that admit an asset. Async
    /// because that default reads the principal's grants, memoized alongside the fact load.
    /// </summary>
    ValueTask<IReadOnlySet<string>> AccessibleAssetIdsAsync(
        ClaimsPrincipal user,
        IReadOnlyList<AssetNode> assets,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Grants every authenticated user the whole organisation union, then applies the same closure the
/// authz-backed seam does - so an ownerless vendor and a retired discovered asset stay out. Retained as
/// a unit-test double for the selection/resolver logic; the app default is <c>AuthzAssetAccess</c>
/// (see Program.cs).
/// </summary>
public sealed class AllAssetAccess : IAssetAccess
{
    public ValueTask<IReadOnlySet<string>> AccessibleAssetIdsAsync(
        ClaimsPrincipal user,
        IReadOnlyList<AssetNode> assets,
        CancellationToken cancellationToken = default)
    {
        var organisationUnion = assets
            .Where(a => a.IsOrganisation)
            .Select(a => a.Id)
            .ToHashSet(StringComparer.Ordinal);
        return ValueTask.FromResult(AssetReadAccess.AccessibleAssetIds(assets, organisationUnion));
    }
}
