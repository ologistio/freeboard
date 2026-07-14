using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using Freeboard.Core.GitOps;

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

    public async Task<IReadOnlyList<EvidenceCollectorRow>> GetEvidenceCollectorsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(string Id, string Title, string Control, string? Vendor, string Type, string Frequency, int? Threshold, string? Config, string? Connection)>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, control_id AS Control, vendor_id AS Vendor, type AS Type, "
            + "frequency AS Frequency, threshold AS Threshold, config AS Config, connection_id AS Connection "
            + "FROM evidence_collectors ORDER BY id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows
            .Select(r => new EvidenceCollectorRow(
                r.Id, r.Title, r.Control, r.Vendor, r.Type, r.Frequency, r.Threshold, DeserializeConfig(r.Config), r.Connection))
            .ToList();
    }

    public async Task<IReadOnlyList<IntegrationConnectionRow>> GetIntegrationConnectionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<IntegrationConnectionRow>(new CommandDefinition(
            "SELECT id AS Id, provider AS Provider, base_url AS BaseUrl, discovery_cadence AS DiscoveryCadence, "
            + "vendor_id AS Vendor FROM integration_connections ORDER BY id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <summary>Deserializes the stored config JSON to a string map; empty when the column is NULL.</summary>
    private static IReadOnlyDictionary<string, string> DeserializeConfig(string? json) =>
        string.IsNullOrEmpty(json)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(StringComparer.Ordinal);

    public async Task<IReadOnlyList<AttestationTemplateRow>> GetAttestationTemplatesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<(string Id, string Title, string Control, string Type, string? Body, string? Fields, int? PassMark, string? Quiz)>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, control_id AS Control, type AS Type, body AS Body, "
            + "fields AS Fields, pass_mark AS PassMark, quiz AS Quiz "
            + "FROM attestation_templates ORDER BY id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows
            .Select(r => new AttestationTemplateRow(
                r.Id, r.Title, r.Control, r.Type, r.Body,
                DeserializeFields(r.Fields), r.PassMark, DeserializeQuiz(r.Quiz)))
            .ToList();
    }

    /// <summary>Deserializes the stored fields JSON to the typed list; empty when the column is NULL.</summary>
    private static IReadOnlyList<AttestationField> DeserializeFields(string? json) =>
        string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize<List<AttestationField>>(json) ?? [];

    /// <summary>
    /// Deserializes the stored quiz JSON and projects each item to an answer-free <see cref="QuizItemView"/>.
    /// The stored quiz carries the correct answer for the grading runtime; dropping it here is what keeps
    /// the answer off every read surface. Empty when the column is NULL.
    /// </summary>
    private static IReadOnlyList<QuizItemView> DeserializeQuiz(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        var items = JsonSerializer.Deserialize<List<QuizItem>>(json) ?? [];
        return items.Select(q => new QuizItemView(q.Id, q.Prompt, q.Options)).ToList();
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

    public async Task<SoaDrilldownInputs> GetStatementOfApplicabilityDrilldownInputsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // One consistent snapshot so the drill-down inputs cannot straddle a concurrent gitops sync
        // commit and pair, say, old controls with new collectors.
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

        var collectorRows = (await connection.QueryAsync<(string Id, string Title, string Control, string? Vendor, string Type, string Frequency, int? Threshold, string? Config)>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, control_id AS Control, vendor_id AS Vendor, type AS Type, "
            + "frequency AS Frequency, threshold AS Threshold, config AS Config "
            + "FROM evidence_collectors ORDER BY id;",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var collectors = collectorRows
            .Select(r => new EvidenceCollectorRow(
                r.Id, r.Title, r.Control, r.Vendor, r.Type, r.Frequency, r.Threshold, DeserializeConfig(r.Config)))
            .ToList();

        var templateRows = (await connection.QueryAsync<(string Id, string Title, string Control, string Type, string? Body, string? Fields, int? PassMark, string? Quiz)>(new CommandDefinition(
            "SELECT id AS Id, title AS Title, control_id AS Control, type AS Type, body AS Body, "
            + "fields AS Fields, pass_mark AS PassMark, quiz AS Quiz "
            + "FROM attestation_templates ORDER BY id;",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var templates = templateRows
            .Select(r => new AttestationTemplateRow(
                r.Id, r.Title, r.Control, r.Type, r.Body,
                DeserializeFields(r.Fields), r.PassMark, DeserializeQuiz(r.Quiz)))
            .ToList();

        var vendors = (await connection.QueryAsync<VendorRow>(new CommandDefinition(
            "SELECT id AS Id, title AS Title FROM vendors ORDER BY id;",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new SoaDrilldownInputs(organisations, scopes, requirements, requirementScopes, controls, collectors, templates, vendors);
    }

    public async Task<ComplianceCounts> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCountsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ComplianceCounts> ReadCountsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var counts = await connection.QuerySingleAsync<(int Standards, int Controls, int Requirements, int Organisations, int Scopes, int RequirementScopes, int Vendors, int VendorScopes, int EvidenceCollectors, int AttestationTemplates)>(new CommandDefinition(
            "SELECT "
            + "(SELECT COUNT(*) FROM standards) AS Standards, "
            + "(SELECT COUNT(*) FROM controls) AS Controls, "
            + "(SELECT COUNT(*) FROM requirements) AS Requirements, "
            + "(SELECT COUNT(*) FROM organisations) AS Organisations, "
            + "(SELECT COUNT(*) FROM scopes) AS Scopes, "
            + "(SELECT COUNT(*) FROM requirement_scopes) AS RequirementScopes, "
            + "(SELECT COUNT(*) FROM vendors) AS Vendors, "
            + "(SELECT COUNT(*) FROM vendor_scopes) AS VendorScopes, "
            + "(SELECT COUNT(*) FROM evidence_collectors) AS EvidenceCollectors, "
            + "(SELECT COUNT(*) FROM attestation_templates) AS AttestationTemplates;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new ComplianceCounts(
            counts.Standards, counts.Controls, counts.Requirements, counts.Organisations, counts.Scopes,
            counts.RequirementScopes, counts.Vendors, counts.VendorScopes, counts.EvidenceCollectors,
            counts.AttestationTemplates);
    }
}
