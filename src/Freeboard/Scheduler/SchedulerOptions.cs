namespace Freeboard.Scheduler;

/// <summary>
/// Options for the in-service collector scheduler, bound from the <c>Freeboard:CollectorScheduler</c>
/// config section. Due-ness and lease time are the database clock; the app clock is used only for loop
/// pacing.
/// </summary>
public sealed class SchedulerOptions
{
    public const string SectionName = "Freeboard:CollectorScheduler";

    /// <summary>When false the service no-ops without touching the database (also inert without a connection).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long to sleep between claim cycles.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Lease time-to-live. A crashed worker's row is reclaimable after at most this long.</summary>
    public TimeSpan LeaseTtl { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Maximum concurrent dispatches per cycle.</summary>
    public int MaxDegreeOfParallelism { get; set; } = 4;

    /// <summary>Maximum collectors claimed per cycle. Clamped to <see cref="MaxDegreeOfParallelism"/>.</summary>
    public int BatchSize { get; set; } = 4;

    /// <summary>Failures before a collector goes terminal <c>dead</c>.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Base for the bounded exponential retry backoff (capped at the collector's interval).</summary>
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Lease owner id for this process. Defaults to the machine name plus a per-process GUID.</summary>
    public string NodeId { get; set; } = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    /// <summary>
    /// The batch actually claimed per cycle: <c>min(BatchSize, MaxDegreeOfParallelism)</c>, or 0 when either
    /// is non-positive (a misconfiguration - the cycle then claims nothing). The scheduler must claim no more
    /// rows than it can dispatch immediately, so every claimed row starts its heartbeat at claim time and no
    /// claimed-but-queued row can have its lease lapse before dispatch. Returning 0 (rather than flooring to
    /// 1) keeps the <c>BatchSize &lt;= MaxDegreeOfParallelism</c> invariant intact under misconfiguration: a
    /// zero dispatch budget must not still claim one row.
    /// </summary>
    public int EffectiveBatchSize =>
        MaxDegreeOfParallelism <= 0 || BatchSize <= 0 ? 0 : Math.Min(BatchSize, MaxDegreeOfParallelism);
}
