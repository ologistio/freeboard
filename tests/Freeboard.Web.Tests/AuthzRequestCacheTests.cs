using Freeboard.Authz;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// The request cache's shared asset read: it carries the assets alone, it is pinned on first use, and an
/// assurance snapshot the request has already taken supplies it without a second read. These are the
/// properties that let an organisation gate stay off a feature table, so each is asserted directly here
/// rather than inferred from a surface that happens to exercise them.
/// </summary>
public sealed class AuthzRequestCacheTests
{
    private static AuthzRequestCache Cache(IComplianceStore store) => new(new FakeAuthzStore(), store);

    private static CountingStore Store() => new()
    {
        Assets = [TestAssets.Org("org-a"), TestAssets.Vendor("vendor-a", "org-a")],
    };

    [Fact]
    public async Task AnAssuranceSnapshotAlreadyTakenServesTheSharedReadWithoutASecondRead()
    {
        var store = Store();
        var cache = Cache(store);

        var inputs = await cache.GetVendorAssuranceInputsAsync();
        var assets = await cache.GetAssetsAsync();

        Assert.Same(inputs.Assets, assets);
        Assert.Equal(0, store.AssetReads);
    }

    [Fact]
    public async Task TheSharedReadIsPinnedSoALaterAssuranceSnapshotDoesNotDisplaceIt()
    {
        // The ordering that a re-evaluated reuse check would break: once a gate has pinned the shared list,
        // a snapshot landing afterwards must not start serving its own to later callers.
        var store = Store();
        var cache = Cache(store);

        var first = await cache.GetAssetsAsync();
        await cache.GetVendorAssuranceInputsAsync();
        var second = await cache.GetAssetsAsync();

        Assert.Same(first, second);
        Assert.Equal(1, store.AssetReads);
    }

    [Fact]
    public async Task TheSharedReadNeverTakesTheAssuranceSnapshot()
    {
        var store = Store();
        var cache = Cache(store);

        await cache.GetAssetsAsync();

        Assert.Equal(0, store.AssuranceReads);
        Assert.Equal(1, store.AssetReads);
    }

    [Fact]
    public async Task AFaultedAssuranceReadLeavesTheSharedReadAnswering()
    {
        // The gate-after-failure ordering: an unmigrated assurance table must degrade the register, not the
        // authorization decisions that never needed it.
        var store = new CountingStore { Assets = [TestAssets.Org("org-a")], FaultAssurances = true };
        var cache = Cache(store);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cache.GetVendorAssuranceInputsAsync());
        var assets = await cache.GetAssetsAsync();

        Assert.Single(assets);
        Assert.Equal(1, store.AssetReads);
    }

    private sealed class CountingStore : FakeComplianceStore
    {
        public int AssetReads { get; private set; }

        public int AssuranceReads { get; private set; }

        public bool FaultAssurances { get; init; }

        public override Task<IReadOnlyList<AssetNode>> GetAssetsAsync(CancellationToken cancellationToken = default)
        {
            AssetReads++;
            return base.GetAssetsAsync(cancellationToken);
        }

        public override Task<VendorAssuranceInputs> GetVendorAssuranceInputsAsync(
            CancellationToken cancellationToken = default)
        {
            AssuranceReads++;
            return FaultAssurances
                ? throw new InvalidOperationException("assurances unreachable")
                : base.GetVendorAssuranceInputsAsync(cancellationToken);
        }
    }
}
