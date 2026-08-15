using Dapper;
using Freeboard.Core.Authz;
using Freeboard.Persistence.Auth;
using Freeboard.Persistence.System;
using Freeboard.TestInfrastructure;
using MySqlConnector;

namespace Freeboard.Persistence.Tests;

/// <summary>
/// Races an organisation delete against every app write that can create a reference to that organisation,
/// against a real MySQL discovered via FREEBOARD_TEST_DB. Each test SKIPS cleanly when the env var is
/// absent.
///
/// The racing write commits at an exact statement boundary of the delete, driven by the counted command
/// hook rather than a sleep, and the delete is raced at every boundary it has. What is asserted is the
/// PAIR of outcomes, never which side won: both orders are legal, so a test that demanded one would pin
/// timing rather than the guarantee. What is forbidden is both sides succeeding, and any orphan left
/// behind - a scope naming a subject that is gone, or an asset naming a parent that is gone. It is also a
/// failure for either side to throw a MySqlException: a lock failure the store did not map reaches the
/// endpoint as an unreachable store, which is exactly what the mapping exists to prevent.
///
/// Every run re-seeds the fixture, because BOTH sides are destructive: the measuring run deletes the
/// organisation outright, a writer-wins run leaves a committed reference under it, and a delete-wins run
/// leaves it gone. Without the reset, every run after the first would race nothing and the "both outcomes
/// occur" assertion could pass by fixture exhaustion instead of by the race. The reset runs BEFORE Arm,
/// which zeroes the command counter.
///
/// The racing session bounds its own lock wait. Once the delete holds the row, the racing writer blocks -
/// on a transaction that is itself suspended inside the hook waiting for that writer, so nothing releases
/// the lock. A low session innodb_lock_wait_timeout is what breaks that: the server refuses the writer
/// with the lock-wait timeout the store must map, and the delete proceeds. The hook's own cap is a
/// diagnostic backstop, not the mechanism.
///
/// Boundary 2 is the statement-order guard, and it is asserted by name. The locking read is the delete's
/// first statement, so boundary 2 is the first interleaving where the delete already holds the row.
/// Boundary 1 cannot serve: with no command issued the delete has taken no read view, so even a delete
/// with no locking read at all sees the racing commit and refuses there.
///
/// The two role-assignment cases instrument OPPOSITE sides of the race because they need different
/// endings. The lock-wait timeout needs the delete held open, so the delete is hooked and the assign
/// blocks. The foreign-key failure needs the delete COMMITTED first, which the racing session can never
/// reach - it times out before the delete finishes - so the ASSIGN is hooked instead and the delete runs
/// to completion inside the hook.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class OrganisationDeleteConcurrencyTests
{
    private const string OrgId = "org-a";
    private const string ChildId = "dept-child";
    private const string StandardId = "std-a";
    private const string ScopeId = "sc-1";
    private const string UserId = "user-1";

    // The locking read is the delete's first statement, so this is the first interleaving at which the
    // delete already holds the organisation's row.
    private const int LockedBoundary = 2;

    // The assignment's INSERT. It issues exactly three plain reads first - the role scope, the user, the
    // organisation - and transactions are not counted. Arming any earlier is green either way while only
    // sometimes running the mapping: at READ COMMITTED an arm at 3 makes the organisation-exists read
    // itself see the row gone, so the method refuses from its own guard and never reaches the INSERT.
    private const int AssignInsertOrdinal = 4;

    // The racing session gives up here. Long enough that an UNBLOCKED write never hits it, short enough
    // that a whole sweep stays quick.
    private const int LockWaitSeconds = 3;

    // Comfortably above the racing session's own bound, so an expiry here means something else hung.
    private static readonly TimeSpan InterleaveCap = TimeSpan.FromSeconds(20);

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AScopeWriteRacingTheDeleteNeverBothSucceed()
    {
        await using var db = await SeedAsync();
        var runs = await RaceEveryBoundaryAsync(db, ScopeWrite, "scope write");

        AssertBothOutcomesOccur(runs, "scope write");
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AChildOrganisationWriteRacingTheDeleteNeverBothSucceed()
    {
        await using var db = await SeedAsync();
        var runs = await RaceEveryBoundaryAsync(db, ChildWrite, "child organisation write");

        AssertBothOutcomesOccur(runs, "child organisation write");
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task TheBoundaryAfterTheLockingReadRefusesAScopeWrite()
    {
        await using var db = await SeedAsync();
        var run = await RaceAtAsync(db, LockedBoundary, ScopeWrite);

        AssertTheLockHeld(run, "scope write");
        await AssertNoOrphansAsync(db, $"racing the scope write at boundary {LockedBoundary}");
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task TheBoundaryAfterTheLockingReadRefusesAChildOrganisationWrite()
    {
        await using var db = await SeedAsync();
        var run = await RaceAtAsync(db, LockedBoundary, ChildWrite);

        AssertTheLockHeld(run, "child organisation write");
        await AssertNoOrphansAsync(db, $"racing the child organisation write at boundary {LockedBoundary}");
    }

    // The plain guard, unweakened by the locking read: a reference that is already committed when the
    // delete starts still refuses it.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AReferenceCommittedBeforeTheDeleteRefusesIt()
    {
        await using var db = await SeedAsync();
        var store = new MySqlComplianceWriteStore(db.ConnectionFactory);

        await ResetAsync(db);
        Assert.True((await ScopeWrite(store, CancellationToken.None)).Ok);
        Assert.False((await store.DeleteOrganisationAsync(OrgId)).Ok);

        await ResetAsync(db);
        Assert.True((await ChildWrite(store, CancellationToken.None)).Ok);
        Assert.False((await store.DeleteOrganisationAsync(OrgId)).Ok);

        await AssertNoOrphansAsync(db, "the sequential reference-then-delete cases");
    }

    // The tail the raced sweep cannot reach: there the blocked writer times out rather than waiting for
    // the delete to commit, so it never resumes to find the organisation gone.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AReferenceWrittenAfterACommittedDeleteIsRefused()
    {
        await using var db = await SeedAsync();
        var store = new MySqlComplianceWriteStore(db.ConnectionFactory);

        await ResetAsync(db);
        Assert.True((await store.DeleteOrganisationAsync(OrgId)).Ok);
        Assert.False((await ScopeWrite(store, CancellationToken.None)).Ok);

        await ResetAsync(db);
        Assert.True((await store.DeleteOrganisationAsync(OrgId)).Ok);
        Assert.False((await ChildWrite(store, CancellationToken.None)).Ok);

        await AssertNoOrphansAsync(db, "the sequential delete-then-reference cases");
    }

    // The assignment's INSERT takes a shared foreign-key lock on the organisation's asset row, so it
    // blocks behind the delete and its own session gives up first. That must come back as a conflict
    // RESULT; unmapped it escapes the store as a MySqlException, and the endpoint answers a bare 500.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AnAssignmentBlockedByTheDeleteReturnsAConflict()
    {
        await using var db = await SeedAsync();
        var hooked = new InstrumentedConnectionFactory(db.ConnectionFactory);
        var deleteStore = new MySqlComplianceWriteStore(hooked);
        var authz = new MySqlAuthzAdministrationStore(
            new LockWaitConnectionFactory(db.ConnectionFactory, LockWaitSeconds), new UlidFactory());

        await ResetAsync(db);
        var assign = new Attempt<AuthzWriteResult>();
        hooked.Arm(
            LockedBoundary,
            ct => assign.RunAsync(() => authz.AssignOrganisationRoleAsync(UserId, AuthzRoles.ComplianceReader, OrgId, ct)),
            InterleaveCap);
        var delete = await Attempt<WriteResult>.OfAsync(() => deleteStore.DeleteOrganisationAsync(OrgId));

        AssertNoFault(assign, "assignment");
        AssertNoFault(delete, "delete");
        Assert.True(delete.Result!.Ok, $"The delete failed while the assignment waited: {delete.Result.Error}");
        Assert.Equal(AuthzWriteStatus.Conflict, assign.Result!.Status);
    }

    // The shape production produces: the delete COMMITS while the assignment is between its plain
    // organisation-exists read and its INSERT, so the insert fails the foreign key instead of timing out.
    // The parent row is gone for good, so the answer must be invalid, not a retryable conflict.
    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AnAssignmentInsertingAfterACommittedDeleteReturnsInvalid()
    {
        await using var db = await SeedAsync();
        var hooked = new InstrumentedConnectionFactory(db.ConnectionFactory);
        var authz = new MySqlAuthzAdministrationStore(hooked, new UlidFactory());

        // The armed delete is meant to RUN, not block, so it opens from the uninstrumented factory with
        // no lock-wait decorator. The assignment holds no locks at its INSERT: the three reads ahead of
        // it are plain.
        var deleteStore = new MySqlComplianceWriteStore(db.ConnectionFactory);

        await ResetAsync(db);
        var delete = new Attempt<WriteResult>();
        hooked.Arm(AssignInsertOrdinal, ct => delete.RunAsync(() => deleteStore.DeleteOrganisationAsync(OrgId)), InterleaveCap);
        var assign = await Attempt<AuthzWriteResult>.OfAsync(
            () => authz.AssignOrganisationRoleAsync(UserId, AuthzRoles.ComplianceReader, OrgId));

        AssertNoFault(delete, "delete");
        AssertNoFault(assign, "assignment");
        Assert.True(delete.Result!.Ok, $"The interleaved delete failed: {delete.Result.Error}");
        Assert.True(
            hooked.CommandsExecuted >= AssignInsertOrdinal,
            $"The assignment issued only {hooked.CommandsExecuted} statements, so its INSERT never ran "
            + "after the delete and the foreign-key mapping was not exercised.");
        Assert.Equal(AuthzWriteStatus.Invalid, assign.Result!.Status);
    }

    #region the race

    /// <summary>One raced run: the boundary, and what each side returned or threw.</summary>
    private sealed record Run(int Boundary, Attempt<WriteResult> Delete, Attempt<WriteResult> Racing);

    /// <summary>
    /// Measures the delete's statement count from an unraced run, then races <paramref name="write"/> at
    /// every boundary it has. The count is measured rather than written down, so a delete that grows a
    /// statement still races every boundary.
    /// </summary>
    private static async Task<List<Run>> RaceEveryBoundaryAsync(
        MySqlTestDatabase db,
        Func<MySqlComplianceWriteStore, CancellationToken, Task<WriteResult>> write,
        string racer)
    {
        var hooked = new InstrumentedConnectionFactory(db.ConnectionFactory);
        var deleteStore = new MySqlComplianceWriteStore(hooked);

        await ResetAsync(db);
        hooked.Arm();
        Assert.True((await deleteStore.DeleteOrganisationAsync(OrgId)).Ok);
        var statements = hooked.CommandsExecuted;
        Assert.True(statements > 1, $"The delete cost {statements} statement(s), so it has no boundary to race.");

        var runs = new List<Run>();
        for (var boundary = 1; boundary <= statements; boundary++)
        {
            var run = await RaceOnceAsync(db, hooked, deleteStore, boundary, write);
            AssertAtMostOneTookEffect(run, racer);
            await AssertNoOrphansAsync(db, $"racing the {racer} at boundary {boundary}");
            runs.Add(run);
        }

        return runs;
    }

    private static async Task<Run> RaceAtAsync(
        MySqlTestDatabase db, int boundary, Func<MySqlComplianceWriteStore, CancellationToken, Task<WriteResult>> write)
    {
        var hooked = new InstrumentedConnectionFactory(db.ConnectionFactory);
        return await RaceOnceAsync(db, hooked, new MySqlComplianceWriteStore(hooked), boundary, write);
    }

    // Returns the run rather than asserting on it: each caller decides what the pair must be, and the
    // orphan check runs against the state this run left, before the next reset wipes it.
    private static async Task<Run> RaceOnceAsync(
        MySqlTestDatabase db,
        InstrumentedConnectionFactory hooked,
        MySqlComplianceWriteStore deleteStore,
        int boundary,
        Func<MySqlComplianceWriteStore, CancellationToken, Task<WriteResult>> write)
    {
        // The racing writer must be able to give up: the delete is suspended inside the hook while it
        // runs, so nothing else can release the row it waits for.
        var racingStore = new MySqlComplianceWriteStore(
            new LockWaitConnectionFactory(db.ConnectionFactory, LockWaitSeconds));

        await ResetAsync(db);
        var racing = new Attempt<WriteResult>();
        hooked.Arm(boundary, ct => racing.RunAsync(() => write(racingStore, ct)), InterleaveCap);
        var delete = await Attempt<WriteResult>.OfAsync(() => deleteStore.DeleteOrganisationAsync(OrgId));

        // The hook fires immediately before the Nth statement, so a run that never reached it raced
        // nothing and proves nothing.
        Assert.True(
            hooked.CommandsExecuted >= boundary,
            $"The run raced at boundary {boundary} issued only {hooked.CommandsExecuted} statements, so "
            + "the racing write never landed inside it.");

        return new Run(boundary, delete, racing);
    }

    private static void AssertAtMostOneTookEffect(Run run, string racer)
    {
        AssertNeitherThrew(run, racer);
        Assert.False(
            run.Delete.Result!.Ok && run.Racing.Result!.Ok,
            $"Racing the {racer} before statement {run.Boundary} of the delete let BOTH take effect, so "
            + "the delete removed an organisation a committed write references.");
    }

    private static void AssertTheLockHeld(Run run, string racer)
    {
        AssertNeitherThrew(run, racer);
        Assert.True(
            run.Delete.Result!.Ok && !run.Racing.Result!.Ok,
            $"Racing the {racer} at boundary {run.Boundary} - the first boundary after the delete's "
            + "locking read - must find the delete already holding the organisation's row, so the write "
            + $"is refused and the delete succeeds. Got delete={Describe(run.Delete.Result)}, "
            + $"{racer}={Describe(run.Racing.Result)}. Move a plain read in front of the locking read and "
            + "this boundary finds the delete holding nothing: the write commits, the delete's later "
            + "count answers from a read view that predates it, and both succeed.");
    }

    private static void AssertNeitherThrew(Run run, string racer)
    {
        AssertNoFault(run.Delete, $"delete raced at boundary {run.Boundary}");
        AssertNoFault(run.Racing, $"{racer} raced at boundary {run.Boundary}");
        Assert.True(run.Racing.Result is not null, $"The {racer} raced at boundary {run.Boundary} never ran.");
    }

    // A lock failure the store does not map to a result escapes as a MySqlException and is answered as an
    // unreachable store, which is untrue and points the caller away from retrying.
    private static void AssertNoFault<T>(Attempt<T> attempt, string side) =>
        Assert.True(
            attempt.Fault is null,
            $"The {side} threw instead of returning a result: {attempt.Fault}");

    private static void AssertBothOutcomesOccur(List<Run> runs, string racer)
    {
        // Without this the never-both check would hold on a fixture where the race never happens.
        Assert.True(runs.Exists(r => r.Delete.Result!.Ok), "No boundary let the delete win.");
        Assert.True(runs.Exists(r => r.Racing.Result!.Ok), $"No boundary let the {racer} win.");
    }

    private static string Describe(WriteResult result) => result.Ok ? "success" : result.Error!;

    /// <summary>Captures what one side of the race returned, or the exception it threw instead.</summary>
    private sealed class Attempt<T>
    {
        public T? Result { get; private set; }

        public Exception? Fault { get; private set; }

        public static async Task<Attempt<T>> OfAsync(Func<Task<T>> run)
        {
            var attempt = new Attempt<T>();
            await attempt.RunAsync(run);
            return attempt;
        }

        public async Task RunAsync(Func<Task<T>> run)
        {
            try
            {
                Result = await run();
            }
            catch (Exception ex)
            {
                Fault = ex;
            }
        }
    }

    #endregion

    #region the two racing writes

    private static Task<WriteResult> ScopeWrite(MySqlComplianceWriteStore store, CancellationToken ct) =>
        store.UpsertScopeDispositionAsync(ScopeId, "Scope", OrgId, StandardId, "In", cancellationToken: ct);

    private static Task<WriteResult> ChildWrite(MySqlComplianceWriteStore store, CancellationToken ct) =>
        store.UpsertOrganisationAsync(ChildId, "Child", "Department", OrgId, cancellationToken: ct);

    #endregion

    #region the fixture

    private static async Task<MySqlTestDatabase> SeedAsync()
    {
        var db = await MySqlTestDatabase.TryCreateAsync();
        Skip.If(db is null, $"{MySqlTestDatabase.EnvVar} not set; skipping MySQL integration test.");
        await new MySqlMigrationRunner(db!.ConnectionFactory, typeof(IMigrationRunner).Assembly).ApplyPendingAsync();

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // The standard is the scope write's target. The user is the role assignment's: without it the
        // assign stops at its own user-exists check and never reaches the foreign-key path.
        await conn.ExecuteAsync(
            "INSERT INTO standards (id, api_version, title, created_at, updated_at) "
            + "VALUES (@Id, 'v1', 'Standard', NOW(6), NOW(6));",
            new { Id = StandardId });
        await conn.ExecuteAsync(
            "INSERT INTO users (id, email, email_normalized, name, global_role, enabled, force_password_reset, "
            + "mfa_enabled, created_at, updated_at) "
            + "VALUES (@Id, 'u@example.com', 'u@example.com', 'U', 'member', 1, 0, 0, NOW(6), NOW(6));",
            new { Id = UserId });

        // No role is seeded: migration 010 already seeds the organisation-scoped roles these tests use.
        return db;
    }

    /// <summary>
    /// Puts the fixture back to the pre-race state: the organisation present, with no scope, no child,
    /// and no role assignment naming it. Runs on the uninstrumented factory and always BEFORE Arm, since
    /// Arm zeroes the command counter and a reset issued through the hook would shift every boundary.
    /// </summary>
    private static async Task ResetAsync(MySqlTestDatabase db)
    {
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("DELETE FROM scopes WHERE subject_id = @Id;", new { Id = OrgId });
        await conn.ExecuteAsync(
            "DELETE FROM authz_organisation_role_assignments WHERE organisation_id = @Id;", new { Id = OrgId });
        await conn.ExecuteAsync("DELETE FROM assets WHERE parent = @Id;", new { Id = OrgId });
        await conn.ExecuteAsync(
            "INSERT INTO assets (id, type, source, api_version, title, created_at, updated_at) "
            + "VALUES (@Id, 'Company', 'declared', 'v1', 'Org A', NOW(6), NOW(6)) "
            + "ON DUPLICATE KEY UPDATE updated_at = VALUES(updated_at);",
            new { Id = OrgId });
    }

    private static async Task AssertNoOrphansAsync(MySqlTestDatabase db, string what)
    {
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var orphanScopes = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM scopes s LEFT JOIN assets a ON a.id = s.subject_id WHERE a.id IS NULL;");
        Assert.True(orphanScopes == 0, $"After {what}, {orphanScopes} scope(s) name a subject that no longer exists.");

        var orphanChildren = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM assets c LEFT JOIN assets p ON p.id = c.parent "
            + "WHERE c.parent IS NOT NULL AND p.id IS NULL;");
        Assert.True(orphanChildren == 0, $"After {what}, {orphanChildren} asset(s) name a parent that no longer exists.");
    }

    #endregion
}
