using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// The collector-credential admin API: issue and revoke, gated by system.admin, returning the raw token
/// once. Unknown collector is 422; a non-admin caller is 403; issuance is blocked by GitOps read-only
/// mode (it is admin config, not runtime ingest).
/// </summary>
public sealed class CollectorCredentialEndpointTests
{
    private static EvidenceCollectorRow Collector(string id) =>
        new(id, $"{id} title", "ctrl-mfa", null, "integration", "daily", null, new Dictionary<string, string>());

    private static string IssueRoute(string id) => $"/api/v1/freeboard/evidence-collectors/{id}/credentials";

    [Fact]
    public async Task AdminIssuesCredentialAndGetsRawTokenOnce()
    {
        using var factory = new AuthWebFactory
        {
            Compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] },
        };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("admin-1", role: "admin"));

        var response = await client.PostAsJsonAsync(IssueRoute("col-1"), new { });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));
        Assert.Equal("col-1", body.GetProperty("collector_id").GetString());
    }

    [Fact]
    public async Task IssueForUnknownCollectorIs422()
    {
        using var factory = new AuthWebFactory
        {
            Compliance = new FakeComplianceStore { Collectors = [] },
        };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("admin-1", role: "admin"));

        var response = await client.PostAsJsonAsync(IssueRoute("ghost"), new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task NonAdminIssueIs403()
    {
        using var factory = new AuthWebFactory
        {
            Compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] },
        };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("member-1", role: "member"));

        var response = await client.PostAsJsonAsync(IssueRoute("col-1"), new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NonAdminRevokeIs403()
    {
        using var factory = new AuthWebFactory
        {
            Compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] },
        };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("member-1", role: "member"));

        var response = await client.DeleteAsync($"{IssueRoute("col-1")}/cred-1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminRevokesIssuedCredential()
    {
        using var factory = new AuthWebFactory
        {
            Compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] },
        };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("admin-1", role: "admin"));

        var issue = await client.PostAsJsonAsync(IssueRoute("col-1"), new { });
        var credId = (await issue.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("credential_id").GetString();

        var revoke = await client.DeleteAsync($"{IssueRoute("col-1")}/{credId}");

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
    }

    [Fact]
    public async Task IssueStoreInvalidOperationIs503()
    {
        // A lazily-opened connection over an empty connection string surfaces as
        // InvalidOperationException, not DbException; issuance must still map to a 503.
        using var factory = new AuthWebFactory
        {
            Compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] },
            CollectorCreds = new FakeCollectorCredentialStore { Fault = new InvalidOperationException("no connection string") },
        };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("admin-1", role: "admin"));

        var response = await client.PostAsJsonAsync(IssueRoute("col-1"), new { });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyModeBlocksCredentialIssuance()
    {
        using var factory = new AuthWebFactory
        {
            ReadOnly = true,
            Compliance = new FakeComplianceStore { Collectors = [Collector("col-1")] },
        };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("admin-1", role: "admin"));

        var response = await client.PostAsJsonAsync(IssueRoute("col-1"), new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
