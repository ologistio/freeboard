using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Freeboard.Web.Tests;

public sealed class GitOpsReadOnlyTests
{
    [Fact]
    public async Task ReadOnlyOnRejectsMutatingRequestWith409ProblemJson()
    {
        using var factory = new GitOpsWebFactory(readOnly: true, repositoryUrl: "https://example.com/repo.git");
        using var client = factory.CreateClient();

        using var content = new StringContent("", Encoding.UTF8);
        var response = await client.PostAsync("/", content);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("type", out _));
        Assert.True(root.TryGetProperty("title", out _));
        Assert.Equal(409, root.GetProperty("status").GetInt32());
        Assert.True(root.TryGetProperty("detail", out _));
        Assert.Equal("https://example.com/repo.git", root.GetProperty("repositoryUrl").GetString());
        Assert.Contains("https://example.com/repo.git", root.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ReadOnlyOnAllowsGet()
    {
        using var factory = new GitOpsWebFactory(readOnly: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyOffDoesNotInterceptMutatingRequest()
    {
        // Exercises the middleware's off-branch against the real pipeline. With the flag
        // off the middleware passes a POST through to routing, which returns 405 because
        // "/" is GET-only. The point is the response is the real downstream one, NOT the
        // 409 GitOps problem+json. POST "/" with the flag ON returns 409 (asserted
        // below), so this test fails if the middleware wrongly intercepted when off.
        using var factory = new GitOpsWebFactory(readOnly: false);
        using var client = factory.CreateClient();

        using var content = new StringContent("", Encoding.UTF8);
        var response = await client.PostAsync("/", content);

        Assert.NotEqual(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.NotEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ReadOnlyOnInterceptsSamePathThatOffLetsThrough()
    {
        // Pairs with ReadOnlyOffDoesNotInterceptMutatingRequest: the same POST "/" that
        // the off-branch lets reach routing (405) is intercepted with 409 when on. This
        // proves the middleware runs upstream of routing and the off-test is not vacuous.
        using var factory = new GitOpsWebFactory(readOnly: true);
        using var client = factory.CreateClient();

        using var content = new StringContent("", Encoding.UTF8);
        var response = await client.PostAsync("/", content);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task StatusReportsGitOpsOnWithRepoUrl()
    {
        using var factory = new GitOpsWebFactory(readOnly: true, repositoryUrl: "https://example.com/repo.git");
        using var client = factory.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/gitops/status");

        Assert.True(json.GetProperty("gitOps").GetBoolean());
        Assert.Equal("https://example.com/repo.git", json.GetProperty("repositoryUrl").GetString());
    }

    [Fact]
    public async Task StatusReportsGitOpsOffByDefault()
    {
        using var factory = new GitOpsWebFactory(readOnly: false);
        using var client = factory.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/freeboard/gitops/status");

        Assert.False(json.GetProperty("gitOps").GetBoolean());
        Assert.False(json.TryGetProperty("repositoryUrl", out _));
    }
}
