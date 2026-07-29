using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// In-memory <see cref="IComplianceStore"/> double for web tests so the suite is green
/// without MySQL. When <see cref="Unreachable"/> is true, every read throws to
/// simulate a down store. <see cref="Assets"/> is the one unfiltered asset set every read
/// projects from, exactly as the real store serves it.
/// </summary>
internal sealed class FakeComplianceStore : IComplianceStore
{
    public bool Unreachable { get; init; }

    /// <summary>
    /// When true, the reads that surface the asset list - <see cref="GetAssetsAsync"/>
    /// and <see cref="GetStatementOfApplicabilityInputsAsync"/> - throw; the other reads succeed.
    /// </summary>
    public bool AssetsUnreachable { get; init; }

    public IReadOnlyList<StandardRow> Standards { get; set; } = [];

    public IReadOnlyList<RequirementRow> Requirements { get; set; } = [];

    public IReadOnlyList<ControlRow> Controls { get; set; } = [];

    public IReadOnlyList<AssetNode> Assets { get; set; } = [];

    public IReadOnlyList<ScopeRow> Scopes { get; set; } = [];

    public IReadOnlyList<CollectorRow> Collectors { get; set; } = [];

    public IReadOnlyList<IntegrationConnectionRow> Connections { get; set; } = [];

    public Task<IReadOnlyList<StandardRow>> GetStandardsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Standards);

    public Task<IReadOnlyList<RequirementRow>> GetRequirementsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Requirements);

    public Task<IReadOnlyList<ControlRow>> GetControlsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Controls);

    public Task<IReadOnlyList<AssetNode>> GetAssetsAsync(CancellationToken cancellationToken = default)
    {
        if (AssetsUnreachable)
        {
            throw new InvalidOperationException("assets unreachable");
        }

        return Guard(() => Assets);
    }

    public Task<IReadOnlyList<ScopeRow>> GetScopesAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Scopes);

    public Task<IReadOnlyList<CollectorRow>> GetCollectorsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Collectors);

    public Task<IReadOnlyList<IntegrationConnectionRow>> GetIntegrationConnectionsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => Connections);

    public Task<SoaInputs> GetStatementOfApplicabilityInputsAsync(CancellationToken cancellationToken = default)
    {
        if (AssetsUnreachable)
        {
            throw new InvalidOperationException("assets unreachable");
        }

        return Guard(() => new SoaInputs(Assets, Scopes, Requirements));
    }

    public Task<SoaDrilldownInputs> GetStatementOfApplicabilityDrilldownInputsAsync(CancellationToken cancellationToken = default)
    {
        if (AssetsUnreachable)
        {
            throw new InvalidOperationException("assets unreachable");
        }

        return Guard(() => new SoaDrilldownInputs(Assets, Scopes, Requirements, Controls, Collectors));
    }

    public Task<ComplianceCounts> GetCountsAsync(CancellationToken cancellationToken = default) =>
        Guard(() => new ComplianceCounts(
            Standards.Count, Controls.Count, Requirements.Count, Assets.Count(a => a.IsOrganisation), Scopes.Count,
            Assets.Count(a => a.Type is "Vendor"), Collectors.Count));

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

    public static AssetNode Vendor(string id, string? owner, string? title = null)
        => new(id, title ?? id, "Vendor", "declared", null, null, owner);

    public static AssetNode Machine(
        string id, string? parent, string source = "declared", string? state = null, string? title = null)
        => new(id, title ?? id, "Machine", source, state, parent, null);
}
