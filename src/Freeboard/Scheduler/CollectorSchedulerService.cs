using System.Security.Cryptography;
using System.Text;
using Freeboard.Core.GitOps;
using Freeboard.Persistence;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Freeboard.Scheduler;

/// <summary>
/// In-service per-collector scheduler. Each cycle reads the integration collectors, ensures a
/// scheduler-state row for each with a resolvable interval, claims a batch of due rows across the replica
/// fleet (per-collector lease, SKIP LOCKED), and dispatches each through <see cref="IScheduledCollectorRunner"/>.
/// A per-row heartbeat renews the lease while a dispatch is in flight and cancels the dispatch if the lease
/// is lost; the heartbeat is stopped and awaited before the fenced completion so a late renewal cannot race
/// it. Time comparisons for due-ness and leasing are the database clock; the app clock only paces the loop.
/// </summary>
public sealed class CollectorSchedulerService(
    IComplianceStore complianceStore,
    ICollectorSchedulerStore schedulerStore,
    IScheduledCollectorRunner runner,
    IOptions<SchedulerOptions> options,
    ILogger<CollectorSchedulerService> logger,
    TimeProvider timeProvider,
    bool databaseConfigured)
    : BackgroundService
{
    private const string IntegrationType = "integration";
    private const int MaxErrorLength = 1000;

    private readonly SchedulerOptions options = options.Value;

    private bool ShouldSchedule => this.options.Enabled && databaseConfigured;

    // Backs off well past the poll interval when the table is missing, so a not-yet-migrated deployment
    // does not log-spam the missing-table warning every poll.
    private TimeSpan MissingTableBackoff =>
        TimeSpan.FromSeconds(Math.Max(options.PollInterval.TotalSeconds * 5, 60));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!ShouldSchedule)
        {
            logger.LogInformation(
                "Collector scheduler not started (Enabled={Enabled}, database configured={Configured}).",
                this.options.Enabled, databaseConfigured);
            return;
        }

        logger.LogInformation(
            "Collector scheduler started: node={NodeId} poll={Poll} ttl={Ttl} batch={Batch} maxDop={MaxDop}.",
            this.options.NodeId, this.options.PollInterval, this.options.LeaseTtl,
            this.options.EffectiveBatchSize, this.options.MaxDegreeOfParallelism);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = this.options.PollInterval;
            try
            {
                await RunCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.NoSuchTable)
            {
                // Only the specific missing-table error backs off: a transient connection/timeout error must
                // not be mistaken for a missing table, so it falls through to the general handler below.
                logger.LogWarning(
                    "collector_scheduler_state is missing (migration 016 not applied); backing off. {Message}",
                    ex.Message);
                delay = MissingTableBackoff;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Collector scheduler cycle failed; continuing.");
            }

            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // One scheduler cycle. Internal so orchestration tests can drive a single cycle without the loop.
    internal async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        // A non-positive MaxDegreeOfParallelism or BatchSize yields a zero dispatch budget. Claim nothing:
        // claiming a row we cannot dispatch would leave it leased without a heartbeat until it lapsed.
        if (options.EffectiveBatchSize <= 0)
        {
            logger.LogWarning(
                "Collector scheduler skipping cycle: non-positive batch (BatchSize={BatchSize}, MaxDegreeOfParallelism={MaxDop}).",
                options.BatchSize, options.MaxDegreeOfParallelism);
            return;
        }

        var collectors = await complianceStore.GetEvidenceCollectorsAsync(cancellationToken).ConfigureAwait(false);

        // Only integration collectors with a resolvable interval are scheduled. A null-interval collector is
        // not seeded and its id is kept out of the active set, so it is never claimed (the primary guard
        // against a null interval tight-looping).
        var schedulable = collectors
            .Where(c => string.Equals(c.Type, IntegrationType, StringComparison.Ordinal)
                        && EvidenceCollectorFrequency.Interval(c.Frequency) is not null)
            .ToList();
        if (schedulable.Count == 0)
        {
            return;
        }

        await schedulerStore.EnsureScheduledAsync(
            schedulable.Select(c => new ScheduledCollectorItem(c.Id, Fingerprint(c))).ToList(),
            cancellationToken).ConfigureAwait(false);

        var activeIds = schedulable.Select(c => c.Id).ToList();
        var claimed = await schedulerStore.ClaimDueAsync(
            options.NodeId, options.LeaseTtl, options.EffectiveBatchSize, activeIds, cancellationToken)
            .ConfigureAwait(false);
        if (claimed.Count == 0)
        {
            return;
        }

        var byId = schedulable.ToDictionary(c => c.Id, StringComparer.Ordinal);
        // EffectiveBatchSize <= MaxDegreeOfParallelism, so every claimed row can dispatch immediately and
        // start its heartbeat with no queuing gap; no extra throttle is needed.
        await Task.WhenAll(claimed.Select(lease => DispatchAsync(lease, byId, cancellationToken))).ConfigureAwait(false);
    }

    private async Task DispatchAsync(
        ClaimedCollectorLease lease,
        IReadOnlyDictionary<string, EvidenceCollectorRow> byId,
        CancellationToken stoppingToken)
    {
        if (!byId.TryGetValue(lease.CollectorId, out var collector))
        {
            return;
        }

        var interval = EvidenceCollectorFrequency.Interval(collector.Frequency);
        if (interval is null)
        {
            // Defensive: such ids are already excluded from the active set, so this is normally unreachable.
            // Release with a non-claimable status so it is not re-selected every poll.
            logger.LogWarning(
                "Claimed collector={CollectorId} has no resolvable interval; releasing as dead.", lease.CollectorId);
            await schedulerStore.ReleaseLeaseAsync(lease.CollectorId, lease.LeaseToken, "dead", null, stoppingToken)
                .ConfigureAwait(false);
            return;
        }

        logger.LogInformation("claimed collector={CollectorId} run={RunId}", lease.CollectorId, lease.CurrentRunId);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        // Linked to the host stopping token so shutdown stops the heartbeat even if the runner is slow to
        // return: on shutdown the heartbeat stops renewing and the lease is left to expire. The finally
        // below still cancels and awaits it before any completion, preserving the ordering guarantee.
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = HeartbeatAsync(lease, linked, heartbeatStop.Token);

        Exception? failure = null;
        try
        {
            await runner.RunAsync(collector, lease.CurrentRunId, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            // Stop and await the heartbeat BEFORE the fenced completion: a late renewal firing after a
            // completion cleared the lease would see 0 rows and falsely log a lost lease.
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }

        // Lease lost mid-dispatch (heartbeat cancelled the linked token): the new holder owns the row, so do
        // not complete. Host stopping: leave the lease to expire, no forced completion of partial work.
        if (linked.IsCancellationRequested)
        {
            return;
        }

        if (failure is null)
        {
            var held = await schedulerStore.CompleteSuccessAsync(lease.CollectorId, lease.LeaseToken, interval.Value, stoppingToken)
                .ConfigureAwait(false);
            if (held)
            {
                logger.LogInformation(
                    "completed collector={CollectorId} run={RunId} next-due=+{Interval}",
                    lease.CollectorId, lease.CurrentRunId, interval.Value);
            }
            else
            {
                logger.LogWarning("lease-lost collector={CollectorId} run={RunId} on complete.", lease.CollectorId, lease.CurrentRunId);
            }

            return;
        }

        var outcome = await schedulerStore.CompleteFailureAsync(
            lease.CollectorId, lease.LeaseToken, Truncate(failure.Message), interval.Value,
            options.BaseBackoff, options.MaxAttempts, stoppingToken).ConfigureAwait(false);
        switch (outcome)
        {
            case CollectorFailureOutcome.Dead:
                logger.LogError(
                    failure, "dead collector={CollectorId} run={RunId} (gave up after {MaxAttempts} attempts).",
                    lease.CollectorId, lease.CurrentRunId, options.MaxAttempts);
                break;
            case CollectorFailureOutcome.Retrying:
                logger.LogWarning(
                    failure, "failed collector={CollectorId} run={RunId} (retry after backoff).",
                    lease.CollectorId, lease.CurrentRunId);
                break;
            default:
                logger.LogWarning("lease-lost collector={CollectorId} run={RunId} on failure.", lease.CollectorId, lease.CurrentRunId);
                break;
        }
    }

    private async Task HeartbeatAsync(
        ClaimedCollectorLease lease, CancellationTokenSource linked, CancellationToken heartbeatStop)
    {
        // Renew at ~TTL/3 so two renewals can be missed before the lease lapses. Floored so a tiny
        // misconfigured TTL cannot busy-loop.
        var period = TimeSpan.FromMilliseconds(Math.Max(options.LeaseTtl.TotalMilliseconds / 3, 100));
        while (!heartbeatStop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(period, timeProvider, heartbeatStop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool held;
            try
            {
                held = await schedulerStore.RenewLeaseAsync(
                    lease.CollectorId, lease.LeaseToken, options.LeaseTtl, heartbeatStop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A transient renewal error is not proof the lease is lost; log and try again next tick.
                logger.LogWarning(ex, "Heartbeat renewal failed for collector={CollectorId}; retrying.", lease.CollectorId);
                continue;
            }

            if (!held)
            {
                logger.LogWarning("lease-lost collector={CollectorId} run={RunId}; cancelling dispatch.", lease.CollectorId, lease.CurrentRunId);
                await linked.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    // Hash over the scheduling-relevant config (type + frequency) as lowercase hex (CHAR(64)); a change to
    // it revives a dead/error row via ensure.
    private static string Fingerprint(EvidenceCollectorRow collector) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{collector.Type}\n{collector.Frequency}")));

    private static string Truncate(string value) =>
        value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}
