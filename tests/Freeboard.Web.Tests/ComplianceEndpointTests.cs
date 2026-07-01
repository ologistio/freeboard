using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// The compliance read endpoints (standards, controls, organisations, scopes, statement-of-
/// applicability, compliance/status) require an authenticated user: any logged-in user reads,
/// admin is NOT required, and an anonymous caller is 401'd. Once authenticated, the store-
/// unreachable degradation stands (503 on the resource reads, 200 all-null for status).
/// </summary>
public sealed class ComplianceEndpointTests
{
    private static readonly string[] ResourceReadPaths =
    [
        "/api/v1/freeboard/standards",
        "/api/v1/freeboard/controls",
        "/api/v1/freeboard/organisations",
        "/api/v1/freeboard/scopes",
        "/api/v1/freeboard/statement-of-applicability/std-a",
    ];

    private static FakeComplianceStore PopulatedStore() => new()
    {
        Standards = [new StandardRow("std-a", "Standard A"), new StandardRow("std-b", "Standard B")],
        Controls = [new ControlRow("ctrl-a", "Control A", ["std-a", "std-b"])],
        Organisations =
        [
            new OrganisationRow("org-a", "Org A", "Company", null),
            new OrganisationRow("org-eng", "Engineering", "Department", "org-a"),
        ],
        Scopes = [new ScopeRow("scope-a", "Scope A", "org-a", "std-a", "In")],
    };

    private static AuthWebFactory Factory(FakeComplianceStore store, bool readOnly = false)
        => new() { Compliance = store, ReadOnly = readOnly };

    /// <summary>An authenticated non-admin (member) user; reads are not admin-gated.</summary>
    private static HttpClient MemberClient(AuthWebFactory factory)
        => factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("member1"));

    [Fact]
    public async Task StandardsEndpointReturnsIdsAndTitlesOrderedById()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/standards");

        Assert.Equal(2, json.GetArrayLength());
        Assert.Equal("std-a", json[0].GetProperty("id").GetString());
        Assert.Equal("Standard A", json[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task ControlsEndpointReturnsResolvedMapsTo()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/controls");

        var control = json[0];
        Assert.Equal("ctrl-a", control.GetProperty("id").GetString());
        var mapsTo = control.GetProperty("maps_to").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["std-a", "std-b"], mapsTo);
    }

    [Fact]
    public async Task OrganisationsEndpointReturnsTreeWithKindAndParent()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/organisations");

        Assert.Equal(2, json.GetArrayLength());
        Assert.Equal("org-a", json[0].GetProperty("id").GetString());
        Assert.Equal("Company", json[0].GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, json[0].GetProperty("parent").ValueKind);
        Assert.Equal("org-eng", json[1].GetProperty("id").GetString());
        Assert.Equal("org-a", json[1].GetProperty("parent").GetString());
    }

    [Fact]
    public async Task ScopesEndpointReturnsMapping()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/scopes");

        var scope = json[0];
        Assert.Equal("scope-a", scope.GetProperty("id").GetString());
        Assert.Equal("org-a", scope.GetProperty("organisation").GetString());
        Assert.Equal("std-a", scope.GetProperty("standard").GetString());
        Assert.Equal("In", scope.GetProperty("disposition").GetString());
    }

    [Fact]
    public async Task StatementOfApplicabilityResolvesInheritanceOrderedById()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/statement-of-applicability/std-a");

        var nodes = json.GetProperty("nodes");
        Assert.Equal(2, nodes.GetArrayLength());

        // org-a is explicitly In; org-eng (its child, unstated) inherits In. Ordered by id.
        Assert.Equal("org-a", nodes[0].GetProperty("id").GetString());
        Assert.Equal("In", nodes[0].GetProperty("disposition").GetString());
        Assert.Equal("explicit", nodes[0].GetProperty("resolution").GetString());

        Assert.Equal("org-eng", nodes[1].GetProperty("id").GetString());
        Assert.Equal("In", nodes[1].GetProperty("disposition").GetString());
        Assert.Equal("inherited", nodes[1].GetProperty("resolution").GetString());
    }

    [Fact]
    public async Task StatementOfApplicabilityServedInReadOnlyModeToAuthenticatedUser()
    {
        using var factory = Factory(PopulatedStore(), readOnly: true);
        using var client = MemberClient(factory);

        var response = await client.GetAsync("/api/v1/freeboard/statement-of-applicability/std-a");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StatementOfApplicabilityUnreachableStoreReturns503()
    {
        using var factory = Factory(new FakeComplianceStore { Unreachable = true });
        using var client = MemberClient(factory);

        var response = await client.GetAsync("/api/v1/freeboard/statement-of-applicability/std-a");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task StatusEndpointReturnsPersistedCounts()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/compliance/status");

        var persisted = json.GetProperty("persisted");
        Assert.Equal(2, persisted.GetProperty("standards").GetInt32());
        Assert.Equal(1, persisted.GetProperty("controls").GetInt32());
        Assert.Equal(2, persisted.GetProperty("organisations").GetInt32());
        Assert.Equal(1, persisted.GetProperty("scopes").GetInt32());
    }

    [Fact]
    public async Task ReadEndpointServedInReadOnlyModeToAuthenticatedUser()
    {
        using var factory = Factory(PopulatedStore(), readOnly: true);
        using var client = MemberClient(factory);

        var response = await client.GetAsync("/api/v1/freeboard/standards");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnreachableStoreReturns503ProblemForReads()
    {
        using var factory = Factory(new FakeComplianceStore { Unreachable = true });
        using var client = MemberClient(factory);

        foreach (var path in new[]
                 {
                     "/api/v1/freeboard/standards",
                     "/api/v1/freeboard/controls",
                     "/api/v1/freeboard/organisations",
                     "/api/v1/freeboard/scopes",
                 })
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

    [Fact]
    public async Task UnreachableStoreStatusReturns200WithNullCounts()
    {
        using var factory = Factory(new FakeComplianceStore { Unreachable = true });
        using var client = MemberClient(factory);

        var response = await client.GetAsync("/api/v1/freeboard/compliance/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var persisted = json.GetProperty("persisted");
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("standards").ValueKind);
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("controls").ValueKind);
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("organisations").ValueKind);
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("scopes").ValueKind);
    }

    [Fact]
    public async Task AnonymousReadIsUnauthorized()
    {
        using var factory = Factory(PopulatedStore());
        using var client = factory.CreateClient();

        foreach (var path in ResourceReadPaths.Append("/api/v1/freeboard/compliance/status"))
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task NonAdminAuthenticatedUserCanReadStatus()
    {
        // Reads require authentication only, not the admin role.
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var response = await client.GetAsync("/api/v1/freeboard/compliance/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GitOpsStatusUnchangedAndIndependentOfStore()
    {
        // /api/gitops/status stays anonymous and store-independent even with an unreachable store.
        using var factory = Factory(new FakeComplianceStore { Unreachable = true }, readOnly: true);
        using var client = factory.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/gitops/status");

        Assert.True(json.GetProperty("gitOps").GetBoolean());
        Assert.False(json.TryGetProperty("persisted", out _));
    }
}
