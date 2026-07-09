namespace Freeboard.Persistence;

/// <summary>
/// A collector to ensure a scheduler-state row for. <see cref="ConfigFingerprint"/> is a hash over the
/// scheduling-relevant config (frequency + type); a change to it revives a dead/error row.
/// </summary>
public sealed record ScheduledCollectorItem(string CollectorId, string ConfigFingerprint);

/// <summary>
/// The fencing values resolved inside a claim transaction, one per claimed collector.
/// <see cref="LeaseToken"/> is minted fresh on the claim and fences every later write for this attempt;
/// <see cref="CurrentRunId"/> is the stable run token (assigned only when absent via COALESCE) that a
/// crash retry reuses. The caller cannot reconstruct either, so the claim returns them.
/// </summary>
public sealed record ClaimedCollectorLease(
    string CollectorId, string LeaseToken, string CurrentRunId, DateTime LeaseExpiresAt);

/// <summary>Outcome of a fenced failure write, so the caller can log the terminal <c>dead</c> transition.</summary>
public enum CollectorFailureOutcome
{
    /// <summary>The lease token did not match (0 rows); the write did nothing.</summary>
    LeaseLost,

    /// <summary>Recorded as <c>error</c>; the collector retries after backoff.</summary>
    Retrying,

    /// <summary>Reached <c>MaxAttempts</c> and went terminal <c>dead</c>; not claimed again until revived.</summary>
    Dead,
}

/// <summary>
/// Per-collector scheduler-state store backing the in-service collector scheduler. One row per collector
/// holds the schedule, in-flight run, lease, and health fields. All time is the database clock
/// (UTC_TIMESTAMP(6)); every post-claim write is fenced on <c>lease_token</c> so a worker that lost its
/// lease affects 0 rows and cannot overwrite the new holder's state.
/// </summary>
public interface ICollectorSchedulerStore
{
    /// <summary>
    /// Upserts a state row for each collector. A missing row is seeded <c>status='pending'</c>,
    /// <c>failure_count=0</c>, <c>next_due_at = UTC_TIMESTAMP(6)</c> (immediately due). An existing
    /// <c>running</c> row is left untouched; a <c>pending</c>/<c>ok</c> row only refreshes its stored
    /// fingerprint when it differs; an <c>error</c>/<c>dead</c> row with a differing fingerprint is
    /// revived (fingerprint refreshed, <c>status='pending'</c>, <c>failure_count=0</c>,
    /// <c>last_error=NULL</c>, and a revived <c>dead</c> row is made immediately due).
    /// </summary>
    Task EnsureScheduledAsync(
        IReadOnlyCollection<ScheduledCollectorItem> items, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> due, unleased (or lease-expired), non-dead rows whose id
    /// is in <paramref name="activeCollectorIds"/>, using <c>FOR UPDATE SKIP LOCKED</c> so concurrent
    /// workers claim disjoint rows. Returns the resolved fencing values per claimed row. An empty
    /// <paramref name="activeCollectorIds"/> claims nothing.
    /// </summary>
    Task<IReadOnlyList<ClaimedCollectorLease>> ClaimDueAsync(
        string owner, TimeSpan ttl, int batchSize, IReadOnlyCollection<string> activeCollectorIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends <c>lease_expires_at</c> for the current lease. Returns false (0 rows) when the lease was
    /// lost (expired and reclaimed, so <paramref name="leaseToken"/> no longer matches).
    /// </summary>
    Task<bool> RenewLeaseAsync(
        string collectorId, string leaseToken, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the lease and sets <paramref name="status"/> without recording a run outcome, advancing
    /// <c>next_due_at</c> to <paramref name="nextDueAt"/> when supplied. The null-interval skip path; does
    /// not touch <c>current_run_id</c> or the failure counters. Fenced on <paramref name="leaseToken"/>.
    /// </summary>
    Task<bool> ReleaseLeaseAsync(
        string collectorId, string leaseToken, string status, DateTime? nextDueAt = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the run successful: <c>next_due_at = completion + interval</c>, <c>status='ok'</c>, clears the
    /// run id and lease, resets <c>failure_count</c> and <c>last_error</c>. Fenced on
    /// <paramref name="leaseToken"/>.
    /// </summary>
    Task<bool> CompleteSuccessAsync(
        string collectorId, string leaseToken, TimeSpan interval, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failure: keeps <c>current_run_id</c> (the retry reuses it), releases the lease, increments
    /// <c>failure_count</c>. When the incremented count reaches <paramref name="maxAttempts"/> the row goes
    /// terminal <c>dead</c> (never claimed again until revived); otherwise <c>error</c> with
    /// <c>next_due_at = UTC_TIMESTAMP(6) + LEAST(interval, baseBackoff * 2^failure_count)</c> from the
    /// pre-increment count. Fenced on <paramref name="leaseToken"/>. Returns the resulting outcome so the
    /// caller can log the terminal <c>dead</c> transition distinctly.
    /// </summary>
    Task<CollectorFailureOutcome> CompleteFailureAsync(
        string collectorId, string leaseToken, string error, TimeSpan interval, TimeSpan baseBackoff,
        int maxAttempts, CancellationToken cancellationToken = default);
}
