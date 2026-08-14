using System.Net;
using System.Text.Json;
using Freeboard.Core.GitOps;
using Freeboard.Persistence.GitOps;
using Freeboard.Persistence.System;
using Freeboard.TestInfrastructure;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// Races the two narrowed compliance surfaces against a whole gitops sync that reparents a vendor from
/// an organisation the caller may read to one it may not, against a real MySQL discovered via
/// FREEBOARD_TEST_DB. Each test SKIPS cleanly when the env var is absent.
///
/// The sync commits at an exact statement boundary in the middle of the read, driven by a counted
/// command hook rather than a sleep, so the interleaving is deterministic and the tests do not race. Each
/// surface is raced at every boundary its read has, and what is asserted is the RESPONSE - the JSON the
/// endpoint returns and the HTML the register renders - so the real accessible-set closure decides
/// readability rather than a rule restated in the fixture.
///
/// The assertion is that the answer is wholly from ONE side of the commit: the vendor, its Out
/// justification, and its certification are present together or absent together. Deliberately NOT that
/// the answer is post-commit - a snapshot opened before the sync legitimately answers from the pre-commit
/// state after it lands, and that answer is correct. What can never be right is a cross pairing.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class ComplianceSnapshotConcurrencyTests
{
    private const string VendorId = "vendor-v";
    private const string ScopeId = "vsc-1";
    private const string ReadableOrg = "org-readable";
    private const string HiddenOrg = "org-hidden";
    private const string Reader = "u1";
    private const string BeforeJustification = "Reviewed while owned by the readable organisation.";
    private const string AfterJustification = "Reassessed after the move to the hidden organisation.";

    // The register renders an expiry against today, so the clock is pinned: a fixture dated against the
    // wall clock changes wording as that clock moves. Both dates sit far enough out to read as a plain
    // expiry, and they render differently, so the certification says which state it came from.
    private const string BeforeExpiryText = "expires Mar 27";
    private const string AfterExpiryText = "expires Sep 1";
    private static readonly DateOnly Today = new(2026, 3, 1);
    private static readonly DateOnly BeforeExpiry = new(2027, 3, 27);
    private static readonly DateOnly AfterExpiry = new(2028, 9, 1);

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task TheScopesResponseAnswersWhollyFromOneSideOfASyncCommit()
    {
        await using var db = await RequireDbAsync();
        var importer = await SeedAsync(db);
        using var factory = Factory(db);
        await factory.BootAsync();
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser(Reader));

        var bodies = await RaceEveryStatementAsync(
            factory, importer, () => client.GetStringAsync("/api/v1/freeboard/scopes"));

        var answers = bodies.Select(VendorScopeJustification).ToList();
        for (var i = 0; i < answers.Count; i++)
        {
            // The response carries the justification only when it carried the scope, and the scope only
            // when the closure admitted the vendor. The After justification exists only in the state
            // where the vendor is not readable, so a response holding it is a decision no state of the
            // database supports.
            Assert.True(
                answers[i] is null or BeforeJustification,
                $"Racing the sync before statement {i + 1} of the read returned the justification from "
                + $"the state where the vendor is not readable: {answers[i]}");
        }

        // Neither outcome may be the only one, or the check above would hold on a fixture that never
        // shows the vendor at all.
        Assert.Contains(answers, a => a == BeforeJustification);
        Assert.Contains(answers, a => a is null);
    }

    [RequiresEnvVarFact(EnvVar = MySqlTestDatabase.EnvVar)]
    public async Task TheVendorRegisterRenderAnswersWhollyFromOneSideOfASyncCommit()
    {
        await using var db = await RequireDbAsync();
        var importer = await SeedAsync(db);
        using var factory = Factory(db);
        await factory.BootAsync();
        var token = factory.SeedSession(AuthWebFactory.MakeUser(Reader));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var pages = await RaceEveryStatementAsync(factory, importer, () => GetRegisterAsync(client, token));

        var rendered = pages.Select((html, i) => AssertRegisterIsFromOneState(html, i + 1)).ToList();

        Assert.Contains(rendered, shown => shown);
        Assert.Contains(rendered, shown => !shown);
    }

    /// <summary>
    /// Asserts one rendered register is wholly from one state, and returns whether the vendor was shown.
    /// The vendor row, its Out justification, and its certification each come from a different table, so
    /// each check fails on its own: the row is the accessible-set decision, the justification is the
    /// scopes read, and the expiry wording is the assurances read.
    /// </summary>
    private static bool AssertRegisterIsFromOneState(string html, int boundary)
    {
        // A store failure renders an in-page notice and an empty register, which would read like a hidden
        // vendor and pass every check below without racing anything.
        Assert.DoesNotContain("The compliance store could not be reached", html, StringComparison.Ordinal);

        var shown = html.Contains($"data-vendor-id=\"{VendorId}\"", StringComparison.Ordinal);
        var state = shown ? "showed" : "hid";

        Assert.True(
            html.Contains(BeforeJustification, StringComparison.Ordinal) == shown,
            $"Racing the sync before statement {boundary} of the render {state} the vendor row while its "
            + "Out justification went the other way.");
        Assert.True(
            html.Contains(BeforeExpiryText, StringComparison.Ordinal) == shown,
            $"Racing the sync before statement {boundary} of the render {state} the vendor row while its "
            + "certification went the other way.");

        // Both belong to the state where the vendor is not readable, so neither may ever render.
        Assert.DoesNotContain(AfterJustification, html, StringComparison.Ordinal);
        Assert.DoesNotContain(AfterExpiryText, html, StringComparison.Ordinal);

        return shown;
    }

    /// <summary>
    /// Runs a surface once per statement its read costs, committing a whole sync immediately before each
    /// of those statements in turn, and returns each run's response body. The database is put back to the
    /// pre-sync state before every run.
    ///
    /// The statement count is MEASURED from an unraced run rather than written down, so every boundary
    /// the surface has is raced even after a read is added or moved.
    /// </summary>
    private static async Task<List<string>> RaceEveryStatementAsync(
        MySqlComplianceWebFactory factory, MySqlGitOpsImporter importer, Func<Task<string>> read)
    {
        factory.Connections.Arm();
        await read();
        var statements = factory.Connections.CommandsExecuted;
        Assert.True(statements > 1, $"The read cost {statements} statement(s), so it has no boundary to race.");

        var bodies = new List<string>();
        for (var boundary = 1; boundary <= statements; boundary++)
        {
            await importer.ImportAsync(Before());
            factory.Connections.Arm(boundary, ct => importer.ImportAsync(After(), ct));
            bodies.Add(await read());

            // The hook fires immediately before the Nth statement, so a run that never reached it raced
            // nothing and its body proves nothing.
            Assert.True(
                factory.Connections.CommandsExecuted >= boundary,
                $"The run raced at boundary {boundary} issued only {factory.Connections.CommandsExecuted} "
                + "statements, so the sync never landed inside it.");
        }

        return bodies;
    }

    /// <summary>
    /// The vendor scope's justification in a <c>/scopes</c> response, or null when the response omits the
    /// scope. The scope carries a justification in BOTH states, so null means the caller was not shown it.
    /// </summary>
    private static string? VendorScopeJustification(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Where(s => s.GetProperty("id").GetString() == ScopeId)
            .Select(s => s.GetProperty("justification").GetString())
            .SingleOrDefault();
    }

    private static async Task<string> GetRegisterAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/compliance/vendors");
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    // The caller holds a read grant on the readable organisation alone, and the app resolves what that
    // reaches from the asset rows of the snapshot each surface read - the closure this test exists to
    // keep in the loop.
    private static MySqlComplianceWebFactory Factory(MySqlTestDatabase db) => new(db.ConnectionFactory)
    {
        AuthzMode = "Enforce",
        Authz = new FakeAuthzStore().GrantComplianceReader(Reader, ReadableOrg),
        Clock = new FixedClock(Today),
    };

    private static async Task<MySqlTestDatabase> RequireDbAsync()
    {
        var db = await MySqlTestDatabase.TryCreateAsync();
        Skip.If(db is null, $"{MySqlTestDatabase.EnvVar} not set; skipping MySQL integration test.");
        return db!;
    }

    private static async Task<MySqlGitOpsImporter> SeedAsync(MySqlTestDatabase db)
    {
        await new MySqlMigrationRunner(db.ConnectionFactory, typeof(IMigrationRunner).Assembly).ApplyPendingAsync();
        var importer = new MySqlGitOpsImporter(db.ConnectionFactory);
        await importer.ImportAsync(Before());
        return importer;
    }

    // The two states the database really holds, before and after the sync. Every fact one decision uses
    // moves together: the owner edge that decides readability, the justification, and the certification.
    private static GitOpsConfig State(string owner, string justification, DateOnly expires) => new()
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
        Assets =
        [
            new Asset { Id = ReadableOrg, ApiVersion = "v1", Title = "T", Type = "Company", Source = "declared" },
            new Asset { Id = HiddenOrg, ApiVersion = "v1", Title = "T", Type = "Company", Source = "declared" },
            new Asset
            {
                Id = VendorId,
                ApiVersion = "v1",
                Title = "T",
                Type = "Vendor",
                Source = "declared",
                Owner = owner,
                Assurances = [new Assurance { Standard = "std-a", Expires = expires.ToString("yyyy-MM-dd") }],
            },
        ],
        Scopes =
        [
            new Scope
            {
                Id = ScopeId,
                ApiVersion = "v1",
                Title = "T",
                Subject = VendorId,
                Requirement = "req-a",
                Disposition = "Out",
                Justification = justification,
            },
        ],
    };

    private static GitOpsConfig Before() => State(ReadableOrg, BeforeJustification, BeforeExpiry);

    private static GitOpsConfig After() => State(HiddenOrg, AfterJustification, AfterExpiry);

    private sealed class FixedClock(DateOnly today) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }
}
