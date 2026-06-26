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
            "SELECT id AS Id, title AS Title FROM standards ORDER BY id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<ControlRow>> GetControlsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var controls = (await connection.QueryAsync<(string Id, string Title)>(new CommandDefinition(
            "SELECT id AS Id, title AS Title FROM controls ORDER BY id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var links = await connection.QueryAsync<(string ControlId, string StandardId)>(new CommandDefinition(
            "SELECT control_id AS ControlId, standard_id AS StandardId FROM control_standards ORDER BY control_id, standard_id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var mapsTo = links
            .GroupBy(l => l.ControlId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(l => l.StandardId).ToList(), StringComparer.Ordinal);

        return controls
            .Select(c => new ControlRow(
                c.Id,
                c.Title,
                mapsTo.TryGetValue(c.Id, out var ids) ? ids : []))
            .ToList();
    }

    public async Task<IReadOnlyList<ScopeRow>> GetScopesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var scopes = (await connection.QueryAsync<(string Id, string Title)>(new CommandDefinition(
            "SELECT id AS Id, title AS Title FROM scopes ORDER BY id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var links = await connection.QueryAsync<(string ScopeId, string ControlId)>(new CommandDefinition(
            "SELECT scope_id AS ScopeId, control_id AS ControlId FROM scope_controls ORDER BY scope_id, control_id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var byScope = links
            .GroupBy(l => l.ScopeId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(l => l.ControlId).ToList(), StringComparer.Ordinal);

        return scopes
            .Select(s => new ScopeRow(
                s.Id,
                s.Title,
                byScope.TryGetValue(s.Id, out var ids) ? ids : []))
            .ToList();
    }

    public async Task<ComplianceCounts> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCountsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ComplianceCounts> ReadCountsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var counts = await connection.QuerySingleAsync<(int Standards, int Controls, int Scopes)>(new CommandDefinition(
            "SELECT "
            + "(SELECT COUNT(*) FROM standards) AS Standards, "
            + "(SELECT COUNT(*) FROM controls) AS Controls, "
            + "(SELECT COUNT(*) FROM scopes) AS Scopes;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new ComplianceCounts(counts.Standards, counts.Controls, counts.Scopes);
    }
}
