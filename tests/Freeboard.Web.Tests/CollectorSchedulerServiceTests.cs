using Freeboard.Persistence;
using Freeboard.Scheduler;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Freeboard.Web.Tests;

/// <summary>
/// Orchestration tests for <see cref="CollectorSchedulerService"/> using in-memory fakes (fake scheduler
/// store with real leasing/backoff/dead semantics, fake compliance store, fake runner). Each drives a
/// single cycle via the internal RunCycleAsync, except the disabled cases which drive the hosted-service
/// start path. No MySQL. The exact missing-table error-code handling is covered at the persistence layer
/// (MySqlException with ErrorCode NoSuchTable is not constructible outside the client); here the general
/// "other errors surface" path is asserted instead.
/// </summary>
public sealed class CollectorSchedulerServiceTests
{
    private static EvidenceCollectorRow Collector(string id, string type = "integration", string frequency = "daily") =>
        new(id, "Title", "ctrl-1", Vendor: null, type, frequency, Threshold: null,
            new Dictionary<string, string>(StringComparer.Ordinal));

    private static SchedulerOptions Options(Action<SchedulerOptions>? tweak = null)
    {
        var o = new SchedulerOptions { NodeId = "test-node", PollInterval = TimeSpan.FromMilliseconds(20) };
        tweak?.Invoke(o);
        return o;
    }

    private static CollectorSchedulerService Service(
        FakeComplianceStore compliance,
        ICollectorSchedulerStore store,
        FakeScheduledCollectorRunner runner,
        SchedulerOptions options,
        bool databaseConfigured = true,
        TimeProvider? timeProvider = null) =>
        new(
            compliance, store, runner, Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<CollectorSchedulerService>.Instance, timeProvider ?? TimeProvider.System, databaseConfigured);

    [Fact]
    public async Task DueIntegrationCollectorIsClaimedAndDispatchedOnceWithRunId()
    {
        var compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] };
        var store = new FakeCollectorSchedulerStore();
        var runner = new FakeScheduledCollectorRunner();
        var service = Service(compliance, store, runner, Options());

        await service.RunCycleAsync(CancellationToken.None);

        var dispatch = Assert.Single(runner.Dispatched);
        Assert.Equal("col-1", dispatch.CollectorId);
        Assert.False(string.IsNullOrEmpty(dispatch.RunId));

        var row = store.Peek("col-1");
        Assert.NotNull(row);
        Assert.Equal("ok", row!.Status);
        Assert.Equal("run-1", dispatch.RunId); // the stable run id passed to the runner
        Assert.Null(row.CurrentRunId); // cleared on success
    }

    [Fact]
    public async Task NonIntegrationCollectorsAreNeverScheduled()
    {
        var compliance = new FakeComplianceStore
        {
            Collectors = [Collector("script-1", type: "script"), Collector("manual-1", type: "manual-attestation")],
        };
        var store = new FakeCollectorSchedulerStore();
        var runner = new FakeScheduledCollectorRunner();
        var service = Service(compliance, store, runner, Options());

        await service.RunCycleAsync(CancellationToken.None);

        Assert.Empty(runner.Dispatched);
        Assert.Equal(0, store.EnsureCalls); // nothing schedulable, so no ensure/claim at all
        Assert.Equal(0, store.RowCount);
    }

    [Fact]
    public async Task RunnerExceptionIsCaughtLeaseReleasedRunTokenKeptAndBatchContinues()
    {
        var compliance = new FakeComplianceStore { Collectors = [Collector("bad"), Collector("good")] };
        var store = new FakeCollectorSchedulerStore();
        var runner = new FakeScheduledCollectorRunner
        {
            OnRun = (collector, _, _) => collector.Id == "bad"
                ? throw new InvalidOperationException("boom")
                : Task.CompletedTask,
        };
        var service = Service(compliance, store, runner, Options());

        await service.RunCycleAsync(CancellationToken.None);

        // Both were dispatched: one failure does not abort the batch.
        Assert.Equal(2, runner.Dispatched.Count);

        var bad = store.Peek("bad")!;
        Assert.Equal("error", bad.Status);
        Assert.Equal(1, bad.FailureCount);
        Assert.False(string.IsNullOrEmpty(bad.CurrentRunId)); // retained for the retry
        Assert.Null(bad.LeaseToken); // lease released

        Assert.Equal("ok", store.Peek("good")!.Status);
    }

    [Fact]
    public async Task CollectorFailingMaxAttemptsGoesDeadAndIsNotDispatchedAgain()
    {
        var compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] };
        var store = new FakeCollectorSchedulerStore();
        var runner = new FakeScheduledCollectorRunner
        {
            OnRun = (_, _, _) => throw new InvalidOperationException("always fails"),
        };
        var service = Service(compliance, store, runner, Options(o => o.MaxAttempts = 2));

        // First failure -> error.
        await service.RunCycleAsync(CancellationToken.None);
        Assert.Equal("error", store.Peek("col-1")!.Status);

        // Second failure -> dead.
        store.MakeDue("col-1");
        await service.RunCycleAsync(CancellationToken.None);
        Assert.Equal("dead", store.Peek("col-1")!.Status);

        // A dead collector is not claimed or dispatched again, even when due.
        store.MakeDue("col-1");
        await service.RunCycleAsync(CancellationToken.None);
        Assert.Equal(2, runner.Dispatched.Count);
        Assert.Equal("dead", store.Peek("col-1")!.Status);
    }

    [Theory]
    [InlineData(0, 4)] // MaxDegreeOfParallelism non-positive
    [InlineData(4, 0)] // BatchSize non-positive
    public async Task NonPositiveBatchConfigClaimsAndDispatchesNothing(int maxDop, int batchSize)
    {
        // A zero dispatch budget must not still claim one row (that row would be leased without a
        // heartbeat). The cycle skips claiming entirely.
        var compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] };
        var store = new FakeCollectorSchedulerStore();
        var runner = new FakeScheduledCollectorRunner();
        var service = Service(compliance, store, runner, Options(o =>
        {
            o.MaxDegreeOfParallelism = maxDop;
            o.BatchSize = batchSize;
        }));

        await service.RunCycleAsync(CancellationToken.None);

        Assert.Empty(runner.Dispatched);
        Assert.Equal(0, store.ClaimCalls);
        Assert.Equal(0, store.EnsureCalls);
    }

    [Fact]
    public async Task NullIntervalCollectorIsNotSeededOrClaimed()
    {
        // An integration collector with an unknown frequency resolves to a null interval: it is filtered
        // out of the ensure input and the active set, so it is never seeded and never claimed.
        var compliance = new FakeComplianceStore { Collectors = [Collector("col-1", frequency: "fortnightly")] };
        var store = new FakeCollectorSchedulerStore();
        var runner = new FakeScheduledCollectorRunner();
        var service = Service(compliance, store, runner, Options());

        await service.RunCycleAsync(CancellationToken.None);

        Assert.Empty(runner.Dispatched);
        Assert.Equal(0, store.RowCount);
    }

    [Fact]
    public async Task LostLeaseCancelsInFlightDispatchAndSkipsCompletion()
    {
        var compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] };
        // Small TTL so the heartbeat fires quickly; renewals report the lease lost.
        var store = new FakeCollectorSchedulerStore { RenewalsReportLost = true };
        var cancelled = new TaskCompletionSource();
        var runner = new FakeScheduledCollectorRunner
        {
            OnRun = async (_, _, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                }
            },
        };
        var service = Service(compliance, store, runner, Options(o => o.LeaseTtl = TimeSpan.FromMilliseconds(300)));

        // Bounded so a heartbeat regression (runner never cancelled) fails fast instead of hanging the suite.
        await service.RunCycleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // The runner observed cancellation triggered by the lost-lease heartbeat.
        Assert.True(cancelled.Task.IsCompletedSuccessfully);
        // The lease was lost, so the service did not complete the run: the row stays running under the (now
        // superseded) lease rather than being marked ok/error by this worker.
        Assert.Equal("running", store.Peek("col-1")!.Status);
    }

    [Fact]
    public async Task HostShutdownStopsHeartbeatEvenWhenRunnerIsSlow()
    {
        // On host stop the heartbeat must stop renewing (the lease is left to expire), even if the runner
        // is slow / non-cooperative. Here the runner ignores its own token and blocks on a gate the test
        // controls, so the only thing that can stop the heartbeat is the host stopping token.
        var compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] };
        var store = new FakeCollectorSchedulerStore();
        using var release = new SemaphoreSlim(0);
        var runner = new FakeScheduledCollectorRunner
        {
            OnRun = async (_, _, _) => await release.WaitAsync(TimeSpan.FromSeconds(10)),
        };
        var service = Service(compliance, store, runner, Options(o => o.LeaseTtl = TimeSpan.FromMilliseconds(300)));

        using var host = new CancellationTokenSource();
        var cycle = service.RunCycleAsync(host.Token);

        // Let at least one heartbeat renewal happen, then signal host shutdown.
        await Task.Delay(250);
        await host.CancelAsync();

        // After shutdown the heartbeat stops renewing: the renewal count settles.
        await Task.Delay(250);
        var afterStop = store.RenewCalls;
        await Task.Delay(250);
        Assert.Equal(afterStop, store.RenewCalls);

        release.Release();
        await cycle.WaitAsync(TimeSpan.FromSeconds(10));

        // Partial work under a stopping host is not force-completed; the lease is left to expire.
        Assert.Equal("running", store.Peek("col-1")!.Status);
    }

    [Fact]
    public async Task DisabledSchedulerDispatchesNothingAndDoesNotQuery()
    {
        var compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] };
        var store = new FakeCollectorSchedulerStore();
        var runner = new FakeScheduledCollectorRunner();
        var service = Service(compliance, store, runner, Options(o => o.Enabled = false));

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(runner.Dispatched);
        Assert.Equal(0, store.ClaimCalls);
        Assert.Equal(0, store.EnsureCalls);
    }

    [Fact]
    public async Task EmptyConnectionSchedulerDispatchesNothingAndDoesNotQuery()
    {
        var compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] };
        var store = new FakeCollectorSchedulerStore();
        var runner = new FakeScheduledCollectorRunner();
        var service = Service(compliance, store, runner, Options(), databaseConfigured: false);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(runner.Dispatched);
        Assert.Equal(0, store.ClaimCalls);
        Assert.Equal(0, store.EnsureCalls);
    }

    [Fact]
    public async Task NonMissingTableErrorSurfacesAndIsNotSwallowed()
    {
        // A generic store error must propagate out of the cycle (it is NOT caught as a missing table),
        // so the loop's general error handling can log it rather than mis-classifying it.
        var compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] };
        var store = new ThrowingSchedulerStore();
        var runner = new FakeScheduledCollectorRunner();
        var service = Service(compliance, store, runner, Options());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunCycleAsync(CancellationToken.None));
    }

    /// <summary>A scheduler store whose claim throws a non-MySql error, to prove it is not swallowed.</summary>
    private sealed class ThrowingSchedulerStore : ICollectorSchedulerStore
    {
        public Task EnsureScheduledAsync(IReadOnlyCollection<ScheduledCollectorItem> items, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ClaimedCollectorLease>> ClaimDueAsync(
            string owner, TimeSpan ttl, int batchSize, IReadOnlyCollection<string> activeCollectorIds,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("transient claim failure");

        public Task<bool> RenewLeaseAsync(string collectorId, string leaseToken, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> ReleaseLeaseAsync(string collectorId, string leaseToken, string status, DateTime? nextDueAt = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> CompleteSuccessAsync(string collectorId, string leaseToken, TimeSpan interval, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<CollectorFailureOutcome> CompleteFailureAsync(string collectorId, string leaseToken, string error, TimeSpan interval, TimeSpan baseBackoff, int maxAttempts, CancellationToken cancellationToken = default) =>
            Task.FromResult(CollectorFailureOutcome.LeaseLost);
    }
}
