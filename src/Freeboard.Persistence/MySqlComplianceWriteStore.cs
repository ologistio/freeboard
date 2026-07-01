using System.Data.Common;
using Dapper;
using Freeboard.Core.GitOps;

namespace Freeboard.Persistence;

/// <summary>
/// MySQL-backed <see cref="IComplianceWriteStore"/>. Each write runs in one transaction and
/// checks the domain invariants against the current rows before committing; a violated
/// invariant rolls back so the store is never left in a half-written state. The database keys
/// (self-FK, the unique <c>(organisation_id, standard_id)</c> key) are the backstop, but the
/// checks here return a clear error instead of a raw driver exception.
/// </summary>
public sealed class MySqlComplianceWriteStore(IDbConnectionFactory connectionFactory) : IComplianceWriteStore
{
    public async Task<WriteResult> UpsertOrganisationAsync(
        string id,
        string title,
        string kind,
        string? parent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return WriteResult.Fail("Organisation id is required.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return WriteResult.Fail("Organisation title is required.");
        }

        if (!ConfigValidator.TryParseKind(kind, out _))
        {
            return WriteResult.Fail($"Organisation kind must be '{nameof(OrganisationKind.Company)}' or '{nameof(OrganisationKind.Department)}'.");
        }

        var parentId = string.IsNullOrEmpty(parent) ? null : parent;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (parentId is not null)
        {
            if (string.Equals(parentId, id, StringComparison.Ordinal))
            {
                return WriteResult.Fail("An organisation cannot be its own parent.");
            }

            var parentExists = await ExistsAsync(connection, transaction, "organisations", parentId, cancellationToken).ConfigureAwait(false);
            if (!parentExists)
            {
                return WriteResult.Fail($"Parent organisation '{parentId}' does not exist.");
            }

            if (await WouldFormCycleAsync(connection, transaction, id, parentId, cancellationToken).ConfigureAwait(false))
            {
                return WriteResult.Fail("Setting this parent would form an organisation cycle.");
            }
        }

        var now = DateTime.UtcNow;
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO organisations (id, api_version, title, kind, parent_id, created_at, updated_at) "
            + "VALUES (@Id, @ApiVersion, @Title, @Kind, @Parent, @Now, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            + "api_version = VALUES(api_version), title = VALUES(title), kind = VALUES(kind), "
            + "parent_id = VALUES(parent_id), updated_at = VALUES(updated_at);",
            new { Id = id, ApiVersion = GitOpsSchema.ApiVersion, Title = title, Kind = kind, Parent = parentId, Now = now },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return WriteResult.Success;
    }

    public async Task<WriteResult> DeleteOrganisationAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var childCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM organisations WHERE parent_id = @Id;",
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (childCount > 0)
        {
            return WriteResult.Fail("Cannot delete an organisation that still has child organisations.");
        }

        var scopeCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM scopes WHERE organisation_id = @Id;",
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (scopeCount > 0)
        {
            return WriteResult.Fail("Cannot delete an organisation that still has scopes.");
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM organisations WHERE id = @Id;",
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return WriteResult.Success;
    }

    public async Task<WriteResult> UpsertScopeDispositionAsync(
        string id,
        string title,
        string organisation,
        string standard,
        string disposition,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return WriteResult.Fail("Scope id is required.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return WriteResult.Fail("Scope title is required.");
        }

        if (!ConfigValidator.TryParseDisposition(disposition, out _))
        {
            return WriteResult.Fail($"Disposition must be '{nameof(ScopeDisposition.In)}' or '{nameof(ScopeDisposition.Out)}'.");
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (!await ExistsAsync(connection, transaction, "organisations", organisation, cancellationToken).ConfigureAwait(false))
        {
            return WriteResult.Fail($"Organisation '{organisation}' does not exist.");
        }

        if (!await ExistsAsync(connection, transaction, "standards", standard, cancellationToken).ConfigureAwait(false))
        {
            return WriteResult.Fail($"Standard '{standard}' does not exist.");
        }

        // At most one scope per (organisation, standard). A row for the pair under a DIFFERENT
        // id is a duplicate mapping; the same id updating its own pair is fine.
        var conflictingId = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT id FROM scopes WHERE organisation_id = @Organisation AND standard_id = @Standard LIMIT 1;",
            new { Organisation = organisation, Standard = standard }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (conflictingId is not null && !string.Equals(conflictingId, id, StringComparison.Ordinal))
        {
            return WriteResult.Fail(
                $"A scope already maps organisation '{organisation}' to standard '{standard}'.");
        }

        var now = DateTime.UtcNow;
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO scopes (id, api_version, title, organisation_id, standard_id, disposition, created_at, updated_at) "
            + "VALUES (@Id, @ApiVersion, @Title, @Organisation, @Standard, @Disposition, @Now, @Now) "
            + "ON DUPLICATE KEY UPDATE "
            + "api_version = VALUES(api_version), title = VALUES(title), organisation_id = VALUES(organisation_id), "
            + "standard_id = VALUES(standard_id), disposition = VALUES(disposition), updated_at = VALUES(updated_at);",
            new
            {
                Id = id,
                ApiVersion = GitOpsSchema.ApiVersion,
                Title = title,
                Organisation = organisation,
                Standard = standard,
                Disposition = disposition,
                Now = now,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return WriteResult.Success;
    }

    public async Task<WriteResult> DeleteScopeAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM scopes WHERE id = @Id;",
            new { Id = id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return WriteResult.Success;
    }

    private static async Task<bool> ExistsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string id,
        CancellationToken cancellationToken)
    {
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM {table} WHERE id = @Id;",
            new { Id = id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return count > 0;
    }

    /// <summary>
    /// True if making <paramref name="parentId"/> the parent of <paramref name="childId"/> would
    /// create a cycle: it does when <paramref name="childId"/> is already an ancestor of
    /// <paramref name="parentId"/> (so the new edge would close a loop).
    /// </summary>
    private static async Task<bool> WouldFormCycleAsync(
        DbConnection connection,
        DbTransaction transaction,
        string childId,
        string parentId,
        CancellationToken cancellationToken)
    {
        var current = (string?)parentId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (current is not null && visited.Add(current))
        {
            if (string.Equals(current, childId, StringComparison.Ordinal))
            {
                return true;
            }

            current = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT parent_id FROM organisations WHERE id = @Id;",
                new { Id = current }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        return false;
    }
}
