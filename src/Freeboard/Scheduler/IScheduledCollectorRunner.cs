using Freeboard.Persistence;

namespace Freeboard.Scheduler;

/// <summary>
/// Dispatch target for a due collector. <paramref name="runId"/> is the stable <c>current_run_id</c>, passed
/// so a future real runner can make its evidence append idempotent on it. Implementations must honor the
/// cancellation token: a worker that loses its lease cancels the in-flight dispatch.
/// </summary>
public interface IScheduledCollectorRunner
{
    Task RunAsync(EvidenceCollectorRow collector, string runId, CancellationToken cancellationToken);
}

/// <summary>
/// Default no-op runner: logs the dispatch and returns without appending evidence. The real
/// integration-execution runner replaces this single registration later. The log line makes the no-op
/// phase observable (a dispatch with no downstream evidence effect).
/// </summary>
public sealed class LoggingScheduledCollectorRunner(ILogger<LoggingScheduledCollectorRunner> logger)
    : IScheduledCollectorRunner
{
    public Task RunAsync(EvidenceCollectorRow collector, string runId, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Collector scheduler dispatch (no-op runner, no evidence appended): collector={CollectorId} "
            + "run={RunId} type={Type} frequency={Frequency}",
            collector.Id, runId, collector.Type, collector.Frequency);
        return Task.CompletedTask;
    }
}
