using Freeboard.Persistence;
using Freeboard.Scheduler;

namespace Freeboard.Web.Tests;

/// <summary>
/// In-memory <see cref="IScheduledCollectorRunner"/> double. Records each dispatch and optionally runs a
/// supplied behaviour (to throw, block on the cancellation token, etc.).
/// </summary>
internal sealed class FakeScheduledCollectorRunner : IScheduledCollectorRunner
{
    private readonly Lock gate = new();
    private readonly List<(string CollectorId, string RunId)> dispatched = [];

    public Func<EvidenceCollectorRow, string, CancellationToken, Task>? OnRun { get; set; }

    public IReadOnlyList<(string CollectorId, string RunId)> Dispatched
    {
        get { lock (gate) { return dispatched.ToList(); } }
    }

    public Task RunAsync(EvidenceCollectorRow collector, string runId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            dispatched.Add((collector.Id, runId));
        }

        return OnRun?.Invoke(collector, runId, cancellationToken) ?? Task.CompletedTask;
    }
}
