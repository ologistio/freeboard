using Freeboard.Persistence;

namespace Freeboard.Scheduler;

/// <summary>
/// Dispatch target for a due collector. <paramref name="runId"/> is the stable <c>current_run_id</c>, passed
/// so a future real runner can make its evidence append idempotent on it. It names ONE collection cycle,
/// and a re-delivered append under the same cycle id is an accepted replay rather than a run failure: a
/// new lease holder re-dispatches the cycle a lost lease interrupted.
/// <para>
/// Implementations must honor the cancellation token: a worker that loses its lease cancels the in-flight
/// dispatch. Honoring it means THROWING <see cref="OperationCanceledException"/> rather than returning. A
/// runner that has not finished its work must not return normally under a cancelled token, because the
/// service reads a normal return as a finished cycle: it completes the run, clears the run token, and the
/// next dispatch of this collector therefore runs under a new run id.
/// </para>
/// </summary>
public interface IScheduledCollectorRunner
{
    Task RunAsync(CollectorRow collector, string runId, CancellationToken cancellationToken);
}

/// <summary>
/// Default no-op runner: logs the dispatch and returns without appending evidence. The real
/// integration-execution runner replaces this single registration later. The log line makes the no-op
/// phase observable (a dispatch with no downstream evidence effect).
/// </summary>
public sealed class LoggingScheduledCollectorRunner(ILogger<LoggingScheduledCollectorRunner> logger)
    : IScheduledCollectorRunner
{
    public Task RunAsync(CollectorRow collector, string runId, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Collector scheduler dispatch (no-op runner, no evidence appended): collector={CollectorId} "
            + "run={RunId} type={Type} frequency={Frequency}",
            collector.Id, runId, collector.Type, collector.Frequency);
        return Task.CompletedTask;
    }
}
