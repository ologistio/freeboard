using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Freeboard.Web.Tests;

/// <summary>
/// The moved endpoints answer under /api/v1/freeboard/*; in read-only mode a marked auth
/// endpoint is NOT 409 while a non-auth mutating route under the prefix STILL 409s, and GETs
/// are unaffected. Exercises the AuthEndpoint marker + the post-routing middleware reorder.
/// </summary>
public sealed class RouteMoveReadOnlyTests
{
    private static FakeComplianceStore Store() => new()
    {
        Standards = [new Persistence.StandardRow("std-a", "Standard A", "1.0", "Example Authority", null, null)],
    };

    [Fact]
    public async Task MovedReadEndpointsAnswerUnderNewPrefix()
    {
        using var factory = new AuthWebFactory { Compliance = Store() };
        // The compliance reads require an authenticated user; gitops/status stays anonymous.
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("route1"));

        foreach (var path in new[]
        {
            "/api/v1/freeboard/standards",
            "/api/v1/freeboard/controls",
            "/api/v1/freeboard/scopes",
            "/api/v1/freeboard/compliance/status",
            "/api/v1/freeboard/gitops/status",
        })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task OldPathsAreGone()
    {
        using var factory = new ComplianceWebFactory(Store());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/standards");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyExemptsMarkedAuthEndpointButNot409sNonAuthMutatingRoute()
    {
        // The non-auth mutating route under the prefix is a TEST-ONLY endpoint (not shipped
        // in Program.cs), registered via the factory as an EndpointDataSource so it flows through
        // the real GitOps middleware.
        using var factory = new AuthWebFactory { ReadOnly = true, IncludeTestProbe = true };
        using var client = factory.CreateClient();

        // Marked auth POST (real /auth/login): exempt from the 409 even in read-only mode. With an
        // empty body and an unknown user it returns 401, NOT the GitOps 409 - proving the exemption.
        var login = await client.PostAsJsonAsync(
            "/api/v1/freeboard/auth/login", new { email = "nobody@example.com", password = "x" });
        Assert.NotEqual(HttpStatusCode.Conflict, login.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        // Non-auth mutating route under the SAME prefix (unmarked): still 409.
        using var plainContent = new StringContent("", Encoding.UTF8);
        var plain = await client.PostAsync(TestProbeEndpointDataSource.Path, plainContent);
        Assert.Equal(HttpStatusCode.Conflict, plain.StatusCode);
        Assert.Equal("application/problem+json", plain.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ReadOnlyDoesNotAffectGets()
    {
        using var factory = new AuthWebFactory { Compliance = Store(), ReadOnly = true };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("route2"));

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/standards");
        Assert.Equal(1, json.GetArrayLength());
    }
}
