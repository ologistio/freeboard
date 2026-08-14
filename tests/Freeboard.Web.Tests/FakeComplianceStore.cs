using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// In-memory <see cref="IComplianceStore"/> double for web tests so the suite is green
/// without MySQL. When <see cref="Unreachable"/> is true, every read throws to simulate a down store.
/// <see cref="Assets"/> is the one unfiltered asset set every read projects from, exactly as the real
/// store serves it. <see cref="SnapshotReads"/> records the shape of each snapshot served, so a test can
/// assert which sets a surface named and how many reads it cost.
/// </summary>
internal sealed class FakeComplianceStore : IComplianceStore
{
    // The app's startup token-resolvability warning reads off the request path, so the recording lists
    // are written from a background thread while a test thread reads them. Guard both, and hand out
    // snapshots, exactly as the connection-factory double does.
    private readonly Lock _gate = new();
    private readonly List<ComplianceReadSet> _snapshotReads = [];
    private readonly List<IReadOnlyList<AssetNode>?> _servedAssets = [];
    private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Unreachable { get; init; }

    /// <summary>
    /// The sets whose table is faulted. A snapshot naming any of them throws, so a test can fault the
    /// assurance table alone - the shape of a schema that has not had the migration applied - or the
    /// asset-bearing reads, without naming a method.
    /// </summary>
    public ComplianceReadSet Faulted { get; init; }

    /// <summary>The shape of every snapshot served, in the order the request asked for them.</summary>
    public IReadOnlyList<ComplianceReadSet> SnapshotReads
    {
        get
        {
            lock (_gate)
            {
                return [.. _snapshotReads];
            }
        }
    }

    /// <summary>
    /// The asset list served with each snapshot, index-aligned with <see cref="SnapshotReads"/> and null
    /// where the snapshot named no assets. A narrowing decision must resolve its accessible set from the
    /// list of the snapshot its rows came from, so a test compares the two by reference.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<AssetNode>?> ServedAssets
    {
        get
        {
            lock (_gate)
            {
                return [.. _servedAssets];
            }
        }
    }

    /// <summary>
    /// Completes when the store has served its first snapshot. The app's only boot-time read is the
    /// startup token warning, which runs on a background task, so a test awaits this instead of polling
    /// a count.
    /// </summary>
    public Task FirstRead => _firstRead.Task;

    /// <summary>
    /// The assets to serve from the SECOND asset-bearing snapshot onward, when set. A decision anchored
    /// on a snapshot it did not read then reaches a different tree, so the mistake shows up as a
    /// different outcome rather than as an identical read sequence.
    /// </summary>
    public IReadOnlyList<AssetNode>? AssetsAfterFirstRead { get; init; }

    public IReadOnlyList<StandardRow> Standards { get; set; } = [];

    public IReadOnlyList<RequirementRow> Requirements { get; set; } = [];

    public IReadOnlyList<ControlRow> Controls { get; set; } = [];

    public IReadOnlyList<AssetNode> Assets { get; set; } = [];

    public IReadOnlyList<ScopeRow> Scopes { get; set; } = [];

    public IReadOnlyList<CollectorRow> Collectors { get; set; } = [];

    public IReadOnlyList<IntegrationConnectionRow> Connections { get; set; } = [];

    public IReadOnlyList<VendorAssuranceRow> Assurances { get; set; } = [];

    public Task<ComplianceSnapshot> GetSnapshotAsync(
        ComplianceReadSet sets, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AssetNode>? assets;
        lock (_gate)
        {
            _snapshotReads.Add(sets);

            var source = AssetsAfterFirstRead is not null && _servedAssets.Exists(a => a is not null)
                ? AssetsAfterFirstRead
                : Assets;

            // A fresh asset list per snapshot: the accessible-set memo keys on the list, so a shared
            // instance would collapse two reads into one key and let a memo test pass without the code
            // doing anything.
            assets = Named(sets, ComplianceReadSet.Assets, (IReadOnlyList<AssetNode>)[.. source]);
            _servedAssets.Add(assets);
        }

        _firstRead.TrySetResult();

        if ((sets & Faulted) != ComplianceReadSet.None)
        {
            throw new InvalidOperationException($"{sets & Faulted} unreachable");
        }

        return Guard(() => new ComplianceSnapshot(
            sets,
            assets: assets,
            standards: Named(sets, ComplianceReadSet.Standards, Standards),
            requirements: Named(sets, ComplianceReadSet.Requirements, Requirements),
            controls: Named(sets, ComplianceReadSet.Controls, Controls),
            scopes: Named(sets, ComplianceReadSet.Scopes, Scopes),
            collectors: Named(sets, ComplianceReadSet.Collectors, Collectors),
            integrationConnections: Named(sets, ComplianceReadSet.IntegrationConnections, Connections),
            vendorAssurances: Named(sets, ComplianceReadSet.VendorAssurances, Assurances)));
    }

    public Task<ComplianceCounts> GetCountsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => new ComplianceCounts(
            Standards.Count, Controls.Count, Requirements.Count, Assets.Count(a => a.IsOrganisation), Scopes.Count,
            Assets.Count(a => a.Type is "Vendor"), Collectors.Count));

    // Null for a set the snapshot does not name, so reading it throws just as it does in production.
    private static IReadOnlyList<T>? Named<T>(
        ComplianceReadSet sets, ComplianceReadSet set, IReadOnlyList<T> rows) =>
        sets.HasFlag(set) ? rows : null;

    private Task<T> Guard<T>(Func<T> value)
    {
        if (Unreachable)
        {
            throw new InvalidOperationException("store unreachable");
        }

        return Task.FromResult(value());
    }
}

/// <summary>
/// Terse <see cref="AssetNode"/> constructors for fixtures, so a test names only the fields its case
/// turns on rather than spelling every column of the unified row at ~130 call sites.
/// </summary>
internal static class TestAssets
{
    public static AssetNode Org(string id, string? parent = null, string kind = "Company", string? title = null)
        => new(id, title ?? id, kind, "declared", null, parent, null);

    public static AssetNode Vendor(
        string id, string? owner, string? title = null, string? tier = null, IReadOnlyList<string>? dataClasses = null)
        => new(id, title ?? id, "Vendor", "declared", null, null, owner) { Tier = tier, DataClasses = dataClasses ?? [] };

    public static AssetNode Machine(
        string id, string? parent, string source = "declared", string? state = null, string? title = null)
        => new(id, title ?? id, "Machine", source, state, parent, null);
}
