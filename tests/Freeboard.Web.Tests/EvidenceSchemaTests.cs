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
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Freeboard.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // Built once: JsonSchema.FromText registers the schema by its $id in a global registry, so
    // rebuilding it per test throws "Overwriting registered schemas is not permitted".
    private static readonly JsonSchema Schema = JsonSchema.FromText(File.ReadAllText(
        Path.Combine(RepoRoot(), "docs", "schemas", "evidence-ingest.v1.schema.json")));

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
    public void ExplicitNullMetadataValidates() =>
        // The server treats a null optional field as absent; the schema must accept null to match it.
        Assert.True(Validates("""
        {
          "schema_version": "freeboard.evidence.v1",
          "collector_id": "c", "run_id": "r",
          "started_at": "2026-01-01T00:00:00Z", "finished_at": "2026-01-01T00:01:00Z",
          "checks": [{"name": "c1", "severity": "hard", "status": "pass", "detail": null, "data": null}],
          "metadata": null
        }
        """));

    [Fact]
    public void NonUtcOffsetTimestampFailsSchema() =>
        // Mirrors the server's 422: a non-zero offset is not UTC, so the schema pattern must reject it.
        Assert.False(Validates("""
        {
          "schema_version": "freeboard.evidence.v1",
          "collector_id": "c", "run_id": "r",
          "started_at": "2026-01-01T00:00:00+02:00", "finished_at": "2026-01-01T00:01:00Z",
          "checks": [{"name": "c1", "severity": "hard", "status": "pass"}]
        }
        """));

    [Fact]
    public void WhitespaceOnlyCollectorIdFailsSchema() =>
        // Mirrors the server's 422 on a blank id: the pattern must require a non-whitespace character.
        Assert.False(Validates("""
        {
          "schema_version": "freeboard.evidence.v1",
          "collector_id": " ", "run_id": "r",
          "started_at": "2026-01-01T00:00:00Z", "finished_at": "2026-01-01T00:01:00Z",
          "checks": [{"name": "c1", "severity": "hard", "status": "pass"}]
        }
        """));

    [Fact]
    public void WhitespaceOnlyRunIdFailsSchema() =>
        Assert.False(Validates("""
        {
          "schema_version": "freeboard.evidence.v1",
          "collector_id": "c", "run_id": " ",
          "started_at": "2026-01-01T00:00:00Z", "finished_at": "2026-01-01T00:01:00Z",
          "checks": [{"name": "c1", "severity": "hard", "status": "pass"}]
        }
        """));

    [Fact]
    public void SecondsLessUtcTimestampValidates() =>
        // The server's DateTimeOffset.TryParse accepts a seconds-less UTC instant, so the schema must too.
        Assert.True(Validates("""
        {
          "schema_version": "freeboard.evidence.v1",
          "collector_id": "c", "run_id": "r",
          "started_at": "2026-01-01T00:00Z", "finished_at": "2026-01-01T00:01Z",
          "checks": [{"name": "c1", "severity": "hard", "status": "pass"}]
        }
        """));
}
