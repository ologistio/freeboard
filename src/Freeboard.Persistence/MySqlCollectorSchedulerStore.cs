using Dapper;
using Freeboard.Persistence.Auth;

namespace Freeboard.Persistence;

/// <summary>
/// MySQL-backed <see cref="ICollectorSchedulerStore"/> using hand-written SQL via Dapper. Leasing is
/// per-collector via <c>SELECT ... FOR UPDATE SKIP LOCKED</c>; all time is the database clock
/// (<c>UTC_TIMESTAMP(6)</c>); post-claim writes are fenced on <c>lease_token</c>. Run and lease tokens are
/// ULIDs minted through <see cref="IUlidFactory"/>.
/// </summary>
public sealed class MySqlCollectorSchedulerStore(IDbConnectionFactory connectionFactory, IUlidFactory ulidFactory)
    : ICollectorSchedulerStore
{
    // The ensure upsert. The SET list is evaluated left-to-right and each assignment sees columns already
    // updated to its left, so every revived column and the fingerprint gate on the PRE-update status and
    // config_fingerprint: failure_count/last_error/next_due come first, status next, and
    // config_fingerprint LAST. Assigning config_fingerprint first would make the
    // `config_fingerprint <> new.config_fingerprint` gate compare the row to itself and revival could
    // never fire. Refreshing the fingerprint on any changed non-running row (not just revived ones)
    // stops a stale-fingerprint live row being wrongly re-revived every cycle.
    // current_run_id is cleared on revival too: a config change starts a semantically new run, so the claim
    // must mint a fresh run id rather than COALESCE onto the failed run's stale token.
    // The row alias `new` gets distinct column aliases (new_*) so the incoming values never share a name
    // with the table columns. That keeps every unqualified name in the ODKU clause an unambiguous read of
    // the PRE-update table column (an unaliased `new` would collide on status/config_fingerprint/... and
    // make those reads ambiguous). new_config_fingerprint is the incoming fingerprint.
    private const string EnsureSql =
        "INSERT INTO collector_scheduler_state "
        + "(collector_id, next_due_at, status, failure_count, config_fingerprint) "
        + "VALUES (@CollectorId, UTC_TIMESTAMP(6), 'pending', 0, @ConfigFingerprint) "
        + "AS new(new_collector_id, new_next_due_at, new_status, new_failure_count, new_config_fingerprint) "
        + "ON DUPLICATE KEY UPDATE "
        + "failure_count = IF(status <> 'running' "
        + "                    AND config_fingerprint <> new_config_fingerprint "
        + "                    AND status IN ('dead','error'), 0, failure_count), "
        + "last_error = IF(status <> 'running' "
        + "                 AND config_fingerprint <> new_config_fingerprint "
        + "                 AND status IN ('dead','error'), NULL, last_error), "
        + "current_run_id = IF(status <> 'running' "
        + "                     AND config_fingerprint <> new_config_fingerprint "
        + "                     AND status IN ('dead','error'), NULL, current_run_id), "
        + "next_due_at = IF(status <> 'running' "
        + "                  AND config_fingerprint <> new_config_fingerprint "
        + "                  AND status = 'dead', UTC_TIMESTAMP(6), next_due_at), "
        + "status = IF(status <> 'running' "
        + "            AND config_fingerprint <> new_config_fingerprint "
        + "            AND status IN ('dead','error'), 'pending', status), "
        + "config_fingerprint = IF(status <> 'running', new_config_fingerprint, config_fingerprint);";

    public async Task EnsureScheduledAsync(
        IReadOnlyCollection<ScheduledCollectorItem> items, CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return;
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            EnsureSql,
            items.Select(i => new { i.CollectorId, i.ConfigFingerprint }),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ClaimedCollectorLease>> ClaimDueAsync(
        string owner, TimeSpan ttl, int batchSize, IReadOnlyCollection<string> activeCollectorIds,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0 || activeCollectorIds.Count == 0)
        {
            return [];
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var dueIds = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT collector_id FROM collector_scheduler_state "
            + "WHERE next_due_at <= UTC_TIMESTAMP(6) "
            + "  AND status <> 'dead' "
            + "  AND collector_id IN @ActiveCollectorIds "
            + "  AND (lease_owner IS NULL OR lease_expires_at <= UTC_TIMESTAMP(6)) "
            + "ORDER BY next_due_at LIMIT @BatchSize FOR UPDATE SKIP LOCKED;",
            new { ActiveCollectorIds = activeCollectorIds, BatchSize = batchSize },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var claimed = new List<ClaimedCollectorLease>(dueIds.Count);
        foreach (var id in dueIds)
        {
            var newToken = ulidFactory.NewId();
            var newRunId = ulidFactory.NewId();

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE collector_scheduler_state "
                + "SET lease_owner = @Owner, "
                + "    lease_token = @NewToken, "
                + "    lease_expires_at = UTC_TIMESTAMP(6) + INTERVAL @TtlSeconds SECOND, "
                + "    lease_heartbeat_at = UTC_TIMESTAMP(6), "
                + "    last_started_at = UTC_TIMESTAMP(6), "
                + "    status = 'running', "
                + "    current_run_id = COALESCE(current_run_id, @NewRunId) "
                + "WHERE collector_id = @Id;",
                new { Owner = owner, NewToken = newToken, TtlSeconds = Seconds(ttl), NewRunId = newRunId, Id = id },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            // Read the effective row inside the transaction so the caller gets the COALESCE-resolved run id
            // and the minted lease token; neither is reconstructable outside this claim.
            var lease = await connection.QuerySingleAsync<ClaimedCollectorLease>(new CommandDefinition(
                "SELECT collector_id AS CollectorId, lease_token AS LeaseToken, "
                + "current_run_id AS CurrentRunId, lease_expires_at AS LeaseExpiresAt "
                + "FROM collector_scheduler_state WHERE collector_id = @Id;",
                new { Id = id },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            claimed.Add(lease);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    public async Task<bool> RenewLeaseAsync(
        string collectorId, string leaseToken, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE collector_scheduler_state "
            + "SET lease_heartbeat_at = UTC_TIMESTAMP(6), "
            + "    lease_expires_at = UTC_TIMESTAMP(6) + INTERVAL @TtlSeconds SECOND "
            + "WHERE collector_id = @Id AND lease_token = @Token;",
            new { TtlSeconds = Seconds(ttl), Id = collectorId, Token = leaseToken },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> ReleaseLeaseAsync(
        string collectorId, string leaseToken, string status, DateTime? nextDueAt = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE collector_scheduler_state "
            + "SET lease_owner = NULL, lease_token = NULL, "
            + "    lease_expires_at = NULL, lease_heartbeat_at = NULL, "
            + "    status = @Status, "
            + "    next_due_at = COALESCE(@NextDueAt, next_due_at) "
            + "WHERE collector_id = @Id AND lease_token = @Token;",
            new { Status = status, NextDueAt = nextDueAt, Id = collectorId, Token = leaseToken },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> CompleteSuccessAsync(
        string collectorId, string leaseToken, TimeSpan interval, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // next_due_at derives from UTC_TIMESTAMP(6) directly, not the co-assigned last_completed_at column:
        // a single-table SET list is evaluated left-to-right, so reading a co-assigned column would depend
        // on assignment order.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE collector_scheduler_state "
            + "SET next_due_at = UTC_TIMESTAMP(6) + INTERVAL @IntervalSeconds SECOND, "
            + "    last_completed_at = UTC_TIMESTAMP(6), "
            + "    last_success_at = UTC_TIMESTAMP(6), "
            + "    status = 'ok', "
            + "    failure_count = 0, "
            + "    last_error = NULL, "
            + "    current_run_id = NULL, "
            + "    lease_owner = NULL, lease_token = NULL, "
            + "    lease_expires_at = NULL, lease_heartbeat_at = NULL "
            + "WHERE collector_id = @Id AND lease_token = @Token;",
            new { IntervalSeconds = Seconds(interval), Id = collectorId, Token = leaseToken },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<CollectorFailureOutcome> CompleteFailureAsync(
        string collectorId, string leaseToken, string error, TimeSpan interval, TimeSpan baseBackoff,
        int maxAttempts, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // failure_count = failure_count + 1 is LAST: a single-table SET list is evaluated left-to-right, so
        // status, next_due_at, and POW(2, failure_count) above it read the pre-increment value. Thus
        // `failure_count + 1 >= maxAttempts` tests the incremented count and the first failure backs off
        // baseBackoff * 2^0.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE collector_scheduler_state "
            + "SET last_failure_at = UTC_TIMESTAMP(6), "
            + "    last_error = @Error, "
            + "    current_run_id = current_run_id, "
            + "    lease_owner = NULL, lease_token = NULL, "
            + "    lease_expires_at = NULL, lease_heartbeat_at = NULL, "
            + "    status = IF(failure_count + 1 >= @MaxAttempts, 'dead', 'error'), "
            + "    next_due_at = IF(failure_count + 1 >= @MaxAttempts, next_due_at, "
            + "                     UTC_TIMESTAMP(6) + INTERVAL "
            + "                       LEAST(@IntervalSeconds, @BaseBackoffSeconds * POW(2, failure_count)) SECOND), "
            + "    failure_count = failure_count + 1 "
            + "WHERE collector_id = @Id AND lease_token = @Token;",
            new
            {
                Error = error,
                MaxAttempts = maxAttempts,
                IntervalSeconds = Seconds(interval),
                BaseBackoffSeconds = Seconds(baseBackoff),
                Id = collectorId,
                Token = leaseToken,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (affected == 0)
        {
            return CollectorFailureOutcome.LeaseLost;
        }

        // Read the resulting status on the same connection so the caller can log the terminal dead
        // transition. The row was just fenced-updated by us; a dead/error row is not concurrently claimable
        // (dead is excluded, error's next_due is in the backoff future), so this read is stable.
        var status = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT status FROM collector_scheduler_state WHERE collector_id = @Id;",
            new { Id = collectorId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return status == "dead" ? CollectorFailureOutcome.Dead : CollectorFailureOutcome.Retrying;
    }

    // Whole seconds for INTERVAL ... SECOND. The scheduling cadences are all whole-second windows, so no
    // sub-second precision is lost.
    private static long Seconds(TimeSpan span) => (long)span.TotalSeconds;
}
