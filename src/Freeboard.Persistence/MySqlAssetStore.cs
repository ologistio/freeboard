using Dapper;

namespace Freeboard.Persistence;

/// <summary>
/// MySQL-backed <see cref="IAssetStore"/> with hand-written Dapper reads. Every query filters by
/// <c>organisation_id</c>, so a lookup for the wrong organisation returns nothing.
/// </summary>
public sealed class MySqlAssetStore(IDbConnectionFactory connectionFactory) : IAssetStore
{
    private const string AssetColumns =
        "a.id AS Id, a.organisation_id AS OrganisationId, a.kind AS Kind, a.identity_kind AS IdentityKind, "
        + "a.identity_value AS IdentityValue, a.hostname AS Hostname, a.state AS State, "
        + "a.first_seen_at AS FirstSeenAt, a.last_seen_at AS LastSeenAt, a.retired_at AS RetiredAt, "
        + "a.created_at AS CreatedAt";

    public async Task<AssetRow?> GetByIdAsync(
        string organisationId, string assetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<AssetRow>(new CommandDefinition(
            $"SELECT {AssetColumns} FROM asset a "
            + "WHERE a.organisation_id = @OrganisationId AND a.id = @AssetId;",
            new { OrganisationId = organisationId, AssetId = assetId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<AssetRow?> GetByIdentityAsync(
        string organisationId, string identityKind, string identityValue, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<AssetRow>(new CommandDefinition(
            $"SELECT {AssetColumns} FROM asset a "
            + "WHERE a.organisation_id = @OrganisationId AND a.identity_kind = @IdentityKind "
            + "AND a.identity_value = @IdentityValue;",
            new { OrganisationId = organisationId, IdentityKind = identityKind, IdentityValue = identityValue },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<AssetRow?> GetBySourceAsync(
        string organisationId, string source, string externalId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<AssetRow>(new CommandDefinition(
            $"SELECT {AssetColumns} FROM asset a "
            + "JOIN asset_source s ON s.asset_id = a.id AND s.organisation_id = a.organisation_id "
            + "WHERE s.organisation_id = @OrganisationId AND s.source = @Source AND s.external_id = @ExternalId;",
            new { OrganisationId = organisationId, Source = source, ExternalId = externalId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
