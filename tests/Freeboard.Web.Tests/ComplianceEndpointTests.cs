using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

public sealed class ComplianceEndpointTests
{
    private static FakeComplianceStore PopulatedStore() => new()
    {
        Standards = [new StandardRow("std-a", "Standard A"), new StandardRow("std-b", "Standard B")],
        Controls = [new ControlRow("ctrl-a", "Control A", ["std-a", "std-b"])],
        Scopes = [new ScopeRow("scope-a", "Scope A", ["ctrl-a"])],
    };

    // 7.1
    [Fact]
    public async Task StandardsEndpointReturnsIdsAndTitlesOrderedById()
    {
        using var factory = new ComplianceWebFactory(PopulatedStore());
        using var client = factory.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/api/standards");

        Assert.Equal(2, json.GetArrayLength());
        Assert.Equal("std-a", json[0].GetProperty("id").GetString());
        Assert.Equal("Standard A", json[0].GetProperty("title").GetString());
    }

    // 7.1
    [Fact]
    public async Task ControlsEndpointReturnsResolvedMapsTo()
    {
        using var factory = new ComplianceWebFactory(PopulatedStore());
        using var client = factory.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/api/controls");

        var control = json[0];
        Assert.Equal("ctrl-a", control.GetProperty("id").GetString());
        var mapsTo = control.GetProperty("maps_to").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["std-a", "std-b"], mapsTo);
    }

    // 7.1
    [Fact]
    public async Task ScopesEndpointReturnsResolvedControls()
    {
        using var factory = new ComplianceWebFactory(PopulatedStore());
        using var client = factory.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/api/scopes");

        var scope = json[0];
        Assert.Equal("scope-a", scope.GetProperty("id").GetString());
        Assert.Equal(["ctrl-a"], scope.GetProperty("controls").EnumerateArray().Select(e => e.GetString()).ToList());
    }

    // 7.1
    [Fact]
    public async Task StatusEndpointReturnsPersistedCounts()
    {
        using var factory = new ComplianceWebFactory(PopulatedStore());
        using var client = factory.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/api/compliance/status");

        var persisted = json.GetProperty("persisted");
        Assert.Equal(2, persisted.GetProperty("standards").GetInt32());
        Assert.Equal(1, persisted.GetProperty("controls").GetInt32());
        Assert.Equal(1, persisted.GetProperty("scopes").GetInt32());
    }

    // 7.2
    [Fact]
    public async Task ReadEndpointServedInReadOnlyMode()
    {
        using var factory = new ComplianceWebFactory(PopulatedStore(), readOnly: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/standards");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // 7.4
    [Fact]
    public async Task UnreachableStoreReturns503ProblemForReads()
    {
        using var factory = new ComplianceWebFactory(new FakeComplianceStore { Unreachable = true });
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/api/standards", "/api/controls", "/api/scopes" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

            // Contract-stable RFC 7807 problem title and detail; assert verbatim.
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Compliance store unreachable", problem.GetProperty("title").GetString());
            Assert.Equal(
                "The compliance store could not be reached. Check the database connection.",
                problem.GetProperty("detail").GetString());
        }
    }

    // 7.4
    [Fact]
    public async Task UnreachableStoreStatusReturns200WithNullCounts()
    {
        using var factory = new ComplianceWebFactory(new FakeComplianceStore { Unreachable = true });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/compliance/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var persisted = json.GetProperty("persisted");
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("standards").ValueKind);
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("controls").ValueKind);
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("scopes").ValueKind);
    }

    // 7.5
    [Fact]
    public async Task GitOpsStatusUnchangedAndIndependentOfStore()
    {
        // Even with an unreachable store, /api/gitops/status works and has no persisted field.
        using var factory = new ComplianceWebFactory(new FakeComplianceStore { Unreachable = true }, readOnly: true);
        using var client = factory.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/api/gitops/status");

        Assert.True(json.GetProperty("gitOps").GetBoolean());
        Assert.False(json.TryGetProperty("persisted", out _));
    }
}
