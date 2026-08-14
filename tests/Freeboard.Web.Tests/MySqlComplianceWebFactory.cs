using Freeboard.Persistence;
using Freeboard.TestInfrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Freeboard.Web.Tests;

/// <summary>
/// The app as <see cref="AuthWebFactory"/> boots it, but serving its compliance reads from a real
/// <see cref="MySqlComplianceStore"/> over a throwaway MySQL database, through a decorator a test can
/// cut into. Everything else stays in memory, so a surface runs its real narrowing over real rows while
/// a competing writer commits at an exactly known statement boundary.
/// </summary>
internal sealed class MySqlComplianceWebFactory : AuthWebFactory
{
    /// <summary>How long the boot read may take before the wait is called a failure rather than a hang.</summary>
    private static readonly TimeSpan BootTimeout = TimeSpan.FromSeconds(30);

    private readonly BootSignallingStore _store;

    public MySqlComplianceWebFactory(IDbConnectionFactory connections)
    {
        Connections = new InstrumentedConnectionFactory(connections);
        _store = new BootSignallingStore(new MySqlComplianceStore(Connections));
    }

    /// <summary>The factory the compliance store reads through. Arm it to race a read.</summary>
    public InstrumentedConnectionFactory Connections { get; }

    /// <summary>
    /// Starts the app and waits for its startup read to COMPLETE, then disarms and zeroes the statement
    /// count so a test arms against its own request alone. Waiting on the read itself rather than on a
    /// statement count is what makes the zeroing safe: the count rises as a statement is dispatched, so a
    /// count-based wait can return with a boot statement still in flight and let it land inside the
    /// measurement the test is about to take.
    /// </summary>
    public async Task BootAsync()
    {
        _ = Services;
        await _store.FirstRead.WaitAsync(BootTimeout);

        // Exact, so a second boot read is a failure here rather than statements silently absorbed into a
        // test's own measured read.
        Assert.Equal(
            [ComplianceReadSet.Collectors | ComplianceReadSet.IntegrationConnections], _store.Reads);
        Connections.Arm();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IComplianceStore>();
            services.AddSingleton<IComplianceStore>(_store);
        });
    }

    /// <summary>
    /// Records the shape of every snapshot served and signals when the first one has finished, so a test
    /// waits for the app's startup read to be over rather than polling the statements it has dispatched.
    ///
    /// <see cref="FakeComplianceStore"/> records something similar and the two do not share the code.
    /// That one ANSWERS from in-memory rows where this one WRAPS the real store, because these tests
    /// exist to run the real SQL, and a fake cannot wrap. What they record also differs: the fake logs
    /// the sets and the served asset list together so the two stay index-aligned, and signals before it
    /// throws on a fault, where this logs the sets alone and signals in a finally so a faulted boot read
    /// still releases the wait. A shared log would carry an asset list this caller never has.
    /// </summary>
    private sealed class BootSignallingStore(MySqlComplianceStore inner) : IComplianceStore
    {
        // The startup read runs off the request path, so the recording list is written from a background
        // thread while a test thread reads it.
        private readonly Lock _gate = new();
        private readonly List<ComplianceReadSet> _reads = [];
        private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the store has finished serving its first snapshot.</summary>
        public Task FirstRead => _firstRead.Task;

        /// <summary>The shape of every snapshot served, in the order the app asked for them.</summary>
        public IReadOnlyList<ComplianceReadSet> Reads
        {
            get
            {
                lock (_gate)
                {
                    return [.. _reads];
                }
            }
        }

        public async Task<ComplianceSnapshot> GetSnapshotAsync(
            ComplianceReadSet sets, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _reads.Add(sets);
            }

            try
            {
                return await inner.GetSnapshotAsync(sets, cancellationToken);
            }
            finally
            {
                // Signalled on the way out, a faulted read included: the startup warning swallows a store
                // failure, so a wait that only ever completed on success would hang to its timeout rather
                // than fail on what the test asserts.
                _firstRead.TrySetResult();
            }
        }

        public Task<ComplianceCounts> GetCountsAsync(CancellationToken cancellationToken = default)
            => inner.GetCountsAsync(cancellationToken);
    }
}
