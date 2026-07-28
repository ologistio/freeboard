using System.Data;
using System.Data.Common;
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

    public async Task<IReadOnlyList<OrganisationRow>> GetOrganisationsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<OrganisationRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, type AS Kind, parent AS Parent FROM assets "
            + "WHERE type IN ('Company', 'Department') ORDER BY id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    // The unified scope read joins the subject asset so each row carries the subject's resolved
    // type/source/state/parent/owner, which the web /scopes endpoint uses for the subject-readability
    // branches and the fail-closed check on an unresolved subject. These five narrowing fields are
    // server-side only and are never serialized: the endpoint projects only the eight public fields.
    private const string ScopeSelect =
        "SELECT s.id AS Id, s.title AS Title, s.subject_id AS Subject, s.standard_id AS Standard, "
        + "s.requirement_id AS Requirement, s.control_id AS Control, s.disposition AS Disposition, "
        + "s.justification AS Justification, a.type AS SubjectType, a.source AS SubjectSource, "
        + "a.state AS SubjectState, a.parent AS SubjectParent, a.owner AS SubjectOwner "
        + "FROM scopes s LEFT JOIN assets a ON a.id = s.subject_id ORDER BY s.id;";

    // The subject-resolution predicate as a set: every asset id that is a live authorization anchor
    // (present and not a retired discovered asset). A subject resolves iff its id is in this set.
    private const string ResolvableAssetIdsSelect =
        "SELECT id FROM assets WHERE NOT (source = 'discovered' AND state = 'Retired');";

    public async Task<IReadOnlyList<ScopeRow>> GetScopesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ScopeRow>(new CommandDefinition(
            ScopeSelect, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<VendorRow>> GetVendorsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<VendorRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, owner AS Owner FROM assets WHERE type = 'Vendor' ORDER BY id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
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
        // One consistent snapshot so the four SoA inputs cannot straddle a concurrent gitops sync
        // commit and pair, say, old organisations with new scopes.
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        var organisations = (await connection.QueryAsync<OrganisationRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, type AS Kind, parent AS Parent FROM assets "
            + "WHERE type IN ('Company', 'Department') ORDER BY id;",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

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

        var resolvableAssetIds = (await connection.QueryAsync<string>(new CommandDefinition(
            ResolvableAssetIdsSelect,
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SoaInputs(organisations, scopes, requirements, resolvableAssetIds);
    }

    public async Task<SoaDrilldownInputs> GetStatementOfApplicabilityDrilldownInputsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // One consistent snapshot so the drill-down inputs cannot straddle a concurrent gitops sync
        // commit and pair, say, old controls with new collectors.
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        var organisations = (await connection.QueryAsync<OrganisationRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, type AS Kind, parent AS Parent FROM assets "
            + "WHERE type IN ('Company', 'Department') ORDER BY id;",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

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

        var resolvableAssetIds = (await connection.QueryAsync<string>(new CommandDefinition(
            ResolvableAssetIdsSelect,
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);

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

        var vendors = (await connection.QueryAsync<VendorRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, owner AS Owner FROM assets WHERE type = 'Vendor' ORDER BY id;",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SoaDrilldownInputs(organisations, scopes, requirements, resolvableAssetIds, controls, collectors, vendors);
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
