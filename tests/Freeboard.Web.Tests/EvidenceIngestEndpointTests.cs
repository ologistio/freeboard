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
/// </summary>
public sealed class EvidenceIngestEndpointTests
{
    private const string Route = "/api/v1/freeboard/evidence";
    private const string ExampleCollectorId = "google-workspace-mfa";

    private static EvidenceCollectorRow Collector(string id) =>
        new(id, $"{id} title", "ctrl-mfa", "vendor-google", "integration", "daily", null,
            new Dictionary<string, string>());

    private static AuthWebFactory FactoryFor(string collectorId) => new()
    {
        Compliance = new FakeComplianceStore { Collectors = [Collector(collectorId)] },
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
        string runId = "run-1",
        string startedAt = "2026-01-01T00:00:00Z",
        string finishedAt = "2026-01-01T00:01:00Z",
        string checks = """[{"name":"c1","severity":"hard","status":"pass"}]""")
        => $$"""
        {
          "schema_version": "freeboard.evidence.v1",
          "collector_id": "{{collectorId}}",
          "run_id": "{{runId}}",
          "started_at": "{{startedAt}}",
          "finished_at": "{{finishedAt}}",
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
    public async Task ValidExampleLandsAsCreatedWithSnapshotAndCounts()
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

        // The collector identity is snapshotted onto the run.
        var run = Assert.Single(factory.EvidenceStore.Appended);
        Assert.Equal("ctrl-mfa", run.ControlId);
        Assert.Equal("vendor-google", run.VendorId);
        Assert.Equal("integration", run.CollectorType);
        Assert.Equal(2, run.Checks.Count);
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
    [InlineData("""{"schema_version":"freeboard.evidence.v1","collector_id":123,"run_id":"r","started_at":"2026-01-01T00:00:00Z","finished_at":"2026-01-01T00:01:00Z","checks":[{"name":"c","severity":"hard","status":"pass"}]}""")]
    [InlineData("""{"schema_version":1,"collector_id":"col-1","run_id":"r","started_at":"2026-01-01T00:00:00Z","finished_at":"2026-01-01T00:01:00Z","checks":[{"name":"c","severity":"hard","status":"pass"}]}""")]
    [InlineData("""{"schema_version":"freeboard.evidence.v1","collector_id":"col-1","run_id":"r","started_at":"2026-01-01T00:00:00Z","finished_at":"2026-01-01T00:01:00Z","checks":[{"name":"c","severity":1,"status":"pass"}]}""")]
    [InlineData("""{"schema_version":"freeboard.evidence.v1","collector_id":"col-1","run_id":"r","started_at":"2026-01-01T00:00:00Z","finished_at":"2026-01-01T00:01:00Z","checks":[{"name":"c","severity":"hard","status":2}]}""")]
    [InlineData("""{"schema_version":"freeboard.evidence.v1","collector_id":"col-1","run_id":"r","started_at":"2026-01-01T00:00:00Z","finished_at":"2026-01-01T00:01:00Z","checks":"nope"}""")]
    public async Task JsonTypeMismatchIs422NotBadRequest(string json)
    {
        using var factory = FactoryFor("col-1");
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(Route, Body(json));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Theory]
    [InlineData("wrong-schema")]
    public async Task WrongSchemaVersionIs422(string schema)
    {
        var json = Valid().Replace("freeboard.evidence.v1", schema, StringComparison.Ordinal);
        await AssertUnprocessable(json);
    }

    [Fact]
    public Task BadSeverityIs422() =>
        AssertUnprocessable(Valid(checks: """[{"name":"c1","severity":"critical","status":"pass"}]"""));

    [Fact]
    public Task BadStatusIs422() =>
        AssertUnprocessable(Valid(checks: """[{"name":"c1","severity":"hard","status":"maybe"}]"""));

    [Fact]
    public Task EmptyChecksIs422() => AssertUnprocessable(Valid(checks: "[]"));

    [Fact]
    public Task DuplicateCheckNameIs422() =>
        AssertUnprocessable(Valid(checks:
            """[{"name":"dup","severity":"hard","status":"pass"},{"name":"dup","severity":"soft","status":"fail"}]"""));

    [Fact]
    public Task MissingStartedAtIs422() =>
        AssertUnprocessable("""
        {"schema_version":"freeboard.evidence.v1","collector_id":"col-1","run_id":"r",
         "finished_at":"2026-01-01T00:01:00Z","checks":[{"name":"c","severity":"hard","status":"pass"}]}
        """);

    [Fact]
    public Task NullFinishedAtIs422() =>
        AssertUnprocessable(Valid(finishedAt: "").Replace("\"finished_at\": \"\"", "\"finished_at\": null", StringComparison.Ordinal));

    [Fact]
    public Task EmptyRunIdIs422() => AssertUnprocessable(Valid(runId: ""));

    [Fact]
    public Task WhitespaceRunIdIs422() => AssertUnprocessable(Valid(runId: "   "));

    [Fact]
    public Task EmptyCollectorIdIs422() => AssertUnprocessable(Valid(collectorId: ""));

    [Fact]
    public Task NonTimestampStartedAtIs422() => AssertUnprocessable(Valid(startedAt: "not-a-date"));

    [Fact]
    public Task NonUtcTimestampIs422() => AssertUnprocessable(Valid(startedAt: "2026-01-01T00:00:00+02:00"));

    [Fact]
    public Task FinishedBeforeStartedIs422() =>
        AssertUnprocessable(Valid(startedAt: "2026-01-01T00:05:00Z", finishedAt: "2026-01-01T00:01:00Z"));

    [Fact]
    public Task NonObjectMetadataIs422() =>
        AssertUnprocessable(Valid().TrimEnd().TrimEnd('}') + ""","metadata":"x"}""");

    [Fact]
    public Task NonObjectCheckDataIs422() =>
        AssertUnprocessable(Valid(checks: """[{"name":"c1","severity":"hard","status":"pass","data":"x"}]"""));

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
    public async Task MalformedBearerTokenIs401()
    {
        // A syntactically invalid token: TryHashPrefixed fails, so no DB lookup and a uniform 401.
        using var factory = FactoryFor("col-1");
        factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, "not-a-valid-token");

        var response = await client.PostAsync(Route, Body(Valid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task KeyVersionMismatchIs401()
    {
        // The token hashes and locates its credential, but the stored key version disagrees with the
        // token's parsed key id: an integrity failure that must reject with 401.
        using var factory = FactoryFor("col-1");
        var token = factory.SeedCollectorCredential("col-1", storedKeyVersion: 999);
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(Route, Body(Valid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CredentialExpiringAtOrBeforeNowIs403()
    {
        // Boundary: the active check is strict (expires_at > now), so a credential whose expiry is the
        // seed instant is already expired by request time and denied with 403.
        using var factory = FactoryFor("col-1");
        var token = factory.SeedCollectorCredential("col-1", expiresAt: DateTime.UtcNow);
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(Route, Body(Valid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DuplicateSameBodyIsReplayWithOriginalValues()
    {
        using var factory = FactoryFor("col-1");
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);
        var payload = Valid(checks: """[{"name":"c1","severity":"hard","status":"fail"}]""");

        var first = await client.PostAsync(Route, Body(payload));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();

        var second = await client.PostAsync(Route, Body(payload));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();

        // Replay returns the ORIGINAL stored evidence id, received_at, and counts; nothing re-appended.
        Assert.Equal(firstBody.GetProperty("evidence_id").GetString(), secondBody.GetProperty("evidence_id").GetString());
        Assert.Equal(
            firstBody.GetProperty("received_at").GetString(), secondBody.GetProperty("received_at").GetString());
        Assert.Equal(1, secondBody.GetProperty("hard_fail_count").GetInt32());
        Assert.Single(factory.EvidenceStore.Appended);
    }

    [Fact]
    public async Task DuplicateDifferentBodyIs409()
    {
        using var factory = FactoryFor("col-1");
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);

        var first = await client.PostAsync(Route, Body(Valid(checks: """[{"name":"c1","severity":"hard","status":"pass"}]""")));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsync(Route, Body(Valid(checks: """[{"name":"c1","severity":"hard","status":"fail"}]""")));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task OversizeBodyIs413()
    {
        using var factory = FactoryFor("col-1");
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);

        var big = new string('x', 1_100_000);
        var json = Valid(checks: $$"""[{"name":"c1","severity":"hard","status":"pass","detail":"{{big}}"}]""");

        var response = await client.PostAsync(Route, Body(json));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task StoreFailureIs503()
    {
        using var factory = new AuthWebFactory
        {
            Compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] },
            EvidenceStore = new FakeEvidenceIngestStore { Unreachable = true },
        };
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(Route, Body(Valid()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task StoreInvalidOperationIs503()
    {
        // A lazily-opened connection over an empty connection string surfaces as
        // InvalidOperationException, not DbException; it must still map to a 503.
        using var factory = new AuthWebFactory
        {
            Compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] },
            EvidenceStore = new FakeEvidenceIngestStore { Fault = new InvalidOperationException("no connection string") },
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
            Compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] },
        };
        var token = factory.SeedCollectorCredential("col-1");
        using var client = ClientWith(factory, token);

        var response = await client.PostAsync(Route, Body(Valid()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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
