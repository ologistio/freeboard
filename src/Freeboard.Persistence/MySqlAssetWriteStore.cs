using System.Data;
using System.Data.Common;
using Dapper;
using Freeboard.Core.Assets;
using Freeboard.Persistence.Auth;
using MySqlConnector;

namespace Freeboard.Persistence;

/// <summary>
/// MySQL-backed <see cref="IAssetWriteStore"/>. The upsert reads candidate rows under a lock, decides
/// insert vs update vs conflict, and relies on the org-scoped unique constraints as the race backstop -
/// it does NOT use <c>ON DUPLICATE KEY UPDATE</c>, which cannot refuse a conflicting relink. The
/// transaction opens at <see cref="IsolationLevel.ReadCommitted"/> so a <c>SELECT ... FOR UPDATE</c> on a
/// not-yet-existing identity or source row takes no gap lock; two concurrent first-observations then both
/// reach the insert and the loser fails with a plain duplicate-key error (1062), which the single retry
/// resolves by taking the update path (same pattern as <see cref="MySqlAuthRateLimitStore"/>).
/// </summary>
public sealed class MySqlAssetWriteStore(IDbConnectionFactory connectionFactory, IUlidFactory ulidFactory)
    : IAssetWriteStore
{
    private const string MachineKind = "Machine";
    private const string StateSeen = "Seen";
    private const string StateRetired = "Retired";

    public async Task<AssetUpsertResult> UpsertMachineFromSourceAsync(
        NewMachineObservation observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (string.IsNullOrWhiteSpace(observation.OrganisationId))
        {
            return AssetUpsertResult.Invalid("Asset organisation id is required.");
        }

        if (string.IsNullOrWhiteSpace(observation.Source))
        {
            return AssetUpsertResult.Invalid("Asset source is required.");
        }

        if (string.IsNullOrWhiteSpace(observation.ExternalId))
        {
            return AssetUpsertResult.Invalid("Asset source external id is required.");
        }

        var identity = MachineIdentity.Derive(observation.HardwareSerial, observation.HostUuid);
        if (identity is null)
        {
            return AssetUpsertResult.Invalid("Observation has no usable machine identity (serial or host uuid).");
        }

        // Only relevant when the canonical axis is the serial: the cross-axis guard needs the uuid-keyed
        // asset. A uuid-primary identity already resolves on the uuid axis, so there is nothing to cross.
        var crossAxisUuid = identity.Kind == MachineIdentityKind.Serial
            ? MachineIdentity.NormalizeHostUuid(observation.HostUuid)
            : null;

        // Retry the whole resolve exactly once. Under READ COMMITTED the first-observation race surfaces
        // only as 1062, never as a gap-lock deadlock (1213) or lock-wait-timeout (1205), so those are not
        // swallowed as retryable.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ResolveAndAttachAsync(observation, identity, crossAxisUuid, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry && attempt == 0)
            {
                // A concurrent transaction inserted the same identity or source first. The transaction is
                // rolled back on dispose; resolve again, which now finds the row and updates.
            }
        }
    }

    public async Task<WriteResult> RetireAsync(
        string organisationId, string assetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE asset SET state = @Retired, retired_at = @Now "
            + "WHERE id = @AssetId AND organisation_id = @OrganisationId AND state <> @Retired;",
            new { Retired = StateRetired, Now = DateTime.UtcNow, AssetId = assetId, OrganisationId = organisationId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return WriteResult.Success;
    }

    private async Task<AssetUpsertResult> ResolveAndAttachAsync(
        NewMachineObservation observation, MachineIdentity identity, string? crossAxisUuid,
        CancellationToken cancellationToken)
    {
        var identityKind = identity.Kind.ToString();
        var now = DateTime.UtcNow;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        var existingSourceAssetId = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT asset_id FROM asset_source "
            + "WHERE organisation_id = @Org AND source = @Source AND external_id = @ExternalId FOR UPDATE;",
            new { Org = observation.OrganisationId, observation.Source, observation.ExternalId },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var canonicalAssetId = await LockAssetIdByIdentityAsync(
            connection, transaction, observation.OrganisationId, identityKind, identity.Value, cancellationToken)
            .ConfigureAwait(false);

        if (crossAxisUuid is not null)
        {
            var uuidAssetId = await LockAssetIdByIdentityAsync(
                connection, transaction, observation.OrganisationId,
                MachineIdentityKind.HostUuid.ToString(), crossAxisUuid, cancellationToken).ConfigureAwait(false);
            if (canonicalAssetId is not null && uuidAssetId is not null
                && !string.Equals(canonicalAssetId, uuidAssetId, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return AssetUpsertResult.Conflict(
                    "The observed serial and host uuid resolve to two different assets.");
            }
        }

        string targetAssetId;
        bool created;
        if (canonicalAssetId is not null)
        {
            targetAssetId = canonicalAssetId;
            created = false;
            // Preserve the id (so attached evidence stays resolvable), reactivate, and only overwrite the
            // hostname when a new one was observed.
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE asset SET last_seen_at = @Now, state = @Seen, retired_at = NULL, "
                + "hostname = COALESCE(@Hostname, hostname) "
                + "WHERE id = @Id AND organisation_id = @Org;",
                new
                {
                    Now = now,
                    Seen = StateSeen,
                    observation.Hostname,
                    Id = targetAssetId,
                    Org = observation.OrganisationId,
                },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        else
        {
            targetAssetId = ulidFactory.NewId();
            created = true;
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO asset (id, organisation_id, kind, identity_kind, identity_value, hostname, "
                + "state, first_seen_at, last_seen_at, retired_at, created_at) "
                + "VALUES (@Id, @Org, @Kind, @IdentityKind, @IdentityValue, @Hostname, @Seen, @Now, @Now, NULL, @Now);",
                new
                {
                    Id = targetAssetId,
                    Org = observation.OrganisationId,
                    Kind = MachineKind,
                    IdentityKind = identityKind,
                    IdentityValue = identity.Value,
                    observation.Hostname,
                    Seen = StateSeen,
                    Now = now,
                },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        if (existingSourceAssetId is not null
            && !string.Equals(existingSourceAssetId, targetAssetId, StringComparison.Ordinal))
        {
            // Relinking this source would send its future evidence to the wrong machine. Refuse.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return AssetUpsertResult.Conflict("This source and external id already point at a different asset.");
        }

        if (existingSourceAssetId is not null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE asset_source SET last_seen_at = @Now, observed_serial = @Serial, "
                + "observed_host_uuid = @HostUuid "
                + "WHERE organisation_id = @Org AND source = @Source AND external_id = @ExternalId;",
                new
                {
                    Now = now,
                    Serial = observation.HardwareSerial,
                    HostUuid = observation.HostUuid,
                    Org = observation.OrganisationId,
                    observation.Source,
                    observation.ExternalId,
                },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO asset_source (id, asset_id, organisation_id, source, external_id, "
                + "observed_serial, observed_host_uuid, first_seen_at, last_seen_at, created_at) "
                + "VALUES (@Id, @AssetId, @Org, @Source, @ExternalId, @Serial, @HostUuid, @Now, @Now, @Now);",
                new
                {
                    Id = ulidFactory.NewId(),
                    AssetId = targetAssetId,
                    Org = observation.OrganisationId,
                    observation.Source,
                    observation.ExternalId,
                    Serial = observation.HardwareSerial,
                    HostUuid = observation.HostUuid,
                    Now = now,
                },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return created ? AssetUpsertResult.Created(targetAssetId) : AssetUpsertResult.Updated(targetAssetId);
    }

    private static Task<string?> LockAssetIdByIdentityAsync(
        DbConnection connection, DbTransaction transaction, string organisationId,
        string identityKind, string identityValue, CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT id FROM asset "
            + "WHERE organisation_id = @Org AND identity_kind = @IdentityKind AND identity_value = @IdentityValue "
            + "FOR UPDATE;",
            new { Org = organisationId, IdentityKind = identityKind, IdentityValue = identityValue },
            transaction, cancellationToken: cancellationToken));
}
