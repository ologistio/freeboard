using Dapper;

namespace Freeboard.Persistence;

/// <summary>
/// MySQL-backed <see cref="IAssetStore"/> with hand-written Dapper reads over the unified <c>assets</c>
/// table. Every read is scoped to discovered machines (<c>source = 'discovered'</c>) in the caller's
/// organisation via the <c>parent</c> column, so a lookup for the wrong organisation returns nothing.
/// </summary>
public sealed class MySqlAssetStore(IDbConnectionFactory connectionFactory) : IAssetStore
{
    private const string AssetColumns =
        "a.id AS Id, a.type AS Type, a.source AS Source, a.parent AS Parent, a.owner AS Owner, "
        + "a.identity_kind AS IdentityKind, a.identity_value AS IdentityValue, a.hostname AS Hostname, "
        + "a.state AS State, a.first_seen_at AS FirstSeenAt, a.last_seen_at AS LastSeenAt, "
        + "a.retired_at AS RetiredAt, a.created_at AS CreatedAt";

    public async Task<AssetRow?> GetByIdAsync(
        string organisationId, string assetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<AssetRow>(new CommandDefinition(
            $"SELECT {AssetColumns} FROM assets a "
            + "WHERE a.source = 'discovered' AND a.parent = @OrganisationId AND a.id = @AssetId;",
            new { OrganisationId = organisationId, AssetId = assetId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<AssetRow?> GetByIdentityAsync(
        string organisationId, string identityKind, string identityValue, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<AssetRow>(new CommandDefinition(
            $"SELECT {AssetColumns} FROM assets a "
            + "WHERE a.source = 'discovered' AND a.parent = @OrganisationId AND a.identity_kind = @IdentityKind "
            + "AND a.identity_value = @IdentityValue;",
            new { OrganisationId = organisationId, IdentityKind = identityKind, IdentityValue = identityValue },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<AssetRow?> GetBySourceAsync(
        string organisationId, string source, string externalId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // Keep the org predicate against a.parent (the machine's org): the composite asset_source FK that
        // used to enforce this is relaxed to a simple asset_id FK, so a bad or cross-org asset_source row
        // must not be able to surface another org's machine through this join.
        return await connection.QuerySingleOrDefaultAsync<AssetRow>(new CommandDefinition(
            $"SELECT {AssetColumns} FROM assets a "
            + "JOIN asset_source s ON s.asset_id = a.id AND s.organisation_id = a.parent "
            + "WHERE a.source = 'discovered' AND s.organisation_id = @OrganisationId "
            + "AND s.source = @Source AND s.external_id = @ExternalId;",
            new { OrganisationId = organisationId, Source = source, ExternalId = externalId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
