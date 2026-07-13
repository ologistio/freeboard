using Dapper;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
using Freeboard.Persistence.System;
using Freeboard.TestInfrastructure;
using MySqlConnector;

namespace Freeboard.Persistence.Tests;

/// <summary>
/// Integration tests for the asset schema (migration 017) and the read/write store pair against a real
/// MySQL discovered via FREEBOARD_TEST_DB. Each test SKIPS cleanly when the env var is absent.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class AssetIntegrationTests
{
    private const string Uuid1 = "550e8400-e29b-41d4-a716-446655440000";
    private const string Uuid2 = "9b2f4c1e-1a2b-4c3d-8e5f-0123456789ab";

    private static async Task<MySqlTestDatabase> RequireDbAsync()
    {
        var db = await MySqlTestDatabase.TryCreateAsync();
        Skip.If(db is null, $"{MySqlTestDatabase.EnvVar} not set; skipping MySQL integration test.");
        return db!;
    }

    private static async Task MigrateAsync(MySqlTestDatabase db) =>
        await new MySqlMigrationRunner(db.ConnectionFactory, typeof(IMigrationRunner).Assembly).ApplyPendingAsync();

    private static (MySqlAssetStore Store, MySqlAssetWriteStore Writes) Stores(MySqlTestDatabase db) =>
        (new MySqlAssetStore(db.ConnectionFactory), new MySqlAssetWriteStore(db.ConnectionFactory, new UlidFactory()));

    private static NewMachineObservation Obs(
        string org, string source, string externalId,
        string? serial = null, string? hostUuid = null, string? hostname = null) =>
        new(org, source, externalId, serial, hostUuid, hostname);

    private static void AssertAssetUnchanged(AssetRow before, AssetRow? after)
    {
        Assert.NotNull(after);
        Assert.Equal(before.State, after!.State);
        Assert.Equal(before.RetiredAt, after.RetiredAt);
        Assert.Equal(before.Hostname, after.Hostname);
        Assert.Equal(before.IdentityValue, after.IdentityValue);
    }

    private static Task<int> SourceCountAsync(MySqlTestDatabase db, string assetId) =>
        QueryAsync(db, conn => conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM asset_source WHERE asset_id = @Id;", new { Id = assetId }));

    private static async Task<T> QueryAsync<T>(MySqlTestDatabase db, Func<MySqlConnection, Task<T>> query)
    {
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        return await query(conn);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task MigrationCreatesTablesUniqueKeysAndFk()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        var tables = (await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE();"))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("asset", tables);
        Assert.Contains("asset_source", tables);

        var identityKey = (await conn.QueryAsync<string>(
            "SELECT column_name FROM information_schema.statistics "
            + "WHERE table_schema = DATABASE() AND table_name = 'asset' "
            + "AND index_name = 'uq_asset_org_identity' AND non_unique = 0 ORDER BY seq_in_index;")).ToArray();
        Assert.Equal(["organisation_id", "identity_kind", "identity_value"], identityKey);

        var sourceKey = (await conn.QueryAsync<string>(
            "SELECT column_name FROM information_schema.statistics "
            + "WHERE table_schema = DATABASE() AND table_name = 'asset_source' "
            + "AND index_name = 'uq_asset_source_org_source_external' AND non_unique = 0 ORDER BY seq_in_index;")).ToArray();
        Assert.Equal(["organisation_id", "source", "external_id"], sourceKey);

        // The composite FK binds organisation into the internal reference.
        var fkCols = (await conn.QueryAsync<string>(
            "SELECT column_name FROM information_schema.key_column_usage "
            + "WHERE table_schema = DATABASE() AND table_name = 'asset_source' "
            + "AND constraint_name = 'fk_asset_source_asset' AND referenced_table_name = 'asset' "
            + "ORDER BY ordinal_position;")).ToArray();
        Assert.Equal(["asset_id", "organisation_id"], fkCols);

        // organisation_id on asset carries no foreign key (scalar reference).
        var assetFks = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.key_column_usage "
            + "WHERE table_schema = DATABASE() AND table_name = 'asset' AND referenced_table_name IS NOT NULL;");
        Assert.Equal(0, assetFks);

        // Mutable tables: no append-only triggers.
        var triggers = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.triggers WHERE trigger_schema = DATABASE() "
            + "AND event_object_table IN ('asset', 'asset_source');");
        Assert.Equal(0, triggers);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task MigrationEnforcesCollationPrecisionAndDeleteRule()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // Identity, kind, state, and source tokens are utf8mb4_bin so the org-scoped unique keys compare by
        // exact bytes and never fold case (e.g. 'fleetdm' vs 'FleetDM').
        async Task AssertBinCollation(string table, string column)
        {
            var collation = await conn.ExecuteScalarAsync<string>(
                "SELECT collation_name FROM information_schema.columns "
                + "WHERE table_schema = DATABASE() AND table_name = @Table AND column_name = @Column;",
                new { Table = table, Column = column });
            Assert.Equal("utf8mb4_bin", collation);
        }

        await AssertBinCollation("asset", "identity_kind");
        await AssertBinCollation("asset", "identity_value");
        await AssertBinCollation("asset", "kind");
        await AssertBinCollation("asset", "state");
        await AssertBinCollation("asset_source", "source");
        await AssertBinCollation("asset_source", "external_id");

        // The (id, organisation_id) unique key backs the referenced side of the composite foreign key.
        var idOrgKey = (await conn.QueryAsync<string>(
            "SELECT column_name FROM information_schema.statistics "
            + "WHERE table_schema = DATABASE() AND table_name = 'asset' "
            + "AND index_name = 'uq_asset_id_org' AND non_unique = 0 ORDER BY seq_in_index;")).ToArray();
        Assert.Equal(["id", "organisation_id"], idOrgKey);

        // An asset with attached sources is never hard-deleted, so the foreign key restricts deletes.
        var deleteRule = await conn.ExecuteScalarAsync<string>(
            "SELECT delete_rule FROM information_schema.referential_constraints "
            + "WHERE constraint_schema = DATABASE() AND constraint_name = 'fk_asset_source_asset';");
        Assert.Equal("RESTRICT", deleteRule);

        // Microsecond timestamp precision: DATETIME(6) on every recorded time.
        async Task AssertMicrosecond(string table, string column)
        {
            var precision = await conn.ExecuteScalarAsync<long>(
                "SELECT datetime_precision FROM information_schema.columns "
                + "WHERE table_schema = DATABASE() AND table_name = @Table AND column_name = @Column;",
                new { Table = table, Column = column });
            Assert.Equal(6, precision);
        }

        await AssertMicrosecond("asset", "first_seen_at");
        await AssertMicrosecond("asset", "last_seen_at");
        await AssertMicrosecond("asset", "retired_at");
        await AssertMicrosecond("asset", "created_at");
        await AssertMicrosecond("asset_source", "first_seen_at");
        await AssertMicrosecond("asset_source", "last_seen_at");
        await AssertMicrosecond("asset_source", "created_at");
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task CreateAndLookupBySourceAndIdentity()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var (store, writes) = Stores(db);

        var result = await writes.UpsertMachineFromSourceAsync(
            Obs("org-a", "fleetdm", "host-1", serial: "SN-1", hostname: "laptop-1"));
        Assert.Equal(AssetUpsertStatus.Created, result.Status);
        Assert.NotNull(result.AssetId);

        var bySource = await store.GetBySourceAsync("org-a", "fleetdm", "host-1");
        Assert.NotNull(bySource);
        Assert.Equal(result.AssetId, bySource!.Id);
        Assert.Equal("Machine", bySource.Kind);
        Assert.Equal("Serial", bySource.IdentityKind);
        Assert.Equal("SN-1", bySource.IdentityValue);
        Assert.Equal("Seen", bySource.State);
        Assert.Equal("laptop-1", bySource.Hostname);

        var byIdentity = await store.GetByIdentityAsync("org-a", "Serial", "SN-1");
        Assert.Equal(result.AssetId, byIdentity!.Id);

        var byId = await store.GetByIdAsync("org-a", result.AssetId!);
        Assert.Equal("SN-1", byId!.IdentityValue);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task TwoSourcesSameSerialResolveToOneAssetWithStableId()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var (store, writes) = Stores(db);

        var first = await writes.UpsertMachineFromSourceAsync(Obs("org-a", "fleetdm", "host-1", serial: "SN-9"));
        var second = await writes.UpsertMachineFromSourceAsync(Obs("org-a", "mdm", "device-7", serial: " sn-9 "));

        Assert.Equal(AssetUpsertStatus.Created, first.Status);
        Assert.Equal(AssetUpsertStatus.Updated, second.Status);
        Assert.Equal(first.AssetId, second.AssetId);
        Assert.Equal(2, await SourceCountAsync(db, first.AssetId!));

        // Both sources resolve to the one asset.
        Assert.Equal(first.AssetId, (await store.GetBySourceAsync("org-a", "fleetdm", "host-1"))!.Id);
        Assert.Equal(first.AssetId, (await store.GetBySourceAsync("org-a", "mdm", "device-7"))!.Id);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task TwoSourcesSameUuidResolveToOne()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var (store, writes) = Stores(db);

        var first = await writes.UpsertMachineFromSourceAsync(Obs("org-a", "fleetdm", "host-1", hostUuid: Uuid1));
        var second = await writes.UpsertMachineFromSourceAsync(
            Obs("org-a", "mdm", "device-7", hostUuid: Uuid1.ToUpperInvariant()));

        Assert.Equal(first.AssetId, second.AssetId);
        var asset = await store.GetByIdAsync("org-a", first.AssetId!);
        Assert.Equal("HostUuid", asset!.IdentityKind);
        Assert.Equal(Uuid1, asset.IdentityValue);
        Assert.Equal(2, await SourceCountAsync(db, first.AssetId!));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SameIdentityInTwoOrgsYieldsTwoAssetsAndNoCrossOrgRead()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var (store, writes) = Stores(db);

        var a = await writes.UpsertMachineFromSourceAsync(Obs("org-a", "fleetdm", "host-1", serial: "SN-1"));
        var b = await writes.UpsertMachineFromSourceAsync(Obs("org-b", "fleetdm", "host-1", serial: "SN-1"));

        Assert.NotEqual(a.AssetId, b.AssetId);
        // No read crosses the org boundary: a by-id read for the wrong org returns nothing, and each
        // identity/source lookup returns that org's own asset, never the other's.
        Assert.Null(await store.GetByIdAsync("org-b", a.AssetId!));
        Assert.Equal(b.AssetId, (await store.GetByIdentityAsync("org-b", "Serial", "SN-1"))!.Id);
        Assert.Equal(a.AssetId, (await store.GetByIdentityAsync("org-a", "Serial", "SN-1"))!.Id);
        Assert.Equal(b.AssetId, (await store.GetBySourceAsync("org-b", "fleetdm", "host-1"))!.Id);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ObservationWithNoIdentityIsInvalidAndWritesNothing()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var (store, writes) = Stores(db);

        var result = await writes.UpsertMachineFromSourceAsync(Obs("org-a", "fleetdm", "host-1", serial: "  "));
        Assert.Equal(AssetUpsertStatus.Invalid, result.Status);
        Assert.Null(result.AssetId);
        Assert.Null(await store.GetBySourceAsync("org-a", "fleetdm", "host-1"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task PlaceholderSerialWithUsableUuidResolvesByUuid()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var (store, writes) = Stores(db);

        var result = await writes.UpsertMachineFromSourceAsync(
            Obs("org-a", "fleetdm", "host-1", serial: "To be filled by O.E.M.", hostUuid: Uuid1));
        Assert.Equal(AssetUpsertStatus.Created, result.Status);

        var asset = await store.GetByIdAsync("org-a", result.AssetId!);
        Assert.Equal("HostUuid", asset!.IdentityKind);
        Assert.Equal(Uuid1, asset.IdentityValue);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task RetireIsStateChangeAndRowPersists()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var (store, writes) = Stores(db);

        var created = await writes.UpsertMachineFromSourceAsync(Obs("org-a", "fleetdm", "host-1", serial: "SN-1"));
        var retire = await writes.RetireAsync("org-a", created.AssetId!);
        Assert.True(retire.Ok, retire.Error);

        var asset = await store.GetByIdAsync("org-a", created.AssetId!);
        Assert.Equal("Retired", asset!.State);
        Assert.NotNull(asset.RetiredAt);
        Assert.Equal(1, await SourceCountAsync(db, created.AssetId!));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ReObservingRetiredMachineReturnsItToSeen()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var (store, writes) = Stores(db);

        var created = await writes.UpsertMachineFromSourceAsync(Obs("org-a", "fleetdm", "host-1", serial: "SN-1"));
        Assert.True((await writes.RetireAsync("org-a", created.AssetId!)).Ok);

        var reobserved = await writes.UpsertMachineFromSourceAsync(Obs("org-a", "fleetdm", "host-1", serial: "SN-1"));
        Assert.Equal(AssetUpsertStatus.Updated, reobserved.Status);
        Assert.Equal(created.AssetId, reobserved.AssetId);

        var asset = await store.GetByIdAsync("org-a", created.AssetId!);
        Assert.Equal("Seen", asset!.State);
        Assert.Null(asset.RetiredAt);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SourceRelinkingToDifferentAssetReturnsConflictAndChangesNothing()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var (store, writes) = Stores(db);

        // (fleetdm, host-1) first points at asset X (serial SN-1, hostname alpha).
        var x = await writes.UpsertMachineFromSourceAsync(
            Obs("org-a", "fleetdm", "host-1", serial: "SN-1", hostname: "alpha"));
        // A different source creates asset Y (serial SN-2, hostname beta), then Y is retired to give it a
        // distinct state and retired_at from X.
        var y = await writes.UpsertMachineFromSourceAsync(
            Obs("org-a", "mdm", "device-7", serial: "SN-2", hostname: "beta"));
        Assert.NotEqual(x.AssetId, y.AssetId);
        Assert.True((await writes.RetireAsync("org-a", y.AssetId!)).Ok);

        var xBefore = await store.GetByIdAsync("org-a", x.AssetId!);
        var yBefore = await store.GetByIdAsync("org-a", y.AssetId!);

        // (fleetdm, host-1) now observes SN-2 (resolving to Y) carrying a hostname unlike Y's. Attaching
        // would relink the source and, mid-transaction, reactivate and rename Y. The upsert must refuse and
        // roll back every row it touched.
        var conflict = await writes.UpsertMachineFromSourceAsync(
            Obs("org-a", "fleetdm", "host-1", serial: "SN-2", hostname: "intruder"));
        Assert.Equal(AssetUpsertStatus.Conflict, conflict.Status);

        // The source still points at X, and both assets are exactly what they were before the refusal:
        // Y keeps its Retired state, retired_at, and hostname; X is likewise untouched.
        Assert.Equal(x.AssetId, (await store.GetBySourceAsync("org-a", "fleetdm", "host-1"))!.Id);
        Assert.Equal(1, await SourceCountAsync(db, y.AssetId!));
        AssertAssetUnchanged(yBefore!, await store.GetByIdAsync("org-a", y.AssetId!));
        AssertAssetUnchanged(xBefore!, await store.GetByIdAsync("org-a", x.AssetId!));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task SerialAndUuidMatchingTwoAssetsReturnsConflict()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var (store, writes) = Stores(db);

        // Asset X keyed on serial SN-1; asset Y keyed on uuid. Both retired and given known hostnames so a
        // stray mutation would show as a state, retired_at, or hostname change.
        var x = await writes.UpsertMachineFromSourceAsync(
            Obs("org-a", "fleetdm", "host-1", serial: "SN-1", hostname: "alpha"));
        var y = await writes.UpsertMachineFromSourceAsync(
            Obs("org-a", "mdm", "device-7", hostUuid: Uuid1, hostname: "beta"));
        Assert.NotEqual(x.AssetId, y.AssetId);
        Assert.True((await writes.RetireAsync("org-a", x.AssetId!)).Ok);
        Assert.True((await writes.RetireAsync("org-a", y.AssetId!)).Ok);

        var xBefore = await store.GetByIdAsync("org-a", x.AssetId!);
        var yBefore = await store.GetByIdAsync("org-a", y.AssetId!);

        // A third source reports BOTH identifiers with a fresh hostname: serial resolves to X, uuid to Y.
        // Merging them is ambiguous, so the upsert refuses and no row is created or altered.
        var conflict = await writes.UpsertMachineFromSourceAsync(
            Obs("org-a", "other", "n-3", serial: "SN-1", hostUuid: Uuid1, hostname: "intruder"));
        Assert.Equal(AssetUpsertStatus.Conflict, conflict.Status);

        // No third source attached, and both assets are byte-for-byte unchanged.
        Assert.Null(await store.GetBySourceAsync("org-a", "other", "n-3"));
        Assert.Equal(1, await SourceCountAsync(db, x.AssetId!));
        Assert.Equal(1, await SourceCountAsync(db, y.AssetId!));
        AssertAssetUnchanged(xBefore!, await store.GetByIdAsync("org-a", x.AssetId!));
        AssertAssetUnchanged(yBefore!, await store.GetByIdAsync("org-a", y.AssetId!));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ConcurrentSameSerialUpsertsResolveToOneAssetId()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var (_, writes) = Stores(db);

        // Eight sources observe the same serial at once; they must converge on one asset id.
        var tasks = Enumerable.Range(0, 8)
            .Select(i => writes.UpsertMachineFromSourceAsync(Obs("org-a", $"src-{i}", $"ext-{i}", serial: "SHARED")))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(
            r.Status is AssetUpsertStatus.Created or AssetUpsertStatus.Updated, r.Error));
        var distinctIds = results.Select(r => r.AssetId).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Single(distinctIds);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task RetireUnknownOrWrongOrgIsNoOp()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var (store, writes) = Stores(db);

        // Unknown id: no-op, no exception.
        Assert.True((await writes.RetireAsync("org-a", "00000000000000000000000001")).Ok);

        // Wrong org: the asset exists under org-a but retiring it as org-b changes nothing.
        var created = await writes.UpsertMachineFromSourceAsync(Obs("org-a", "fleetdm", "host-1", serial: "SN-1"));
        Assert.True((await writes.RetireAsync("org-b", created.AssetId!)).Ok);
        Assert.Equal("Seen", (await store.GetByIdAsync("org-a", created.AssetId!))!.State);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task CrossOrgSourceInsertRejectedByCompositeForeignKey()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var (_, writes) = Stores(db);

        var created = await writes.UpsertMachineFromSourceAsync(Obs("org-a", "fleetdm", "host-1", serial: "SN-1"));

        // The store never produces this mismatch, so exercise it by direct SQL: an asset_source in org-b
        // pointing at an org-a asset must be rejected by the (asset_id, organisation_id) foreign key.
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        var ex = await Assert.ThrowsAsync<MySqlException>(() => conn.ExecuteAsync(
            "INSERT INTO asset_source (id, asset_id, organisation_id, source, external_id, "
            + "observed_serial, observed_host_uuid, first_seen_at, last_seen_at, created_at) "
            + "VALUES ('00000000000000000000000009', @AssetId, 'org-b', 'fleetdm', 'x', NULL, NULL, "
            + "NOW(6), NOW(6), NOW(6));",
            new { created.AssetId }));
        Assert.Equal(MySqlErrorCode.NoReferencedRow2, ex.ErrorCode);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task CaseDifferingSourceTokensCreateDistinctSourceRows()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var (_, writes) = Stores(db);

        // Same serial resolves both to one asset; the differently-cased source tokens must not collide on
        // the (organisation_id, source, external_id) unique key, so two distinct source rows exist.
        var first = await writes.UpsertMachineFromSourceAsync(Obs("org-a", "fleetdm", "e1", serial: "SN-1"));
        var second = await writes.UpsertMachineFromSourceAsync(Obs("org-a", "FleetDM", "e1", serial: "SN-1"));
        Assert.Equal(first.AssetId, second.AssetId);
        Assert.Equal(2, await SourceCountAsync(db, first.AssetId!));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task CaseDifferingIdentityValuesAreDistinctKeys()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        // Core folds serial case, so drive the schema key directly: two asset rows differing only in
        // identity_value case both persist, proving the org-scoped identity unique key compares by exact
        // bytes rather than colliding case-insensitively.
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO asset (id, organisation_id, kind, identity_kind, identity_value, hostname, state, "
            + "first_seen_at, last_seen_at, retired_at, created_at) VALUES "
            + "('00000000000000000000000001', 'org-a', 'Machine', 'Serial', 'ABC', NULL, 'Seen', NOW(6), NOW(6), NULL, NOW(6)),"
            + "('00000000000000000000000002', 'org-a', 'Machine', 'Serial', 'abc', NULL, 'Seen', NOW(6), NOW(6), NULL, NOW(6));");

        var count = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM asset WHERE organisation_id = 'org-a' AND identity_kind = 'Serial';");
        Assert.Equal(2, count);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task Migration017ReplayIsIdempotent()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);

        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();

        // ApplyPendingAsync will not re-run 017, so execute the raw SQL text a second time directly.
        // CREATE TABLE IF NOT EXISTS makes it re-runnable.
        var asm = typeof(IMigrationRunner).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("017_assets.sql", StringComparison.Ordinal));
        await using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        await conn.ExecuteAsync(await reader.ReadToEndAsync());

        var tables = (await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE() "
            + "AND table_name IN ('asset', 'asset_source');")).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("asset", tables);
        Assert.Contains("asset_source", tables);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task OversizedInputReturnsInvalidAndWritesNothing()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var (store, writes) = Stores(db);

        // Each field is one character over its column width. The store must reject up front with Invalid
        // rather than let the oversized value reach MySQL and surface as a data-too-long exception.
        var longOrg = new string('o', 191);        // organisation_id VARCHAR(190)
        var longSource = new string('s', 65);      // source VARCHAR(64)
        var longExternal = new string('e', 191);   // external_id VARCHAR(190)
        var longSerial = new string('N', 191);     // observed_serial VARCHAR(190)
        var longHostname = new string('h', 256);   // hostname VARCHAR(255)

        Assert.Equal(AssetUpsertStatus.Invalid,
            (await writes.UpsertMachineFromSourceAsync(Obs(longOrg, "fleetdm", "e1", serial: "SN-1"))).Status);
        Assert.Equal(AssetUpsertStatus.Invalid,
            (await writes.UpsertMachineFromSourceAsync(Obs("org-a", longSource, "e1", serial: "SN-1"))).Status);
        Assert.Equal(AssetUpsertStatus.Invalid,
            (await writes.UpsertMachineFromSourceAsync(Obs("org-a", "fleetdm", longExternal, serial: "SN-1"))).Status);
        Assert.Equal(AssetUpsertStatus.Invalid,
            (await writes.UpsertMachineFromSourceAsync(Obs("org-a", "fleetdm", "e1", serial: longSerial))).Status);
        Assert.Equal(AssetUpsertStatus.Invalid,
            (await writes.UpsertMachineFromSourceAsync(
                Obs("org-a", "fleetdm", "e1", serial: "SN-1", hostname: longHostname))).Status);

        // The oversized-hostname observation had valid keys, so a leaked write would be visible here.
        Assert.Null(await store.GetBySourceAsync("org-a", "fleetdm", "e1"));
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task StaleObservationDoesNotRegressLastSeenOrState()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var (store, writes) = Stores(db);

        var created = await writes.UpsertMachineFromSourceAsync(
            Obs("org-a", "fleetdm", "host-1", serial: "SN-1", hostname: "current"));

        // Push last_seen_at a day into the future and retire the asset, so any observation the store makes
        // now carries an OLDER timestamp than what is stored - the regression the monotonic guard prevents.
        await using var conn = new MySqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "UPDATE asset SET last_seen_at = DATE_ADD(NOW(6), INTERVAL 1 DAY), state = 'Retired', "
            + "retired_at = NOW(6) WHERE id = @Id;", new { Id = created.AssetId });
        await conn.ExecuteAsync(
            "UPDATE asset_source SET last_seen_at = DATE_ADD(NOW(6), INTERVAL 1 DAY) WHERE asset_id = @Id;",
            new { Id = created.AssetId });

        // Re-observe with a new hostname. The store's timestamp is older than the stored last_seen_at, so the
        // update must not reactivate the machine, clear retired_at, overwrite the hostname, or move last_seen.
        var stale = await writes.UpsertMachineFromSourceAsync(
            Obs("org-a", "fleetdm", "host-1", serial: "SN-1", hostname: "stale"));
        Assert.Equal(AssetUpsertStatus.Updated, stale.Status);

        var asset = await store.GetByIdAsync("org-a", created.AssetId!);
        Assert.Equal("Retired", asset!.State);
        Assert.NotNull(asset.RetiredAt);
        Assert.Equal("current", asset.Hostname);

        var assetLastSeen = await conn.ExecuteScalarAsync<DateTime>(
            "SELECT last_seen_at FROM asset WHERE id = @Id;", new { Id = created.AssetId });
        var sourceLastSeen = await conn.ExecuteScalarAsync<DateTime>(
            "SELECT last_seen_at FROM asset_source WHERE asset_id = @Id;", new { Id = created.AssetId });
        Assert.True(assetLastSeen > DateTime.UtcNow, "asset last_seen_at regressed below the stored future value.");
        Assert.True(sourceLastSeen > DateTime.UtcNow, "asset_source last_seen_at regressed below the stored future value.");
    }
}
