using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;

namespace Freeboard.Persistence;

/// <summary>
/// MySQL-backed <see cref="IComplianceStore"/> using hand-written joined reads via
/// Dapper. Domain rows are ordered by id; relation id arrays are ordered by id.
/// </summary>
public sealed class MySqlComplianceStore(IDbConnectionFactory connectionFactory) : IComplianceStore
{
    public async Task<IReadOnlyList<StandardRow>> GetStandardsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<StandardRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, version AS Version, authority AS Authority, "
            + "publisher AS Publisher, source_url AS SourceUrl FROM standards ORDER BY id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<RequirementRow>> GetRequirementsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<RequirementRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, standard_id AS Standard, theme AS Theme, statement AS Statement, "
            + "guidance AS Guidance, citation_label AS CitationLabel, citation_url AS CitationUrl "
            + "FROM requirements ORDER BY id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<ControlRow>> GetControlsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // One consistent snapshot so the controls and their maps_to rows cannot straddle
        // a concurrent gitops sync commit and pair old controls with new cross-refs.
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        var controls = (await connection.QueryAsync<(string Id, string Title, string? Evaluation)>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, evaluation AS Evaluation FROM controls ORDER BY id;",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var links = await connection.QueryAsync<(string ControlId, string RequirementId)>(new CommandDefinition(
            "SELECT control_id AS ControlId, requirement_id AS RequirementId FROM control_requirements "
            + "ORDER BY control_id, requirement_id;",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var mapsTo = links
            .GroupBy(l => l.ControlId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(l => l.RequirementId).ToList(), StringComparer.Ordinal);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return controls
            .Select(c => new ControlRow(
                c.Id,
                c.Title,
                mapsTo.TryGetValue(c.Id, out var ids) ? ids : [],
                c.Evaluation))
            .ToList();
    }

    // One unfiltered asset read shared by every consumer. No WHERE clause: the resolution tree needs a
    // retired machine's parent edge, the drill-down's vendor titles must not be retirement-filtered, and
    // the live-subject predicate needs the retired rows in order to classify them - no single predicate
    // serves all three, and a second query is exactly what this read exists to avoid.
    private const string AssetSelect =
        "SELECT id AS Id, title AS Title, type AS Type, source AS Source, state AS State, "
        + "parent AS Parent, owner AS Owner, tier AS Tier, data_classes AS DataClasses FROM assets ORDER BY id;";

    // data_classes is a JSON array column, so the row binds to raw text and is projected below; the rest
    // of the columns map straight onto AssetNode.
    private sealed record AssetColumns(
        string Id, string Title, string Type, string Source, string? State, string? Parent, string? Owner,
        string? Tier, string? DataClasses);

    public async Task<IReadOnlyList<AssetNode>> GetAssetsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAssetsAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The one asset read. Every caller goes through it, including the snapshot readers, so the JSON
    /// projection cannot be applied on one path and skipped on another.
    /// </summary>
    private static async Task<List<AssetNode>> ReadAssetsAsync(
        DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<AssetColumns>(new CommandDefinition(
            AssetSelect, transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => new AssetNode(r.Id, r.Title, r.Type, r.Source, r.State, r.Parent, r.Owner)
        {
            Tier = r.Tier,
            DataClasses = ReadDataClasses(r.DataClasses),
        }).ToList();
    }

    // A null or unreadable column reads as empty rather than throwing: the register renders "Not tracked"
    // either way, and a malformed row must not take down every asset read.
    private static IReadOnlyList<string> ReadDataClasses(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private const string ScopeSelect =
        "SELECT id AS Id, title AS Title, subject_id AS Subject, standard_id AS Standard, "
        + "requirement_id AS Requirement, control_id AS Control, disposition AS Disposition, "
        + "justification AS Justification FROM scopes ORDER BY id;";

    public async Task<IReadOnlyList<ScopeRow>> GetScopesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ScopeRow>(new CommandDefinition(
            ScopeSelect, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<CollectorRow>> GetCollectorsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<CollectorColumns>(new CommandDefinition(
            CollectorSelect,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(ToCollectorRow).ToList();
    }

    /// <summary>
    /// The `collectors` column list, shared by the list read and the drill-down snapshot read so the two
    /// cannot diverge on which columns a collector row carries.
    /// </summary>
    private const string CollectorSelect =
        "SELECT id AS Id, title AS Title, control_id AS Control, vendor_id AS Vendor, type AS Type, "
        + "provider AS Provider, frequency AS Frequency, threshold AS Threshold, config AS Config, "
        + "connection_id AS Connection FROM collectors ORDER BY id;";

    private sealed record CollectorColumns(
        string Id,
        string Title,
        string Control,
        string? Vendor,
        string Type,
        string? Provider,
        string Frequency,
        int? Threshold,
        string? Config,
        string? Connection);

    /// <summary>
    /// Projects a raw collector row, binding the stored `config` through the single store-boundary
    /// redaction so no caller can reach a quiz answer.
    /// </summary>
    private static CollectorRow ToCollectorRow(CollectorColumns r) => new(
        r.Id, r.Title, r.Control, r.Vendor, r.Type, r.Provider, r.Frequency, r.Threshold,
        StoredCollectorConfig.Read(r.Config), r.Connection);

    public async Task<IReadOnlyList<IntegrationConnectionRow>> GetIntegrationConnectionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<IntegrationConnectionRow>(new CommandDefinition(
            "SELECT id AS Id, provider AS Provider, base_url AS BaseUrl, discovery_cadence AS DiscoveryCadence, "
            + "vendor_id AS Vendor FROM integration_connections ORDER BY id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<SoaInputs> GetStatementOfApplicabilityInputsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // One consistent snapshot so the three SoA inputs cannot straddle a concurrent gitops sync
        // commit and pair, say, old assets with new scopes.
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        var assets = await ReadAssetsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        var scopes = (await connection.QueryAsync<ScopeRow>(new CommandDefinition(
            ScopeSelect,
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var requirements = (await connection.QueryAsync<RequirementRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, standard_id AS Standard, theme AS Theme, statement AS Statement, "
            + "guidance AS Guidance, citation_label AS CitationLabel, citation_url AS CitationUrl "
            + "FROM requirements ORDER BY id;",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SoaInputs(assets, scopes, requirements);
    }

    public async Task<SoaDrilldownInputs> GetStatementOfApplicabilityDrilldownInputsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // One consistent snapshot so the drill-down inputs cannot straddle a concurrent gitops sync
        // commit and pair, say, old controls with new collectors.
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        var assets = await ReadAssetsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        var scopes = (await connection.QueryAsync<ScopeRow>(new CommandDefinition(
            ScopeSelect,
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var requirements = (await connection.QueryAsync<RequirementRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, standard_id AS Standard, theme AS Theme, statement AS Statement, "
            + "guidance AS Guidance, citation_label AS CitationLabel, citation_url AS CitationUrl "
            + "FROM requirements ORDER BY id;",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var controlRows = (await connection.QueryAsync<(string Id, string Title, string? Evaluation)>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, evaluation AS Evaluation FROM controls ORDER BY id;",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var links = await connection.QueryAsync<(string ControlId, string RequirementId)>(new CommandDefinition(
            "SELECT control_id AS ControlId, requirement_id AS RequirementId FROM control_requirements "
            + "ORDER BY control_id, requirement_id;",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var mapsTo = links
            .GroupBy(l => l.ControlId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(l => l.RequirementId).ToList(), StringComparer.Ordinal);

        var controls = controlRows
            .Select(c => new ControlRow(c.Id, c.Title, mapsTo.TryGetValue(c.Id, out var ids) ? ids : [], c.Evaluation))
            .ToList();

        var collectors = (await connection.QueryAsync<CollectorColumns>(new CommandDefinition(
            CollectorSelect,
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false))
            .Select(ToCollectorRow)
            .ToList();

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SoaDrilldownInputs(assets, scopes, requirements, controls, collectors);
    }

    private const string VendorAssuranceSelect =
        "SELECT vendor_id AS VendorId, standard_id AS StandardId, expires AS Expires, warn_days AS WarnDays "
        + "FROM vendor_assurances ORDER BY vendor_id, standard_id;";

    public async Task<VendorAssuranceInputs> GetVendorAssuranceInputsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // One consistent snapshot: every caller narrows the assurances by the owner edges on the assets,
        // so two autocommit reads could pair pre-sync owner edges with post-sync assurance rows.
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        var assets = await ReadAssetsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        var assurances = (await connection.QueryAsync<VendorAssuranceRow>(new CommandDefinition(
            VendorAssuranceSelect,
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new VendorAssuranceInputs(assets, assurances);
    }

    public async Task<ComplianceCounts> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCountsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ComplianceCounts> ReadCountsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var counts = await connection.QuerySingleAsync<(int Standards, int Controls, int Requirements, int Organisations, int Scopes, int Vendors, int Collectors)>(new CommandDefinition(
            "SELECT "
            + "(SELECT COUNT(*) FROM standards) AS Standards, "
            + "(SELECT COUNT(*) FROM controls) AS Controls, "
            + "(SELECT COUNT(*) FROM requirements) AS Requirements, "
            + "(SELECT COUNT(*) FROM assets WHERE type IN ('Company', 'Department')) AS Organisations, "
            + "(SELECT COUNT(*) FROM scopes) AS Scopes, "
            + "(SELECT COUNT(*) FROM assets WHERE type = 'Vendor') AS Vendors, "
            + "(SELECT COUNT(*) FROM collectors) AS Collectors;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new ComplianceCounts(
            counts.Standards, counts.Controls, counts.Requirements, counts.Organisations, counts.Scopes,
            counts.Vendors, counts.Collectors);
    }
}
