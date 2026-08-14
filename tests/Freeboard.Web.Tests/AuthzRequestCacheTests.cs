using Freeboard.Authz;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// The request cache's shared asset read: it names the assets alone, it is pinned on first use, and a
/// wider snapshot the request has already taken supplies it without a second read. These are the
/// properties that let an organisation gate stay off a feature table, so each is asserted directly here
/// rather than inferred from a surface that happens to exercise them.
/// </summary>
public sealed class AuthzRequestCacheTests
{
    private const ComplianceReadSet Register = ComplianceReadSet.Assets | ComplianceReadSet.VendorAssurances;

    private static AuthzRequestCache Cache(IComplianceStore store) => new(new FakeAuthzStore(), store);

    private static FakeComplianceStore Store() => new()
    {
        Assets = [TestAssets.Org("org-a"), TestAssets.Vendor("vendor-a", "org-a")],
    };

    [Fact]
    public async Task AWiderSnapshotAlreadyTakenServesTheSharedReadWithoutASecondRead()
    {
        var store = Store();
        var cache = Cache(store);

        var snapshot = await cache.GetSnapshotAsync(Register);
        var assets = await cache.GetAssetsAsync();

        Assert.Same(snapshot.Assets, assets);
        Assert.Equal([Register], store.SnapshotReads);
    }

    [Fact]
    public async Task TheSharedReadIsPinnedSoALaterWiderSnapshotDoesNotDisplaceIt()
    {
        // The ordering that a re-evaluated reuse check would break: once a gate has pinned the shared list,
        // a snapshot landing afterwards must not start serving its own to later callers.
        var store = Store();
        var cache = Cache(store);

        var first = await cache.GetAssetsAsync();
        await cache.GetSnapshotAsync(Register);
        var second = await cache.GetAssetsAsync();

        Assert.Same(first, second);
        Assert.Equal([ComplianceReadSet.Assets, Register], store.SnapshotReads);
    }

    [Fact]
    public async Task TheSharedReadNamesTheAssetsAndNoPayloadSet()
    {
        var store = Store();
        var cache = Cache(store);

        await cache.GetAssetsAsync();

        Assert.Equal([ComplianceReadSet.Assets], store.SnapshotReads);
    }

    [Fact]
    public async Task AnOrganisationResourceBuiltFromASnapshotWalksThatSnapshotsAssets()
    {
        // The overload every stored-row gate takes. Its whole point is that the ancestry comes from the
        // asset rows the row was read with, even when the request has already pinned a different shared
        // read: a chain walked over the pinned list would authorize the write against a tree the row's
        // own snapshot never had.
        var store = new FakeComplianceStore
        {
            Assets = [TestAssets.Org("root-a"), TestAssets.Org("org-1", "root-a", "Department")],
            AssetsAfterFirstRead = [TestAssets.Org("root-b"), TestAssets.Org("org-1", "root-b", "Department")],
        };
        var cache = Cache(store);

        await cache.GetAssetsAsync();
        var stored = await cache.GetSnapshotAsync(ComplianceReadSet.Assets | ComplianceReadSet.Scopes);
        var resource = cache.OrganisationResource("scope", "s1", "org-1", stored);

        Assert.Equal(["org-1", "root-b"], resource.OrgAncestryInclusive);
    }

    [Fact]
    public async Task AFaultedAssuranceReadLeavesTheSharedReadAnswering()
    {
        // The gate-after-failure ordering: an unmigrated assurance table must degrade the register, not the
        // authorization decisions that never needed it. A faulted snapshot is kept nowhere, so the gate
        // that follows reads the assets alone and answers.
        var store = new FakeComplianceStore
        {
            Assets = [TestAssets.Org("org-a")],
            Faulted = ComplianceReadSet.VendorAssurances,
        };
        var cache = Cache(store);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cache.GetSnapshotAsync(Register));
        var assets = await cache.GetAssetsAsync();

        Assert.Single(assets);
        Assert.Equal([Register, ComplianceReadSet.Assets], store.SnapshotReads);
    }
}
