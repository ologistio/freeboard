using System.Text.Json;
using Json.Schema;

namespace Freeboard.Web.Tests;

/// <summary>
/// Drift guard: the in-repo example payload MUST validate against the published JSON Schema. Combined
/// with the 201 endpoint test (which posts the SAME example), the example, the schema, and the endpoint
/// validator are transitively linked and cannot silently drift.
/// </summary>
public sealed class EvidenceSchemaTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "Freeboard.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // Built once: JsonSchema.FromText registers the schema by its $id in a global registry, so
    // rebuilding it per test throws "Overwriting registered schemas is not permitted".
    private static readonly JsonSchema Schema = JsonSchema.FromText(File.ReadAllText(
        Path.GetFullPath(Path.Join(RepoRoot(), "docs", "schemas", "evidence-ingest.v1.schema.json"))));

    private static bool Validates(string payload)
    {
        using var instance = JsonDocument.Parse(payload);
        return Schema
            .Evaluate(instance.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List })
            .IsValid;
    }

    [Fact]
    public void ExamplePayloadValidatesAgainstSchema() =>
        Assert.True(
            Validates(EvidenceIngestEndpointTests.ExamplePayload()),
            "The example payload does not validate against evidence-ingest.v1.schema.json.");

    [Fact]
    public void ExplicitNullOptionalFieldsValidate() =>
        // The server treats a null optional field as absent; the schema must accept null to match it.
        Assert.True(Validates("""
        {
          "schema_version": "freeboard.evidence.v1",
          "collector_id": "c", "organisation_id": "o", "requirement_id": "r",
          "run_id": "run", "collected_at": "2026-01-01T00:00:00Z",
          "collector_version": null,
          "checks": [{"name": "c1", "severity": "hard", "result": "pass", "detail": null}],
          "metadata": null
        }
        """));

    [Fact]
    public void NonUtcOffsetTimestampFailsSchema() =>
        // Mirrors the server's 422: a non-zero offset is not UTC, so the schema pattern must reject it.
        Assert.False(Validates("""
        {
          "schema_version": "freeboard.evidence.v1",
          "collector_id": "c", "organisation_id": "o", "requirement_id": "r",
          "run_id": "run", "collected_at": "2026-01-01T00:00:00+02:00",
          "checks": [{"name": "c1", "severity": "hard", "result": "pass"}]
        }
        """));

    [Fact]
    public void UnknownCheckResultFailsSchema() =>
        // The narrowed contract has only pass/fail; unknown/not_applicable are rejected.
        Assert.False(Validates("""
        {
          "schema_version": "freeboard.evidence.v1",
          "collector_id": "c", "organisation_id": "o", "requirement_id": "r",
          "run_id": "run", "collected_at": "2026-01-01T00:00:00Z",
          "checks": [{"name": "c1", "severity": "hard", "result": "unknown"}]
        }
        """));

    [Fact]
    public void WhitespaceOnlyCollectorIdFailsSchema() =>
        // Mirrors the server's 422 on a blank id: the pattern must require a non-whitespace character.
        Assert.False(Validates("""
        {
          "schema_version": "freeboard.evidence.v1",
          "collector_id": " ", "organisation_id": "o", "requirement_id": "r",
          "run_id": "run", "collected_at": "2026-01-01T00:00:00Z",
          "checks": [{"name": "c1", "severity": "hard", "result": "pass"}]
        }
        """));

    [Fact]
    public void ExtraCheckPropertyFailsSchema() =>
        // The narrowed contract dropped `data` and `status`; additionalProperties:false pins the removal.
        Assert.False(Validates("""
        {
          "schema_version": "freeboard.evidence.v1",
          "collector_id": "c", "organisation_id": "o", "requirement_id": "r",
          "run_id": "run", "collected_at": "2026-01-01T00:00:00Z",
          "checks": [{"name": "c1", "severity": "hard", "result": "pass", "data": {"k": 1}}]
        }
        """));

    [Fact]
    public void ColonInRunIdFailsSchema() =>
        // Mirrors the server's 422: ':' is the collector_id:run_id delimiter, so it is rejected in run_id.
        Assert.False(Validates("""
        {
          "schema_version": "freeboard.evidence.v1",
          "collector_id": "c", "organisation_id": "o", "requirement_id": "r",
          "run_id": "a:b", "collected_at": "2026-01-01T00:00:00Z",
          "checks": [{"name": "c1", "severity": "hard", "result": "pass"}]
        }
        """));

    [Fact]
    public void WhitespaceOnlyCheckNameFailsSchema() =>
        // Mirrors the server's 422 on a blank check name: the pattern must require a non-whitespace char.
        Assert.False(Validates("""
        {
          "schema_version": "freeboard.evidence.v1",
          "collector_id": "c", "organisation_id": "o", "requirement_id": "r",
          "run_id": "run", "collected_at": "2026-01-01T00:00:00Z",
          "checks": [{"name": "   ", "severity": "hard", "result": "pass"}]
        }
        """));

    [Fact]
    public void MissingOrganisationIdFailsSchema() =>
        Assert.False(Validates("""
        {
          "schema_version": "freeboard.evidence.v1",
          "collector_id": "c", "requirement_id": "r",
          "run_id": "run", "collected_at": "2026-01-01T00:00:00Z",
          "checks": [{"name": "c1", "severity": "hard", "result": "pass"}]
        }
        """));

    [Fact]
    public void SecondsLessUtcTimestampValidates() =>
        // The server's DateTimeOffset.TryParse accepts a seconds-less UTC instant, so the schema must too.
        Assert.True(Validates("""
        {
          "schema_version": "freeboard.evidence.v1",
          "collector_id": "c", "organisation_id": "o", "requirement_id": "r",
          "run_id": "run", "collected_at": "2026-01-01T00:00Z",
          "checks": [{"name": "c1", "severity": "hard", "result": "pass"}]
        }
        """));
}
