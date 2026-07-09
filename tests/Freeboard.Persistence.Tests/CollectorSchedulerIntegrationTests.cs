using Dapper;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
using Freeboard.Persistence.System;
using Freeboard.TestInfrastructure;
using MySqlConnector;

namespace Freeboard.Persistence.Tests;

/// <summary>
/// Integration tests for migration 016 and <see cref="MySqlCollectorSchedulerStore"/> against a real
/// MySQL discovered via FREEBOARD_TEST_DB. Each test SKIPS cleanly when the env var is absent. Exercises
/// per-collector leasing (SKIP LOCKED), crash reclaim, lease-token fencing, scheduling advance, bounded
/// backoff, the dead-letter boundary, and config-fingerprint revival.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class CollectorSchedulerIntegrationTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(90);

    private static async Task<MySqlTestDatabase> RequireDbAsync()
    {
        var db = await MySqlTestDatabase.TryCreateAsync();
        Skip.If(db is null, $"{MySqlTestDatabase.EnvVar} not set; skipping MySQL integration test.");
        return db!;
    }

    private static async Task MigrateAsync(MySqlTestDatabase db) =>
        await new MySqlMigrationRunner(db.ConnectionFactory, typeof(IMigrationRunner).Assembly).ApplyPendingAsync();

    private static MySqlCollectorSchedulerStore Store(MySqlTestDatabase db) =>
        new(db.ConnectionFactory, new UlidFactory());

    private sealed record SchedRow(
        string Status,
        int FailureCount,
        DateTime NextDueAt,
        string? CurrentRunId,
        string? LeaseToken,
        string? LeaseOwner,
        DateTime? LeaseExpiresAt,
        DateTime? LastCompletedAt,
        DateTime? LastFailureAt);

    private static async Task<SchedRow> ReadAsync(MySqlConnection conn, string collectorId) =>
        await conn.QuerySingleAsync<SchedRow>(
            "SELECT status AS Status, failure_count AS FailureCount, next_due_at AS NextDueAt, "
            + "current_run_id AS CurrentRunId, lease_token AS LeaseToken, lease_owner AS LeaseOwner, "
            + "lease_expires_at AS LeaseExpiresAt, last_completed_at AS LastCompletedAt, "
            + "last_failure_at AS LastFailureAt "
            + "FROM collector_scheduler_state WHERE collector_id = @Id;",
            new { Id = collectorId });

    private static async Task MakeDueAsync(MySqlConnection conn, string collectorId, int secondsPast = 5) =>
        await conn.ExecuteAsync(
            "UPDATE collector_scheduler_state SET next_due_at = UTC_TIMESTAMP(6) - INTERVAL @Sec SECOND "
            + "WHERE collector_id = @Id;",
            new { Sec = secondsPast, Id = collectorId });

    // xUnit has no TimeSpan tolerance overload; the DB-clock gaps are exact bar sub-second rounding.
    private static void AssertGap(TimeSpan expected, TimeSpan actual) =>
        Assert.True(
            Math.Abs((expected - actual).TotalSeconds) < 1,
            $"expected gap ~{expected} but was {actual}");

    private static async Task ExpireLeaseAsync(MySqlConnection conn, string collectorId) =>
        await conn.ExecuteAsync(
            "UPDATE collector_scheduler_state SET lease_expires_at = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND "
            + "WHERE collector_id = @Id;",
            new { Id = collectorId });

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task MigrationCreatesTableAndIsReRunnable()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        // A second run must be a clean no-op (CREATE TABLE IF NOT EXISTS, versions already recorded).
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var tables = (await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE();"))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("collector_scheduler_state", tables);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ClaimOnUnmigratedDatabaseThrowsNoSuchTable()
    {
        // The scheduler's degradation path filters on exactly this error code, so pin it: a claim before
        // migration 016 is applied surfaces MySqlException with ErrorCode NoSuchTable (1146).
        await using var db = await RequireDbAsync();
        var store = Store(db);

        var ex = await Assert.ThrowsAsync<MySqlException>(
            () => store.ClaimDueAsync("owner", Ttl, batchSize: 1, ["col-1"]));
        Assert.Equal(MySqlErrorCode.NoSuchTable, ex.ErrorCode);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task TwoOwnersClaimDisjointDueRows()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var store = Store(db);

        await store.EnsureScheduledAsync([new("col-a", "fp"), new("col-b", "fp")]);
        var active = new[] { "col-a", "col-b" };

        var first = await store.ClaimDueAsync("owner-A", Ttl, batchSize: 1, active);
        var second = await store.ClaimDueAsync("owner-B", Ttl, batchSize: 1, active);

        Assert.Single(first);
        Assert.Single(second);
        // A leased row is not re-claimed: the two owners hold disjoint collectors covering both rows.
        Assert.NotEqual(first[0].CollectorId, second[0].CollectorId);
        Assert.Equal(new HashSet<string>(active), new HashSet<string> { first[0].CollectorId, second[0].CollectorId });

        // A third claim finds nothing left due and unleased.
        var third = await store.ClaimDueAsync("owner-C", Ttl, batchSize: 10, active);
        Assert.Empty(third);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ExpiredLeaseIsReclaimedPreservingRunId()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var store = Store(db);

        await store.EnsureScheduledAsync([new("col-1", "fp")]);
        var active = new[] { "col-1" };

        var firstClaim = await store.ClaimDueAsync("owner-A", Ttl, batchSize: 1, active);
        Assert.Single(firstClaim);
        var runId = firstClaim[0].CurrentRunId;

        // Simulate a crashed worker: its lease expires without completing.
        await ExpireLeaseAsync(conn, "col-1");

        var reclaim = await store.ClaimDueAsync("owner-B", Ttl, batchSize: 1, active);
        Assert.Single(reclaim);
        // The stable run token survives the reclaim so the retry is idempotent; the lease token is fresh.
        Assert.Equal(runId, reclaim[0].CurrentRunId);
        Assert.NotEqual(firstClaim[0].LeaseToken, reclaim[0].LeaseToken);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task StaleLeaseTokenCannotCompleteOrFail()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var store = Store(db);

        await store.EnsureScheduledAsync([new("col-1", "fp")]);
        var claim = (await store.ClaimDueAsync("owner-A", Ttl, batchSize: 1, ["col-1"]))[0];

        Assert.False(await store.CompleteSuccessAsync("col-1", "wrong-token", Interval));
        Assert.Equal(
            CollectorFailureOutcome.LeaseLost,
            await store.CompleteFailureAsync("col-1", "wrong-token", "boom", Interval, BaseBackoff, maxAttempts: 5));
        // The row is untouched by the stale-token writes: still running under the real lease.
        var row = await ReadAsync(conn, "col-1");
        Assert.Equal("running", row.Status);
        Assert.Equal(claim.LeaseToken, row.LeaseToken);

        // The current token completes.
        Assert.True(await store.CompleteSuccessAsync("col-1", claim.LeaseToken, Interval));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task RenewLeaseFencesOnLeaseToken()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var store = Store(db);

        await store.EnsureScheduledAsync([new("col-1", "fp")]);
        var claim = (await store.ClaimDueAsync("owner-A", Ttl, batchSize: 1, ["col-1"]))[0];
        var before = (await ReadAsync(conn, "col-1")).LeaseExpiresAt;

        Assert.False(await store.RenewLeaseAsync("col-1", "wrong-token", TimeSpan.FromSeconds(120)));
        Assert.True(await store.RenewLeaseAsync("col-1", claim.LeaseToken, TimeSpan.FromSeconds(120)));

        var after = (await ReadAsync(conn, "col-1")).LeaseExpiresAt;
        Assert.True(after > before, "renewal must push lease_expires_at further out");
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SuccessAdvancesNextDueByOneInterval()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var store = Store(db);

        await store.EnsureScheduledAsync([new("col-1", "fp")]);
        var claim = (await store.ClaimDueAsync("owner-A", Ttl, batchSize: 1, ["col-1"]))[0];
        Assert.True(await store.CompleteSuccessAsync("col-1", claim.LeaseToken, Interval));

        var row = await ReadAsync(conn, "col-1");
        Assert.Equal("ok", row.Status);
        Assert.Equal(0, row.FailureCount);
        Assert.Null(row.CurrentRunId);
        Assert.Null(row.LeaseToken);
        // next_due and last_completed are both UTC_TIMESTAMP(6) in the same statement, so their gap is
        // exactly the interval.
        Assert.NotNull(row.LastCompletedAt);
        AssertGap(Interval, row.NextDueAt - row.LastCompletedAt!.Value);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task OverdueByManyIntervalsRunsOneCatchUp()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var store = Store(db);

        await store.EnsureScheduledAsync([new("col-1", "fp")]);
        // Ten days overdue for a one-hour cadence: a single catch-up run, then one interval out.
        await MakeDueAsync(conn, "col-1", secondsPast: 10 * 24 * 3600);

        var claim = (await store.ClaimDueAsync("owner-A", Ttl, batchSize: 1, ["col-1"]))[0];
        Assert.True(await store.CompleteSuccessAsync("col-1", claim.LeaseToken, Interval));

        var dbNow = await conn.ExecuteScalarAsync<DateTime>("SELECT UTC_TIMESTAMP(6);");
        var row = await ReadAsync(conn, "col-1");
        // next_due is one interval from completion (future), NOT still far in the past, so the row does not
        // re-fire once per missed window.
        Assert.True(row.NextDueAt > dbNow, "next_due must be scheduled forward, not left overdue");
        AssertGap(Interval, row.NextDueAt - row.LastCompletedAt!.Value);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task FailureAppliesBoundedBackoffAndIncrementsCount()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var store = Store(db);

        await store.EnsureScheduledAsync([new("col-1", "fp")]);
        var claim = (await store.ClaimDueAsync("owner-A", Ttl, batchSize: 1, ["col-1"]))[0];
        Assert.Equal(
            CollectorFailureOutcome.Retrying,
            await store.CompleteFailureAsync("col-1", claim.LeaseToken, "boom", Interval, BaseBackoff, maxAttempts: 5));

        var row = await ReadAsync(conn, "col-1");
        Assert.Equal("error", row.Status);
        Assert.Equal(1, row.FailureCount);
        Assert.Null(row.LeaseToken);
        // The run token is retained for the retry.
        Assert.NotNull(row.CurrentRunId);
        // First failure backs off BaseBackoff * 2^0 = 60s from the failure time (proves the pre-increment
        // exponent). Bounded by the interval, which is larger here.
        AssertGap(BaseBackoff, row.NextDueAt - row.LastFailureAt!.Value);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task DeadLetterBoundaryIsExactWithMaxAttemptsTwo()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var store = Store(db);

        await store.EnsureScheduledAsync([new("col-1", "fp")]);

        // First failure: failure_count 0 -> 1, still 'error', backed off 2^0 (proves failure_count = +1 is
        // the LAST assignment so status/backoff read the pre-increment count).
        var claim1 = (await store.ClaimDueAsync("owner-A", Ttl, batchSize: 1, ["col-1"]))[0];
        Assert.Equal(
            CollectorFailureOutcome.Retrying,
            await store.CompleteFailureAsync("col-1", claim1.LeaseToken, "boom", Interval, BaseBackoff, maxAttempts: 2));
        var afterFirst = await ReadAsync(conn, "col-1");
        Assert.Equal("error", afterFirst.Status);
        Assert.Equal(1, afterFirst.FailureCount);
        AssertGap(BaseBackoff, afterFirst.NextDueAt - afterFirst.LastFailureAt!.Value);

        // Second failure: failure_count 1 -> 2 >= maxAttempts, so 'dead' and no longer claimed.
        await MakeDueAsync(conn, "col-1");
        var claim2 = (await store.ClaimDueAsync("owner-A", Ttl, batchSize: 1, ["col-1"]))[0];
        Assert.Equal(
            CollectorFailureOutcome.Dead,
            await store.CompleteFailureAsync("col-1", claim2.LeaseToken, "boom", Interval, BaseBackoff, maxAttempts: 2));
        var afterSecond = await ReadAsync(conn, "col-1");
        Assert.Equal("dead", afterSecond.Status);
        Assert.Equal(2, afterSecond.FailureCount);

        // A dead row is not claimed again even when due.
        await MakeDueAsync(conn, "col-1");
        Assert.Empty(await store.ClaimDueAsync("owner-A", Ttl, batchSize: 1, ["col-1"]));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ConfigFingerprintChangeRevivesDeadRow()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var store = Store(db);

        await store.EnsureScheduledAsync([new("col-1", "fp1")]);
        // Drive the row to dead with maxAttempts=1 (first failure is terminal).
        var claim = (await store.ClaimDueAsync("owner-A", Ttl, batchSize: 1, ["col-1"]))[0];
        Assert.Equal(
            CollectorFailureOutcome.Dead,
            await store.CompleteFailureAsync("col-1", claim.LeaseToken, "boom", Interval, BaseBackoff, maxAttempts: 1));
        var dead = await ReadAsync(conn, "col-1");
        Assert.Equal("dead", dead.Status);
        Assert.NotNull(dead.CurrentRunId);

        // Re-ensuring with the SAME fingerprint must NOT revive it (the gate reads the pre-update
        // fingerprint; an equal fingerprint fails the `<>` gate).
        await store.EnsureScheduledAsync([new("col-1", "fp1")]);
        Assert.Equal("dead", (await ReadAsync(conn, "col-1")).Status);

        // A changed fingerprint revives: pending, failure_count reset, immediately due, and the stale run id
        // from the failed run cleared. That revival can fire at all proves config_fingerprint is assigned
        // LAST (assigned first, the gate would compare the row to itself and never revive).
        await store.EnsureScheduledAsync([new("col-1", "fp2")]);
        var revived = await ReadAsync(conn, "col-1");
        Assert.Equal("pending", revived.Status);
        Assert.Equal(0, revived.FailureCount);
        Assert.Null(revived.CurrentRunId);

        // The revived claim mints a fresh run id rather than COALESCE-ing onto the failed run's token.
        var reclaim = await store.ClaimDueAsync("owner-A", Ttl, batchSize: 1, ["col-1"]);
        Assert.Single(reclaim);
        Assert.NotEqual(dead.CurrentRunId, reclaim[0].CurrentRunId);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ClaimExcludesCollectorsAbsentFromActiveSet()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var store = Store(db);

        await store.EnsureScheduledAsync([new("col-a", "fp"), new("col-b", "fp")]);

        // Only col-a is active (col-b was deleted from config or changed away from integration).
        var claimed = await store.ClaimDueAsync("owner-A", Ttl, batchSize: 10, ["col-a"]);
        Assert.Single(claimed);
        Assert.Equal("col-a", claimed[0].CollectorId);

        // An empty active set claims nothing at all.
        Assert.Empty(await store.ClaimDueAsync("owner-A", Ttl, batchSize: 10, []));
    }
}
