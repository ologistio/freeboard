using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Freeboard.Persistence;

namespace Freeboard.TestInfrastructure;

/// <summary>
/// Decorates an <see cref="IDbConnectionFactory"/> so a test can watch, and cut into, the statements a
/// store issues.
///
/// It does two jobs because both need the same connection and command wrapper. It RECORDS the
/// transactions a store begins, with their isolation level, and the commands it executes, so a test can
/// see that a snapshot spanning several statements runs in one repeatable-read transaction and that a
/// one-statement snapshot runs in none. And it INTERLEAVES: an armed action runs immediately before the
/// Nth command executes, so a competing writer commits at an exactly known point in the middle of a
/// read. That is what makes a concurrency guarantee testable - the interleaving is a counted command
/// hook, with no sleep and no thread timing, so the test is deterministic. Without it a single-threaded
/// test could only assert that MySQL implements repeatable read.
///
/// An armed action that may BLOCK on a lock the hooked transaction holds MUST bound its own wait, by
/// giving its session a low <c>innodb_lock_wait_timeout</c>. The hooked transaction is suspended here
/// until the action settles, so nothing else can release that lock. The factory's own cap does NOT
/// substitute for that: it only turns a hang into a fast failure naming the boundary it happened at.
///
/// The interleaved action MUST open its connections from the UNINSTRUMENTED factory. A connection taken
/// from this one counts its commands here and re-enters the hook.
///
/// The command counter spans every connection this factory opens, so a test that pins an exact Nth
/// statement drives one store read at a time. Transactions the store begins are not commands: the
/// counter sees only the commands a caller creates.
/// </summary>
public sealed class InstrumentedConnectionFactory(IDbConnectionFactory inner) : IDbConnectionFactory
{
    // How long the hook waits for an armed action that named no cap of its own. Set well above any
    // interleaved action's runtime and below MySQL's 50-second default lock wait, so a test that
    // deadlocks against the hooked transaction fails here rather than at the server.
    private static readonly TimeSpan DefaultInterleaveCap = TimeSpan.FromSeconds(30);

    private readonly List<IsolationLevel> transactionsBegun = [];
    private int commandsExecuted;
    private int connectionsOpened;
    private int interleaveBeforeCommand;
    private TimeSpan interleaveCap = DefaultInterleaveCap;
    private Func<CancellationToken, Task>? interleave;

    /// <summary>
    /// Arms the interleave: <paramref name="action"/> runs immediately before the
    /// <paramref name="beforeCommand"/>th command executed from here on, and the count restarts at zero.
    /// Called with no arguments it disarms and restarts the count, which is how a test measures the
    /// statements one read costs before racing each of them in turn, and how it discounts the statements
    /// an app issued while booting.
    ///
    /// <paramref name="cap"/> bounds the wait for the action. An action meant to block states its own
    /// bound here alongside the session lock-wait timeout it sets, so the two do not fight.
    /// </summary>
    public void Arm(int beforeCommand = 0, Func<CancellationToken, Task>? action = null, TimeSpan? cap = null)
    {
        interleave = action;
        interleaveBeforeCommand = beforeCommand;
        interleaveCap = cap ?? DefaultInterleaveCap;
        Interlocked.Exchange(ref commandsExecuted, 0);
    }

    /// <summary>Commands executed through every connection this factory has opened.</summary>
    public int CommandsExecuted => Volatile.Read(ref commandsExecuted);

    /// <summary>Connections opened through this factory.</summary>
    public int ConnectionsOpened => Volatile.Read(ref connectionsOpened);

    /// <summary>The isolation level of each transaction begun, in the order they were begun.</summary>
    public IReadOnlyList<IsolationLevel> TransactionsBegun
    {
        get
        {
            lock (transactionsBegun)
            {
                return transactionsBegun.ToArray();
            }
        }
    }

    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref connectionsOpened);
        return new InstrumentedConnection(await inner.OpenAsync(cancellationToken).ConfigureAwait(false), this);
    }

    private void RecordTransaction(IsolationLevel isolationLevel)
    {
        lock (transactionsBegun)
        {
            transactionsBegun.Add(isolationLevel);
        }
    }

    // Counts the command about to run and, on the Nth, runs the interleaved action first. Awaiting it
    // here is the whole point: the competing writer has settled before the hooked statement reaches the
    // server, so the interleaving point is exact rather than raced. The cap is the diagnostic backstop
    // for an action that never settles, not the mechanism that makes a blocking one give up.
    //
    // On expiry the cap throws, the throw propagates out of the store's execute call, and the await
    // using on the transaction and the connection rolls the hooked transaction back and releases its
    // locks - so that side is left in a defined state. The ABANDONED action is not cancelled. The
    // rollback releases the very lock it was waiting for, so it may still complete (committing a write
    // into a fixture the test has stopped tracking) or may fault, against a database the fixture is
    // about to drop, with nothing observing it. A test that needs the abandoned action's outcome must
    // observe it after the rollback rather than assume it failed.
    private async Task BeforeCommandAsync(CancellationToken cancellationToken)
    {
        var ordinal = Interlocked.Increment(ref commandsExecuted);
        if (interleave is null || ordinal != interleaveBeforeCommand)
        {
            return;
        }

        // WhenAny rather than WaitAsync, so the cap's own message is never put on a TimeoutException the
        // action itself raised.
        var action = interleave(cancellationToken);
        using var expiry = new CancellationTokenSource();
        if (await Task.WhenAny(action, Task.Delay(interleaveCap, expiry.Token)).ConfigureAwait(false) != action)
        {
            throw new TimeoutException(
                $"The action interleaved before command {ordinal} did not settle within "
                + $"{interleaveCap.TotalSeconds:0.###}s. An armed action that can block must bound its own "
                + "wait: the hooked transaction is suspended here and cannot release the locks it holds.");
        }

        await expiry.CancelAsync().ConfigureAwait(false);
        await action.ConfigureAwait(false);
    }

    private sealed class InstrumentedConnection(DbConnection inner, InstrumentedConnectionFactory owner) : DbConnection
    {
        internal Task BeforeCommandAsync(CancellationToken cancellationToken) =>
            owner.BeforeCommandAsync(cancellationToken);

        [AllowNull]
        public override string ConnectionString
        {
            get => inner.ConnectionString;
            set => inner.ConnectionString = value;
        }

        public override string Database => inner.Database;

        public override string DataSource => inner.DataSource;

        public override string ServerVersion => inner.ServerVersion;

        public override ConnectionState State => inner.State;

        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);

        public override void Close() => inner.Close();

        public override Task CloseAsync() => inner.CloseAsync();

        public override void Open() => inner.Open();

        public override Task OpenAsync(CancellationToken cancellationToken) => inner.OpenAsync(cancellationToken);

        // The inner transaction is returned unwrapped: it is what the store hands back to a command, and
        // the inner command it is assigned to belongs to the same inner connection.
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            owner.RecordTransaction(isolationLevel);
            return inner.BeginTransaction(isolationLevel);
        }

        protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
            IsolationLevel isolationLevel, CancellationToken cancellationToken)
        {
            owner.RecordTransaction(isolationLevel);
            return await inner.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        }

        protected override DbCommand CreateDbCommand() => new InstrumentedCommand(inner.CreateCommand(), this);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class InstrumentedCommand(DbCommand inner, InstrumentedConnection owner) : DbCommand
    {
        [AllowNull]
        public override string CommandText
        {
            get => inner.CommandText;
            set => inner.CommandText = value;
        }

        public override int CommandTimeout
        {
            get => inner.CommandTimeout;
            set => inner.CommandTimeout = value;
        }

        public override CommandType CommandType
        {
            get => inner.CommandType;
            set => inner.CommandType = value;
        }

        public override UpdateRowSource UpdatedRowSource
        {
            get => inner.UpdatedRowSource;
            set => inner.UpdatedRowSource = value;
        }

        public override bool DesignTimeVisible
        {
            get => inner.DesignTimeVisible;
            set => inner.DesignTimeVisible = value;
        }

        // The inner command is already bound to the inner connection, so an assignment here is the
        // caller handing back the connection it created the command from and needs no action.
        protected override DbConnection? DbConnection
        {
            get => owner;
            set { }
        }

        protected override DbParameterCollection DbParameterCollection => inner.Parameters;

        protected override DbTransaction? DbTransaction
        {
            get => inner.Transaction;
            set => inner.Transaction = value;
        }

        public override void Cancel() => inner.Cancel();

        public override void Prepare() => inner.Prepare();

        protected override DbParameter CreateDbParameter() => inner.CreateParameter();

        // The synchronous paths are not instrumented rather than silently uncounted: nothing under test
        // reads synchronously, and a caller that started to would otherwise slip past the hook and turn a
        // deterministic interleaving into a test that quietly proves nothing.
        public override int ExecuteNonQuery() => throw NoSynchronousExecution();

        public override object? ExecuteScalar() => throw NoSynchronousExecution();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw NoSynchronousExecution();

        private static NotSupportedException NoSynchronousExecution() =>
            new($"{nameof(InstrumentedConnectionFactory)} instruments the asynchronous execute paths only.");

        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            await owner.BeforeCommandAsync(cancellationToken).ConfigureAwait(false);
            return await inner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            await owner.BeforeCommandAsync(cancellationToken).ConfigureAwait(false);
            return await inner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior, CancellationToken cancellationToken)
        {
            await owner.BeforeCommandAsync(cancellationToken).ConfigureAwait(false);
            return await inner.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
