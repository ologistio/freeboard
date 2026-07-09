using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// In-memory <see cref="ICollectorSchedulerStore"/> double that mimics the real leasing, backoff, and
/// dead-letter semantics so the hosted-service orchestration can be exercised without MySQL. Time is the
/// fake's own <see cref="Now"/> clock. Thread-safe: dispatches can run concurrently.
/// </summary>
internal sealed class FakeCollectorSchedulerStore : ICollectorSchedulerStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Row> rows = new(StringComparer.Ordinal);
    private long tokenSeq;

    public DateTime Now { get; set; } = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>When true every <see cref="RenewLeaseAsync"/> reports the lease lost (0 rows).</summary>
    public bool RenewalsReportLost { get; set; }

    public int EnsureCalls { get; private set; }

    public int ClaimCalls { get; private set; }

    private int renewCalls;

    public int RenewCalls
    {
        get { lock (gate) { return renewCalls; } }
    }

    public sealed record Snapshot(string Status, int FailureCount, string? CurrentRunId, string? LeaseToken, DateTime NextDueAt);

    public Snapshot? Peek(string collectorId)
    {
        lock (gate)
        {
            return rows.TryGetValue(collectorId, out var r)
                ? new Snapshot(r.Status, r.FailureCount, r.CurrentRunId, r.LeaseToken, r.NextDueAt)
                : null;
        }
    }

    public int RowCount
    {
        get { lock (gate) { return rows.Count; } }
    }

    /// <summary>Forces a collector due again (undoes a backoff/interval advance) for the next claim.</summary>
    public void MakeDue(string collectorId)
    {
        lock (gate)
        {
            if (rows.TryGetValue(collectorId, out var r))
            {
                r.NextDueAt = Now - TimeSpan.FromSeconds(1);
            }
        }
    }

    public Task EnsureScheduledAsync(
        IReadOnlyCollection<ScheduledCollectorItem> items, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            EnsureCalls++;
            foreach (var item in items)
            {
                if (!rows.TryGetValue(item.CollectorId, out var r))
                {
                    rows[item.CollectorId] = new Row
                    {
                        NextDueAt = Now,
                        Status = "pending",
                        FailureCount = 0,
                        ConfigFingerprint = item.ConfigFingerprint,
                    };
                    continue;
                }

                if (r.Status == "running")
                {
                    continue;
                }

                if (r.ConfigFingerprint != item.ConfigFingerprint)
                {
                    if (r.Status is "dead" or "error")
                    {
                        r.FailureCount = 0;
                        r.LastError = null;
                        if (r.Status == "dead")
                        {
                            r.NextDueAt = Now;
                        }

                        r.Status = "pending";
                    }

                    r.ConfigFingerprint = item.ConfigFingerprint;
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ClaimedCollectorLease>> ClaimDueAsync(
        string owner, TimeSpan ttl, int batchSize, IReadOnlyCollection<string> activeCollectorIds,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ClaimCalls++;
            if (batchSize <= 0 || activeCollectorIds.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<ClaimedCollectorLease>>([]);
            }

            var active = new HashSet<string>(activeCollectorIds, StringComparer.Ordinal);
            var due = rows
                .Where(kvp => active.Contains(kvp.Key)
                              && kvp.Value.NextDueAt <= Now
                              && kvp.Value.Status != "dead"
                              && (kvp.Value.LeaseToken is null || kvp.Value.LeaseExpiresAt <= Now))
                .OrderBy(kvp => kvp.Value.NextDueAt)
                .Take(batchSize)
                .ToList();

            var claimed = new List<ClaimedCollectorLease>(due.Count);
            foreach (var (id, r) in due)
            {
                r.LeaseOwner = owner;
                r.LeaseToken = $"lease-{++tokenSeq}";
                r.LeaseExpiresAt = Now + ttl;
                r.Status = "running";
                r.CurrentRunId ??= $"run-{tokenSeq}";
                claimed.Add(new ClaimedCollectorLease(id, r.LeaseToken, r.CurrentRunId, r.LeaseExpiresAt.Value));
            }

            return Task.FromResult<IReadOnlyList<ClaimedCollectorLease>>(claimed);
        }
    }

    public Task<bool> RenewLeaseAsync(
        string collectorId, string leaseToken, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            renewCalls++;
            if (RenewalsReportLost)
            {
                return Task.FromResult(false);
            }

            if (rows.TryGetValue(collectorId, out var r) && r.LeaseToken == leaseToken)
            {
                r.LeaseExpiresAt = Now + ttl;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }

    public Task<bool> ReleaseLeaseAsync(
        string collectorId, string leaseToken, string status, DateTime? nextDueAt = null,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (rows.TryGetValue(collectorId, out var r) && r.LeaseToken == leaseToken)
            {
                ClearLease(r);
                r.Status = status;
                r.NextDueAt = nextDueAt ?? r.NextDueAt;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }

    public Task<bool> CompleteSuccessAsync(
        string collectorId, string leaseToken, TimeSpan interval, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (rows.TryGetValue(collectorId, out var r) && r.LeaseToken == leaseToken)
            {
                r.NextDueAt = Now + interval;
                r.Status = "ok";
                r.FailureCount = 0;
                r.LastError = null;
                r.CurrentRunId = null;
                ClearLease(r);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }

    public Task<CollectorFailureOutcome> CompleteFailureAsync(
        string collectorId, string leaseToken, string error, TimeSpan interval, TimeSpan baseBackoff,
        int maxAttempts, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (rows.TryGetValue(collectorId, out var r) && r.LeaseToken == leaseToken)
            {
                var newCount = r.FailureCount + 1;
                CollectorFailureOutcome outcome;
                if (newCount >= maxAttempts)
                {
                    r.Status = "dead";
                    outcome = CollectorFailureOutcome.Dead;
                }
                else
                {
                    r.Status = "error";
                    var backoff = TimeSpan.FromSeconds(Math.Min(
                        interval.TotalSeconds, baseBackoff.TotalSeconds * Math.Pow(2, r.FailureCount)));
                    r.NextDueAt = Now + backoff;
                    outcome = CollectorFailureOutcome.Retrying;
                }

                r.FailureCount = newCount;
                r.LastError = error;
                // current_run_id retained for the retry.
                ClearLease(r);
                return Task.FromResult(outcome);
            }

            return Task.FromResult(CollectorFailureOutcome.LeaseLost);
        }
    }

    private static void ClearLease(Row r)
    {
        r.LeaseOwner = null;
        r.LeaseToken = null;
        r.LeaseExpiresAt = null;
    }

    private sealed class Row
    {
        public DateTime NextDueAt { get; set; }

        public string? CurrentRunId { get; set; }

        public string? LeaseOwner { get; set; }

        public string? LeaseToken { get; set; }

        public DateTime? LeaseExpiresAt { get; set; }

        public int FailureCount { get; set; }

        public string Status { get; set; } = "pending";

        public string? ConfigFingerprint { get; set; }

        public string? LastError { get; set; }
    }
}
