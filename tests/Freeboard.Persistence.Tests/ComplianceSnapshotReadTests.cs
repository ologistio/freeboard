using System.Data;
using Freeboard.Core.GitOps;
using Freeboard.Persistence.GitOps;
using Freeboard.Persistence.System;
using Freeboard.TestInfrastructure;

namespace Freeboard.Persistence.Tests;

/// <summary>
/// Covers the snapshot read itself against a real MySQL discovered via FREEBOARD_TEST_DB: how many
/// connections and transactions a shape costs, and that naming a set alongside others returns the rows
/// that set returns on its own. Each test SKIPS cleanly when the env var is absent.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class ComplianceSnapshotReadTests
{
    private const ComplianceReadSet EverySet =
        ComplianceReadSet.Assets | ComplianceReadSet.Standards | ComplianceReadSet.Requirements
        | ComplianceReadSet.Controls | ComplianceReadSet.Scopes | ComplianceReadSet.Collectors
        | ComplianceReadSet.IntegrationConnections | ComplianceReadSet.VendorAssurances;

    private static async Task<MySqlTestDatabase> RequireDbAsync()
    {
        var db = await MySqlTestDatabase.TryCreateAsync();
        Skip.If(db is null, $"{MySqlTestDatabase.EnvVar} not set; skipping MySQL integration test.");
        return db!;
    }

    private static Task MigrateAsync(MySqlTestDatabase db) =>
        new MySqlMigrationRunner(db.ConnectionFactory, typeof(IMigrationRunner).Assembly).ApplyPendingAsync();

    // One config populating all eight lists, so a snapshot naming every set has a row of each to return.
    private static GitOpsConfig EveryList() => new()
    {
        Standards = [new Standard { Id = "std-a", ApiVersion = "v1", Title = "T", Version = "1.0", Authority = "Example Authority" }],
        Requirements =
        [
            new Requirement
            {
                Id = "req-a",
                ApiVersion = "v1",
                Title = "T",
                Standard = "std-a",
                Theme = "Theme",
                Statement = "Do the thing.",
                CitationLabel = "Source",
                CitationUrl = "https://example.com/req-a",
            },
        ],
        Controls = [new Control { Id = "ctrl-a", ApiVersion = "v1", Title = "T", MapsTo = ["req-a"], Evaluation = "all" }],
        Assets =
        [
            new Asset { Id = "org-a", ApiVersion = "v1", Title = "T", Type = "Company", Source = "declared" },
            new Asset
            {
                Id = "vendor-a",
                ApiVersion = "v1",
                Title = "T",
                Type = "Vendor",
                Source = "declared",
                Owner = "org-a",
                Assurances = [new Assurance { Standard = "std-a", Expires = "2027-03-27" }],
            },
        ],
        Scopes =
        [
            new Scope
            {
                Id = "scope-a",
                ApiVersion = "v1",
                Title = "T",
                Subject = "org-a",
                Standard = "std-a",
                Disposition = "In",
            },
        ],
        Collectors =
        [
            new Collector
            {
                Id = "coll-a",
                ApiVersion = "v1",
                Title = "T",
                Control = "ctrl-a",
                Type = "manual",
                Frequency = "annual",
                Config = new CollectorConfig { Body = "Attest." },
            },
        ],
        IntegrationConnections =
        [
            new IntegrationConnection
            {
                Id = "conn-a",
                ApiVersion = "v1",
                Title = "T",
                Provider = "fleet",
                BaseUrl = "https://fleet.example.com",
                DiscoveryCadence = "daily",
            },
        ],
    };

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task ASnapshotSpanningTwoStatementsRunsThemInOneRepeatableReadTransaction()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var factory = new InstrumentedConnectionFactory(db.ConnectionFactory);

        await new MySqlComplianceStore(factory)
            .GetSnapshotAsync(ComplianceReadSet.Assets | ComplianceReadSet.Scopes);

        Assert.Equal(1, factory.ConnectionsOpened);
        Assert.Equal(2, factory.CommandsExecuted);
        Assert.Equal([IsolationLevel.RepeatableRead], factory.TransactionsBegun);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AControlsOnlySnapshotIsTwoStatementsSoItTakesTheTransactionToo()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var factory = new InstrumentedConnectionFactory(db.ConnectionFactory);

        // The controls cost the rows plus the control_requirements join, so one named set is still two
        // statements and still has to be pinned to one state.
        await new MySqlComplianceStore(factory).GetSnapshotAsync(ComplianceReadSet.Controls);

        Assert.Equal(2, factory.CommandsExecuted);
        Assert.Equal([IsolationLevel.RepeatableRead], factory.TransactionsBegun);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task AOneStatementSnapshotRunsWithoutATransaction()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        var factory = new InstrumentedConnectionFactory(db.ConnectionFactory);

        // The shape the authorization gate path takes on every request. One statement is already atomic,
        // so a transaction around it would buy nothing and cost a round trip on every gated request.
        await new MySqlComplianceStore(factory).GetSnapshotAsync(ComplianceReadSet.Assets);

        Assert.Equal(1, factory.ConnectionsOpened);
        Assert.Equal(1, factory.CommandsExecuted);
        Assert.Empty(factory.TransactionsBegun);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task EverySetReturnsTheSameRowsInAWideSnapshotAsItDoesAlone()
    {
        await using var db = await RequireDbAsync();
        await MigrateAsync(db);
        await new MySqlGitOpsImporter(db.ConnectionFactory).ImportAsync(EveryList());
        var store = new MySqlComplianceStore(db.ConnectionFactory);

        var wide = await store.GetSnapshotAsync(EverySet);
        Assert.Equal(EverySet, wide.Sets);

        // Each set holds the rows it holds whichever shape names it: widening a snapshot moves the reads
        // into a transaction and must not change a single row of what any of them answer.
        Assert.Equal(["org-a", "vendor-a"], wide.Assets.Select(a => a.Id));
        Assert.Equal(
            Assets(await store.GetSnapshotAsync(ComplianceReadSet.Assets)),
            Assets(wide));

        Assert.Equal(["std-a"], wide.Standards.Select(s => s.Id));
        Assert.Equal((await store.GetSnapshotAsync(ComplianceReadSet.Standards)).Standards, wide.Standards);

        Assert.Equal(["req-a"], wide.Requirements.Select(r => r.Id));
        Assert.Equal((await store.GetSnapshotAsync(ComplianceReadSet.Requirements)).Requirements, wide.Requirements);

        Assert.Equal(["ctrl-a"], wide.Controls.Select(c => c.Id));
        Assert.Equal(["req-a"], Assert.Single(wide.Controls).MapsTo);
        Assert.Equal(
            Controls(await store.GetSnapshotAsync(ComplianceReadSet.Controls)),
            Controls(wide));

        Assert.Equal(["scope-a"], wide.Scopes.Select(s => s.Id));
        Assert.Equal((await store.GetSnapshotAsync(ComplianceReadSet.Scopes)).Scopes, wide.Scopes);

        Assert.Equal(["coll-a"], wide.Collectors.Select(c => c.Id));
        Assert.Equal(
            Collectors(await store.GetSnapshotAsync(ComplianceReadSet.Collectors)),
            Collectors(wide));

        Assert.Equal(["conn-a"], wide.IntegrationConnections.Select(c => c.Id));
        Assert.Equal(
            (await store.GetSnapshotAsync(ComplianceReadSet.IntegrationConnections)).IntegrationConnections,
            wide.IntegrationConnections);

        Assert.Equal(["vendor-a"], wide.VendorAssurances.Select(a => a.VendorId));
        Assert.Equal(
            (await store.GetSnapshotAsync(ComplianceReadSet.VendorAssurances)).VendorAssurances,
            wide.VendorAssurances);
    }

    // The three row types carrying a collection compare by reference under record equality, so they are
    // projected to a comparable form rather than asserted whole.
    private static IEnumerable<string> Assets(ComplianceSnapshot snapshot) =>
        snapshot.Assets.Select(a =>
            $"{a.Id}|{a.Title}|{a.Type}|{a.Source}|{a.State}|{a.Parent}|{a.Owner}|{a.Tier}|{string.Join(',', a.DataClasses)}");

    private static IEnumerable<string> Controls(ComplianceSnapshot snapshot) =>
        snapshot.Controls.Select(c => $"{c.Id}|{c.Title}|{c.Evaluation}|{string.Join(',', c.MapsTo)}");

    private static IEnumerable<string> Collectors(ComplianceSnapshot snapshot) =>
        snapshot.Collectors.Select(c =>
            $"{c.Id}|{c.Title}|{c.Control}|{c.Vendor}|{c.Type}|{c.Provider}|{c.Frequency}|{c.Threshold}"
            + $"|{c.Connection}|{c.Config.Body}|{c.Config.PassMark}"
            + $"|{string.Join(',', c.Config.Fields.Select(f => f.Id))}"
            + $"|{string.Join(',', c.Config.Checks.Select(k => k.Name))}"
            + $"|{string.Join(',', c.Config.Quiz.Select(q => q.Id))}");
}
