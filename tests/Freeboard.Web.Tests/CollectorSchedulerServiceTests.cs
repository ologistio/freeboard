using Freeboard.Core.GitOps;
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
    private static CollectorRow Collector(
        string id,
        string type = "integration",
        string frequency = "daily",
        string? provider = "fleet",
        string? connection = "fleet-prod",
        int? threshold = null,
        CollectorConfigView? config = null) =>
        new(id, "Title", "ctrl-1", Vendor: null, type, provider, frequency, threshold,
            config ?? CollectorConfigView.Empty, connection);

    private static CollectorConfigView Config(string sourceKey) =>
        new(null, [], null, [], [new Check { SourceKey = sourceKey, Name = "mfa-enforced", Severity = "Hard" }]);

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
            Collectors =
            [
                Collector("script-1", type: "script"),
                Collector("manual-1", type: "manual"),
                Collector("training-1", type: "training"),
            ],
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

    // The fingerprint's scope widened with the merge, and these pin what is now inside it. An operator
    // repairing a bad source_key or repointing a connection is making a config or connection edit, and
    // must not have to wait a dead row out.
    [Theory]
    [InlineData("provider")]
    [InlineData("connection")]
    [InlineData("config")]
    public async Task ACollectionInstructionEditRevivesADeadRow(string edit)
    {
        var store = new FakeCollectorSchedulerStore();
        var runner = new FakeScheduledCollectorRunner
        {
            OnRun = (_, _, _) => throw new InvalidOperationException("always fails"),
        };
        var original = Collector("col-1", config: Config("12"));
        var compliance = new FakeComplianceStore { Collectors = [original] };
        var service = Service(compliance, store, runner, Options(o => o.MaxAttempts = 1));

        await service.RunCycleAsync(CancellationToken.None);
        Assert.Equal("dead", store.Peek("col-1")!.Status);

        compliance.Collectors =
        [
            edit switch
            {
                "provider" => original with { Provider = "intune" },
                "connection" => original with { Connection = "fleet-dev" },
                _ => original with { Config = Config("34") },
            },
        ];
        runner.OnRun = null;
        await service.RunCycleAsync(CancellationToken.None);

        Assert.Equal("ok", store.Peek("col-1")!.Status);
        // The revival cleared the retained run token, so the revived collector collects under a new cycle
        // rather than re-running the one it died on.
        Assert.NotEqual(runner.Dispatched[0].RunId, runner.Dispatched[1].RunId);
    }

    // The fingerprint is persisted and compared across process restarts and app upgrades, so its VALUE
    // is the contract, not merely its sensitivity to an edit. Comparing two fingerprints minted by one
    // process cannot see a member-order change, because both sides move together; this golden digest
    // can. It is the assertion that fails if a [JsonPropertyOrder] on CollectorConfigView, QuizItemView,
    // AttestationField, or Check is removed or renumbered - which would silently revive every dead
    // scheduler row on upgrade. A deliberate change to the hash input is meant to break it: recompute
    // the constant then, and say so in the change.
    [Fact]
    public async Task TheFingerprintOfAKnownCollectorMatchesItsGoldenDigest()
    {
        var config = new CollectorConfigView(
            "Confirm the ruleset was reviewed.",
            [new AttestationField { Id = "f1", Label = "Reviewed?", Type = "single-choice", Options = ["yes", "no"] }],
            80,
            [new QuizItemView("q1", "What should you do with an unexpected attachment?", ["Open it", "Report it"])],
            [new Check { SourceKey = "42", Name = "mfa-enforced", Severity = "Hard" }]);
        var store = new FakeCollectorSchedulerStore();
        var compliance = new FakeComplianceStore { Collectors = [Collector("col-1", config: config)] };
        var service = Service(compliance, store, new FakeScheduledCollectorRunner(), Options());

        await service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(
            "2aceb2c29dc40ebd0808729d2682339883d4630203372db754ed6bee9d9df098",
            store.Peek("col-1")!.ConfigFingerprint);
    }

    // The nested check items are the only part of an integration collector's config that varies, so this
    // is the case the nested [JsonPropertyOrder] pinning exists for.
    [Fact]
    public async Task ChangingOnlyACheckSourceKeyChangesTheFingerprint()
    {
        var store = new FakeCollectorSchedulerStore();
        var compliance = new FakeComplianceStore { Collectors = [Collector("col-1", config: Config("12"))] };
        var service = Service(compliance, store, new FakeScheduledCollectorRunner(), Options());

        await service.RunCycleAsync(CancellationToken.None);
        var before = store.Peek("col-1")!.ConfigFingerprint;

        compliance.Collectors = [Collector("col-1", config: Config("34"))];
        await service.RunCycleAsync(CancellationToken.None);

        Assert.NotEqual(before, store.Peek("col-1")!.ConfigFingerprint);
    }

    // Threshold is a scoring input, not a collection instruction: editing it must revive nothing.
    [Fact]
    public async Task AThresholdEditLeavesTheFingerprintAndADeadRowAlone()
    {
        var store = new FakeCollectorSchedulerStore();
        var runner = new FakeScheduledCollectorRunner
        {
            OnRun = (_, _, _) => throw new InvalidOperationException("always fails"),
        };
        var compliance = new FakeComplianceStore { Collectors = [Collector("col-1", threshold: 90)] };
        var service = Service(compliance, store, runner, Options(o => o.MaxAttempts = 1));

        await service.RunCycleAsync(CancellationToken.None);
        var dead = store.Peek("col-1")!;
        Assert.Equal("dead", dead.Status);

        compliance.Collectors = [Collector("col-1", threshold: 50)];
        await service.RunCycleAsync(CancellationToken.None);

        var after = store.Peek("col-1")!;
        Assert.Equal(dead.ConfigFingerprint, after.ConfigFingerprint);
        Assert.Equal("dead", after.Status);
    }

    // Reviving is for a row that has stopped collecting. A healthy row keeps its schedule, so a config
    // edit must not pull its next run forward.
    [Fact]
    public async Task AConfigEditLeavesAHealthyRowsNextDueAtAlone()
    {
        var store = new FakeCollectorSchedulerStore();
        var compliance = new FakeComplianceStore { Collectors = [Collector("col-1", config: Config("12"))] };
        var service = Service(compliance, store, new FakeScheduledCollectorRunner(), Options());

        await service.RunCycleAsync(CancellationToken.None);
        var healthy = store.Peek("col-1")!;
        Assert.Equal("ok", healthy.Status);

        compliance.Collectors = [Collector("col-1", config: Config("34"))];
        await service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(healthy.NextDueAt, store.Peek("col-1")!.NextDueAt);
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
    // The runner here swallows the cancellation and returns normally, so the dispatch takes the success
    // arm and attempts its fenced completion. The fence is what makes that harmless: the row now carries
    // the new holder's lease token, so the write matches nothing.
    public async Task LostLeaseCancelsInFlightDispatchAndItsCompletionChangesNothing()
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
        // The completion was ATTEMPTED once - the fence, not a skipped call, is what makes it harmless.
        Assert.Equal(1, store.CompleteSuccessCalls);
        // That completion matched no row, so the state stays with the new holder: the row is still
        // running and keeps its run token, which the new holder re-dispatches.
        var row = store.Peek("col-1")!;
        Assert.Equal("running", row.Status);
        Assert.Equal(runner.Dispatched[0].RunId, row.CurrentRunId);
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

        // This runner returns normally, which asserts that it finished its work, so the dispatch ends its
        // cycle even though the host is stopping: the completion write does not take the stopping token.
        var row = store.Peek("col-1")!;
        Assert.Equal("ok", row.Status);
        Assert.Null(row.CurrentRunId);
        Assert.Equal(store.Now + TimeSpan.FromDays(1), row.NextDueAt);
    }

    [Fact]
    public async Task ARunnerThatRethrowsCancellationUnderShutdownRecordsNoOutcome()
    {
        // A runner that honors its token throws instead of returning, and cancelling our own work must not
        // cost the collector a failure: a recorded failure would back it off and, at MaxAttempts, kill it.
        var compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] };
        var store = new FakeCollectorSchedulerStore();
        var running = new TaskCompletionSource();
        var runner = new FakeScheduledCollectorRunner
        {
            OnRun = async (_, _, token) =>
            {
                running.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            },
        };
        var service = Service(compliance, store, runner, Options());

        using var host = new CancellationTokenSource();
        var cycle = service.RunCycleAsync(host.Token);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await host.CancelAsync();
        await cycle.WaitAsync(TimeSpan.FromSeconds(10));

        var row = store.Peek("col-1")!;
        Assert.NotEqual("error", row.Status);
        Assert.NotEqual("dead", row.Status);
        Assert.Equal(0, row.FailureCount);
        Assert.Equal(runner.Dispatched[0].RunId, row.CurrentRunId);
    }

    [Fact]
    public async Task ADispatchThatSucceedsWhileTheHostStopsEndsItsCycle()
    {
        var compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] };
        var store = new FakeCollectorSchedulerStore();
        var running = new TaskCompletionSource();
        using var release = new SemaphoreSlim(0);
        var runner = new FakeScheduledCollectorRunner
        {
            OnRun = async (_, _, _) =>
            {
                running.TrySetResult();
                await release.WaitAsync(TimeSpan.FromSeconds(10));
            },
        };
        var service = Service(compliance, store, runner, Options());

        using var host = new CancellationTokenSource();
        var cycle = service.RunCycleAsync(host.Token);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await host.CancelAsync();
        release.Release();
        await cycle.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(store.Peek("col-1")!.CurrentRunId);

        // Clearing the token is what makes the next claim mint a new one, so the next cycle is a different
        // cycle and work keyed on the run id does not collide with this one.
        store.MakeDue("col-1");
        await service.RunCycleAsync(CancellationToken.None);
        Assert.Equal(2, runner.Dispatched.Count);
        Assert.NotEqual(runner.Dispatched[0].RunId, runner.Dispatched[1].RunId);
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
