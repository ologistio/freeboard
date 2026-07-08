using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// The Evidence ingest response matrix, driven end-to-end through the real endpoint, the collector auth
/// scheme + named ingest policy, and the in-memory fakes. Malformed JSON is the ONLY 400; every
/// well-formed value/type problem is 422. The 201 test posts the SAME in-repo example the JSON Schema
/// drift test validates, so the example, the schema, and the endpoint bindings cannot silently drift.
/// The write path is the shared <see cref="IEvidenceWriteStore"/>: the endpoint maps the contract onto
/// a <see cref="NewEvidenceRun"/>, derives the verdict, and treats a store conflict as a 200 replay.
/// </summary>
public sealed class EvidenceIngestEndpointTests
{
    private const string Route = "/api/v1/freeboard/evidence";
    private const string ExampleCollectorId = "google-workspace-mfa";
    private const string ExampleOrg = "org-acme";
    private const string ExampleRequirement = "req-mfa";
    private const string Control = "ctrl-mfa";
    private const string Standard = "std-1";

    private static EvidenceCollectorRow Collector(string id, string? vendor = "vendor-google") =>
        new(id, $"{id} title", Control, vendor, "integration", "daily", null, new Dictionary<string, string>());

    /// <summary>
    /// Seeds the register + scope so a valid payload for <paramref name="collectorId"/> reporting
    /// (org-acme, req-mfa) passes: the collector's control maps to req-mfa, req-mfa belongs to std-1,
    /// and org-acme resolves In for std-1.
    /// </summary>
    private static FakeComplianceStore Store(string collectorId, string? vendor = "vendor-google") => new()
    {
        Collectors = [Collector(collectorId, vendor)],
        Controls = [new ControlRow(Control, "MFA", [ExampleRequirement], null)],
        Requirements =
        [
            new RequirementRow(ExampleRequirement, "MFA", Standard, "Access", "Enforce MFA", null, "A.5", "https://x/r"),
        ],
        Organisations = [new OrganisationRow(ExampleOrg, "Acme", "Company", null)],
        Scopes = [new ScopeRow("scope-1", "Acme in", ExampleOrg, Standard, "In")],
    };

    private static AuthWebFactory FactoryFor(string collectorId, string? vendor = "vendor-google") => new()
    {
        Compliance = Store(collectorId, vendor),
    };

    private static HttpClient ClientWith(AuthWebFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static string Valid(
        string collectorId = "col-1",
        string organisationId = ExampleOrg,
        string requirementId = ExampleRequirement,
        string runId = "run-1",
        string collectedAt = "2026-01-01T00:00:00Z",
        string checks = """[{"name":"c1","severity":"hard","result":"pass"}]""")
        => $$"""
        {
          "schema_version": "freeboard.evidence.v1",
          "collector_id": "{{collectorId}}",
          "organisation_id": "{{organisationId}}",
          "requirement_id": "{{requirementId}}",
          "run_id": "{{runId}}",
          "collected_at": "{{collectedAt}}",
          "checks": {{checks}}
        }
        """;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Freeboard.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    internal static string ExamplePayload() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "docs", "schemas", "evidence-ingest.v1.example.json"));

    [Fact]
    public async Task ValidExampleLandsAsCreatedWithMappedRun()
    {
        using var factory = FactoryFor(ExampleCollectorId);
        var token = factory.SeedCollectorCredential(ExampleCollectorId);
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(Route, Body(ExamplePayload()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ExampleCollectorId, body.GetProperty("collector_id").GetString());
        Assert.Equal(0, body.GetProperty("hard_fail_count").GetInt32());
        Assert.Equal(1, body.GetProperty("soft_fail_count").GetInt32());
        Assert.Equal(2, body.GetProperty("total_count").GetInt32());
        // The success body carries NO server-assigned evidence id.
        Assert.False(body.TryGetProperty("evidence_id", out _));

        // The contract is mapped onto the shared NewEvidenceRun with the snapshotted vendor and a
        // collector-scoped ref. No hard failure, so the derived verdict is Pass.
        var run = Assert.Single(factory.EvidenceStore.Appended);
        Assert.Equal(ExampleOrg, run.OrganisationId);
        Assert.Equal(ExampleRequirement, run.RequirementId);
        Assert.Equal("vendor-google", run.Vendor);
        Assert.Equal($"{ExampleCollectorId}:2026-07-08T09-00-00Z-a1b2c3", run.CollectorRef);
        Assert.Equal("Pass", run.Result);
        Assert.Equal(2, run.Checks.Count);
        Assert.Equal(["Hard", "Soft"], run.Checks.Select(c => c.Severity).ToArray());
        Assert.Equal(["Pass", "Fail"], run.Checks.Select(c => c.Result).ToArray());
        // The full submitted body is retained as the raw payload.
        Assert.Contains("\"tenant\": \"ologist.io\"", run.RawPayload);
    }

    [Fact]
    public async Task HardCheckFailureDerivesFailVerdict()
    {
        using var factory = FactoryFor("col-1");
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(
            Route, Body(Valid(checks: """[{"name":"c1","severity":"hard","result":"fail"}]""")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var run = Assert.Single(factory.EvidenceStore.Appended);
        Assert.Equal("Fail", run.Result);
    }

    [Fact]
    public async Task MalformedJsonIs400()
    {
        using var factory = FactoryFor("col-1");
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(Route, Body("{ not json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    // A JSON type mismatch on ANY field is 422, never 400.
    [InlineData("""{"schema_version":"freeboard.evidence.v1","collector_id":123,"organisation_id":"org-acme","requirement_id":"req-mfa","run_id":"r","collected_at":"2026-01-01T00:00:00Z","checks":[{"name":"c","severity":"hard","result":"pass"}]}""")]
    [InlineData("""{"schema_version":1,"collector_id":"col-1","organisation_id":"org-acme","requirement_id":"req-mfa","run_id":"r","collected_at":"2026-01-01T00:00:00Z","checks":[{"name":"c","severity":"hard","result":"pass"}]}""")]
    [InlineData("""{"schema_version":"freeboard.evidence.v1","collector_id":"col-1","organisation_id":"org-acme","requirement_id":"req-mfa","run_id":"r","collected_at":"2026-01-01T00:00:00Z","checks":[{"name":"c","severity":1,"result":"pass"}]}""")]
    [InlineData("""{"schema_version":"freeboard.evidence.v1","collector_id":"col-1","organisation_id":"org-acme","requirement_id":"req-mfa","run_id":"r","collected_at":"2026-01-01T00:00:00Z","checks":[{"name":"c","severity":"hard","result":2}]}""")]
    [InlineData("""{"schema_version":"freeboard.evidence.v1","collector_id":"col-1","organisation_id":"org-acme","requirement_id":"req-mfa","run_id":"r","collected_at":"2026-01-01T00:00:00Z","checks":"nope"}""")]
    public async Task JsonTypeMismatchIs422NotBadRequest(string json)
    {
        using var factory = FactoryFor("col-1");
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(Route, Body(json));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public Task WrongSchemaVersionIs422() =>
        AssertUnprocessable(Valid().Replace("freeboard.evidence.v1", "wrong-schema", StringComparison.Ordinal));

    [Fact]
    public Task BadSeverityIs422() =>
        AssertUnprocessable(Valid(checks: """[{"name":"c1","severity":"critical","result":"pass"}]"""));

    [Fact]
    public Task BadResultIs422() =>
        AssertUnprocessable(Valid(checks: """[{"name":"c1","severity":"hard","result":"maybe"}]"""));

    [Theory]
    [InlineData("unknown")]
    [InlineData("not_applicable")]
    public Task DroppedCheckResultsAre422(string result) =>
        AssertUnprocessable(Valid(checks: $$"""[{"name":"c1","severity":"hard","result":"{{result}}"}]"""));

    [Fact]
    public Task EmptyChecksIs422() => AssertUnprocessable(Valid(checks: "[]"));

    [Fact]
    public Task DuplicateCheckNameIs422() =>
        AssertUnprocessable(Valid(checks:
            """[{"name":"dup","severity":"hard","result":"pass"},{"name":"dup","severity":"soft","result":"fail"}]"""));

    [Fact]
    public Task BlankCheckNameIs422() =>
        AssertUnprocessable(Valid(checks: """[{"name":"   ","severity":"hard","result":"pass"}]"""));

    [Fact]
    public Task OverCapCheckDetailIs422() =>
        // A detail over the 4096-char cap but under the 1 MiB body limit: caught in validation as a clean
        // 422, not passed to main's TEXT detail column where it would fail as a 503.
        AssertUnprocessable(Valid(checks:
            $$"""[{"name":"c1","severity":"hard","result":"pass","detail":"{{new string('d', 4097)}}"}]"""));

    [Fact]
    public Task MissingCollectedAtIs422() =>
        AssertUnprocessable("""
        {"schema_version":"freeboard.evidence.v1","collector_id":"col-1","organisation_id":"org-acme",
         "requirement_id":"req-mfa","run_id":"r","checks":[{"name":"c","severity":"hard","result":"pass"}]}
        """);

    [Fact]
    public Task EmptyRunIdIs422() => AssertUnprocessable(Valid(runId: ""));

    [Fact]
    public Task EmptyCollectorIdIs422() => AssertUnprocessable(Valid(collectorId: ""));

    [Fact]
    public Task NonUtcTimestampIs422() => AssertUnprocessable(Valid(collectedAt: "2026-01-01T00:00:00+02:00"));

    [Fact]
    public async Task CollectorMismatchIs422()
    {
        // Credential is for col-1, but the body claims col-2.
        using var factory = FactoryFor("col-1");
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(Route, Body(Valid(collectorId: "col-2")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task UnknownCollectorIs422()
    {
        // Credential authenticates for "ghost", but no such collector is registered.
        using var factory = new AuthWebFactory { Compliance = new FakeComplianceStore { Collectors = [] } };
        var token = factory.SeedCollectorCredential("ghost");
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(Route, Body(Valid(collectorId: "ghost")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task NullVendorCollectorIs422()
    {
        // A registered collector with no vendor cannot ingest: no valid value for the run's vendor.
        using var factory = FactoryFor("col-1", vendor: null);
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(Route, Body(Valid()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(factory.EvidenceStore.Appended);
    }

    [Fact]
    public async Task RequirementNotUnderControlIs422()
    {
        using var factory = FactoryFor("col-1");
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);

        // req-other is a real requirement in scope, but the collector's control does not map to it.
        factory.Compliance.Requirements =
        [
            .. factory.Compliance.Requirements,
            new RequirementRow("req-other", "Other", Standard, "Access", "S", null, "A.6", "https://x/o"),
        ];

        var response = await client.PostAsync(Route, Body(Valid(requirementId: "req-other")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task OrganisationNotInScopeIs422()
    {
        using var factory = FactoryFor("col-1");
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);

        // org-out exists but has an explicit Out scope for the standard.
        factory.Compliance.Organisations =
            [.. factory.Compliance.Organisations, new OrganisationRow("org-out", "Out", "Company", null)];
        factory.Compliance.Scopes =
            [.. factory.Compliance.Scopes, new ScopeRow("scope-out", "Out", "org-out", Standard, "Out")];

        var response = await client.PostAsync(Route, Body(Valid(organisationId: "org-out")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public Task OverLongCollectorRefIs422() =>
        // collector_id (5) + ':' + a 190-char run_id exceeds the 190-char collector_ref cap.
        AssertUnprocessable(Valid(runId: new string('r', 190)));

    [Fact]
    public async Task MissingCredentialIs401()
    {
        using var factory = FactoryFor("col-1");
        using var client = factory.CreateClient(); // no Authorization header

        var response = await client.PostAsync(Route, Body(Valid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SessionTokenAtIngestIs401()
    {
        using var factory = FactoryFor("col-1");
        // A human session token: recognised by the session scheme, but not by the collector scheme.
        var sessionToken = factory.SeedSession(AuthWebFactory.MakeUser("user-01"));
        using var client = ClientWith(factory, sessionToken);

        var response = await client.PostAsync(Route, Body(Valid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnknownTokenIs401()
    {
        using var factory = FactoryFor("col-1");
        factory.SeedCollectorCredential("col-1");
        // A well-formed token that hashes but matches no stored credential.
        using var client = ClientWith(factory, MintUnseededToken(factory));

        var response = await client.PostAsync(Route, Body(Valid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MalformedBearerTokenIs401()
    {
        using var factory = FactoryFor("col-1");
        factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, "not-a-valid-token");

        var response = await client.PostAsync(Route, Body(Valid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task KeyVersionMismatchIs401()
    {
        using var factory = FactoryFor("col-1");
        var token = factory.SeedCollectorCredential("col-1", storedKeyVersion: 999);
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(Route, Body(Valid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RevokedCredentialIs403()
    {
        using var factory = FactoryFor("col-1");
        var token = factory.SeedCollectorCredential("col-1", revokedAt: DateTime.UtcNow.AddMinutes(-1));
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(Route, Body(Valid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredCredentialIs403()
    {
        using var factory = FactoryFor("col-1");
        var token = factory.SeedCollectorCredential("col-1", expiresAt: DateTime.UtcNow.AddMinutes(-1));
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(Route, Body(Valid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DuplicateRunIsReplayWithoutEvidenceId()
    {
        using var factory = FactoryFor("col-1");
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);
        var payload = Valid(checks: """[{"name":"c1","severity":"hard","result":"fail"}]""");

        var first = await client.PostAsync(Route, Body(payload));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsync(Route, Body(payload));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();

        // Replay echoes only request-derived values; the store readback carries no evidence id.
        Assert.False(body.TryGetProperty("evidence_id", out _));
        Assert.Equal("col-1", body.GetProperty("collector_id").GetString());
        Assert.Equal(1, body.GetProperty("hard_fail_count").GetInt32());
        // Only the first append landed; the replay wrote nothing.
        Assert.Single(factory.EvidenceStore.Appended);
    }

    [Fact]
    public async Task StoreValidationErrorIs422()
    {
        using var factory = new AuthWebFactory
        {
            Compliance = Store("col-1"),
            EvidenceStore = new FakeEvidenceWriteStore { FailFirstWith = WriteResult.Fail("bad row") },
        };
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(Route, Body(Valid()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task OversizeBodyIs413()
    {
        using var factory = FactoryFor("col-1");
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);

        var big = new string('x', 1_100_000);
        var json = Valid(checks: $$"""[{"name":"c1","severity":"hard","result":"pass","detail":"{{big}}"}]""");

        var response = await client.PostAsync(Route, Body(json));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task StoreFailureIs503()
    {
        using var factory = new AuthWebFactory
        {
            Compliance = Store("col-1"),
            EvidenceStore = new FakeEvidenceWriteStore { Unreachable = true },
        };
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(Route, Body(Valid()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyModeDoesNotBlockIngest()
    {
        using var factory = new AuthWebFactory
        {
            ReadOnly = true,
            Compliance = Store("col-1"),
        };
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(Route, Body(Valid()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static string MintUnseededToken(AuthWebFactory factory)
    {
        var hasher = (Freeboard.Persistence.Auth.ITokenHasher)factory.Services.GetService(
            typeof(Freeboard.Persistence.Auth.ITokenHasher))!;
        return hasher.MintPrefixed().Token;
    }

    private static async Task AssertUnprocessable(string json)
    {
        using var factory = FactoryFor("col-1");
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(Route, Body(json));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}
