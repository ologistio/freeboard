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

        var controls = (await connection.QueryAsync<(string Id, string Title)>(new CommandDefinition(
            "SELECT id AS Id, title AS Title FROM controls ORDER BY id;",
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
                mapsTo.TryGetValue(c.Id, out var ids) ? ids : []))
            .ToList();
    }

    public async Task<IReadOnlyList<OrganisationRow>> GetOrganisationsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<OrganisationRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, kind AS Kind, parent_id AS Parent FROM organisations ORDER BY id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<ScopeRow>> GetScopesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ScopeRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, organisation_id AS Organisation, standard_id AS Standard, "
            + "disposition AS Disposition FROM scopes ORDER BY id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<RequirementScopeRow>> GetRequirementScopesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<RequirementScopeRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, organisation_id AS Organisation, requirement_id AS Requirement, "
            + "disposition AS Disposition FROM requirement_scopes ORDER BY id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<VendorRow>> GetVendorsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<VendorRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title FROM vendors ORDER BY id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<VendorScopeRow>> GetVendorScopesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<VendorScopeRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, vendor_id AS Vendor, requirement_id AS Requirement, "
            + "control_id AS Control, disposition AS Disposition, justification AS Justification "
            + "FROM vendor_scopes ORDER BY id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<SoaInputs> GetStatementOfApplicabilityInputsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // One consistent snapshot so the four SoA inputs cannot straddle a concurrent gitops sync
        // commit and pair, say, old organisations with new requirement-scopes.
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        var organisations = (await connection.QueryAsync<OrganisationRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, kind AS Kind, parent_id AS Parent FROM organisations ORDER BY id;",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var scopes = (await connection.QueryAsync<ScopeRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, organisation_id AS Organisation, standard_id AS Standard, "
            + "disposition AS Disposition FROM scopes ORDER BY id;",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var requirements = (await connection.QueryAsync<RequirementRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, standard_id AS Standard, theme AS Theme, statement AS Statement, "
            + "guidance AS Guidance, citation_label AS CitationLabel, citation_url AS CitationUrl "
            + "FROM requirements ORDER BY id;",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var requirementScopes = (await connection.QueryAsync<RequirementScopeRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, organisation_id AS Organisation, requirement_id AS Requirement, "
            + "disposition AS Disposition FROM requirement_scopes ORDER BY id;",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SoaInputs(organisations, scopes, requirements, requirementScopes);
    }

    public async Task<ComplianceCounts> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCountsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ComplianceCounts> ReadCountsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var counts = await connection.QuerySingleAsync<(int Standards, int Controls, int Requirements, int Organisations, int Scopes, int RequirementScopes, int Vendors, int VendorScopes)>(new CommandDefinition(
            "SELECT "
            + "(SELECT COUNT(*) FROM standards) AS Standards, "
            + "(SELECT COUNT(*) FROM controls) AS Controls, "
            + "(SELECT COUNT(*) FROM requirements) AS Requirements, "
            + "(SELECT COUNT(*) FROM organisations) AS Organisations, "
            + "(SELECT COUNT(*) FROM scopes) AS Scopes, "
            + "(SELECT COUNT(*) FROM requirement_scopes) AS RequirementScopes, "
            + "(SELECT COUNT(*) FROM vendors) AS Vendors, "
            + "(SELECT COUNT(*) FROM vendor_scopes) AS VendorScopes;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new ComplianceCounts(
            counts.Standards, counts.Controls, counts.Requirements, counts.Organisations, counts.Scopes,
            counts.RequirementScopes, counts.Vendors, counts.VendorScopes);
    }
}
