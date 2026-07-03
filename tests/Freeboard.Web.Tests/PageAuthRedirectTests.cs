using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// The page-route auth funnel: a protected page redirects an unauthenticated browser to /login,
/// while the JSON API keeps its bare 401/403 and the non-page routes (/, gitops/status) are
/// unchanged. Also asserts page-POST antiforgery and that an anonymous page renders without a
/// redirect. The page policy is folder-scoped to /account, never a process-wide default/fallback.
/// </summary>
public sealed class PageAuthRedirectTests
{
    private const string ApiPrefix = "/api/v1/freeboard";

    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task UnauthenticatedGetToProtectedPageRedirectsToLogin()
    {
        using var factory = new AuthWebFactory();
        using var client = NoRedirectClient(factory);

        var response = await client.GetAsync("/account");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task AuthenticatedCookieRendersProtectedAccountPage()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("pa1");
        var token = factory.SeedSession(user);
        using var client = NoRedirectClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/account");
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(user.Email, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnauthenticatedGetToHomeRedirectsToLogin()
    {
        using var factory = new AuthWebFactory();
        using var client = NoRedirectClient(factory);

        var response = await client.GetAsync("/home");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task AuthenticatedCookieRendersHomePage()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("home1");
        var token = factory.SeedSession(user);
        using var client = NoRedirectClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/home");
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnauthenticatedApiRequestReturnsJsonUnauthorizedNotRedirect()
    {
        using var factory = new AuthWebFactory();
        using var client = NoRedirectClient(factory);

        var response = await client.GetAsync($"{ApiPrefix}/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task InvalidBearerToApiReturnsJsonUnauthorizedNotRedirect()
    {
        using var factory = new AuthWebFactory();
        using var client = NoRedirectClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/auth/me");
        request.Headers.Add("Authorization", "Bearer v1.not-a-real-token");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task UnauthenticatedGetToAnonymousLoginPageRendersWithoutRedirect()
    {
        using var factory = new AuthWebFactory();
        using var client = NoRedirectClient(factory);

        var response = await client.GetAsync("/login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task RootIsUnchangedForUnauthenticatedRequest()
    {
        using var factory = new AuthWebFactory();
        using var client = NoRedirectClient(factory);

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("Hello World!", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GitOpsStatusIsUnchangedForUnauthenticatedRequest()
    {
        using var factory = new AuthWebFactory();
        using var client = NoRedirectClient(factory);

        var response = await client.GetAsync($"{ApiPrefix}/gitops/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task PagePostWithoutAntiforgeryTokenIsRejected()
    {
        using var factory = new AuthWebFactory();
        using var client = NoRedirectClient(factory);

        // A form POST with no antiforgery field/cookie is rejected by the global validation.
        using var content = new FormUrlEncodedContent(
            new[] { new KeyValuePair<string, string>("email", "a@b.c") });
        var response = await client.PostAsync("/login", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
