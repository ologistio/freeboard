using Freeboard.Compliance;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// The one read-access rule: close a granted organisation union over the asset tree. The branches key
/// off the edge an asset carries, never its type, so a vendor falls out through its <c>owner</c> and an
/// asset with a missing or dangling edge is unreachable to everyone.
/// </summary>
public sealed class AssetReadAccessTests
{
    private static IReadOnlySet<string> Union(params string[] ids) => ids.ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void ParentChainHitAdmitsAndAMissExcludes()
    {
        IReadOnlyList<AssetNode> assets =
        [
            TestAssets.Org("company"),
            TestAssets.Org("dept", "company"),
            TestAssets.Machine("m-in", "dept"),
            TestAssets.Org("other"),
            TestAssets.Machine("m-out", "other"),
        ];

        var accessible = AssetReadAccess.AccessibleAssetIds(assets, Union("company"));

        Assert.Equal(Union("company", "dept", "m-in"), accessible);
    }

    [Fact]
    public void OwnerHitAdmitsAndAMissExcludes()
    {
        IReadOnlyList<AssetNode> assets =
        [
            TestAssets.Org("company"),
            TestAssets.Org("other"),
            TestAssets.Vendor("v-mine", "company"),
            TestAssets.Vendor("v-theirs", "other"),
        ];

        var accessible = AssetReadAccess.AccessibleAssetIds(assets, Union("company"));

        Assert.Equal(Union("company", "v-mine"), accessible);
    }

    [Fact]
    public void MissingOrDanglingEdgeIsUnreachableEvenWithTheWholeUnion()
    {
        // The union is built from the organisation-typed assets, so an edge naming an id no asset
        // defines can never intersect it. Handing in EVERY organisation still leaves these out.
        IReadOnlyList<AssetNode> assets =
        [
            TestAssets.Org("company"),
            TestAssets.Vendor("v-ownerless", null),
            TestAssets.Vendor("v-dangling", "gone-org"),
            TestAssets.Machine("m-unrooted", null),
            TestAssets.Machine("m-dangling", "gone-org"),
        ];

        var accessible = AssetReadAccess.AccessibleAssetIds(assets, Union("company"));

        Assert.Equal(Union("company"), accessible);
    }

    [Fact]
    public void AnEdgelessAssetIsAdmittedOnlyByItsOwnId()
    {
        IReadOnlyList<AssetNode> assets = [TestAssets.Org("root"), TestAssets.Org("elsewhere")];

        Assert.Equal(Union("root"), AssetReadAccess.AccessibleAssetIds(assets, Union("root")));
    }

    [Fact]
    public void RetiredDiscoveredAssetIsExcludedEvenUnderAnAdmittingChain()
    {
        IReadOnlyList<AssetNode> assets =
        [
            TestAssets.Org("company"),
            TestAssets.Machine("m-seen", "company", source: "discovered", state: "Seen"),
            TestAssets.Machine("m-retired", "company", source: "discovered", state: "Retired"),
            // A DECLARED machine reads a null state, which is live.
            TestAssets.Machine("m-declared", "company"),
        ];

        var accessible = AssetReadAccess.AccessibleAssetIds(assets, Union("company"));

        Assert.Equal(Union("company", "m-seen", "m-declared"), accessible);
    }

    [Fact]
    public void AParentCycleTerminates()
    {
        IReadOnlyList<AssetNode> assets =
        [
            TestAssets.Org("a", "b"),
            TestAssets.Org("b", "a"),
            TestAssets.Machine("m", "a"),
            TestAssets.Org("outside"),
        ];

        // The walk terminates rather than spinning, and nothing in the cycle is reachable from outside it.
        Assert.Equal(Union("outside"), AssetReadAccess.AccessibleAssetIds(assets, Union("outside")));
        // A grant inside the cycle reaches both its members and whatever hangs under them.
        Assert.Equal(Union("a", "b", "m"), AssetReadAccess.AccessibleAssetIds(assets, Union("b")));
    }

    [Fact]
    public void TheChainCrossesANonOrganisationLink()
    {
        // The read closure is edge-only: an organisation parented onto a machine is readable to a
        // caller granted beyond that machine. The write gate cuts the same link; the read does not.
        IReadOnlyList<AssetNode> assets =
        [
            TestAssets.Org("company"),
            TestAssets.Org("dept", "company"),
            TestAssets.Machine("m-1", "dept"),
            TestAssets.Org("far", "m-1"),
        ];

        Assert.Contains("far", AssetReadAccess.AccessibleAssetIds(assets, Union("company")));
    }

    [Fact]
    public void AnEmptyUnionAdmitsNothing()
    {
        IReadOnlyList<AssetNode> assets =
        [
            TestAssets.Org("company"), TestAssets.Machine("m", "company"), TestAssets.Vendor("v", "company"),
        ];

        Assert.Empty(AssetReadAccess.AccessibleAssetIds(assets, Union()));
    }
}
