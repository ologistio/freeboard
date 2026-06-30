using System.Net;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// The page funnel the two middleware edits enable: a force-reset (limited) cookie session can reach
/// its allowed pages (complete-reset GET/POST, logout, account landing) and is redirected to
/// complete-reset on any other page route, while the API JSON 403 stays byte-identical. Also that a
/// mutating auth page POST is exempt from the GitOps read-only 409 while a non-auth route still 409s.
/// </summary>
public sealed class PageFunnelTests
{
    private const string ApiPrefix = "/api/v1/freeboard";

    private static HttpClient NoRedirect(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static HttpRequestMessage Get(string path, string cookieValue)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={cookieValue}");
        return request;
    }

    [Fact]
    public async Task LimitedSessionCanGetAccountLanding()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("pf-acc", forcePasswordReset: true);
        var token = factory.SeedSession(user, SessionAuthState.ForceResetLimited);
        using var client = NoRedirect(factory);

        using var request = Get("/account", token);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LimitedSessionCanPostCompleteResetAndUpgrade()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("pf-cr", forcePasswordReset: true);
        var token = factory.SeedSession(user, SessionAuthState.ForceResetLimited);
        using var client = NoRedirect(factory);

        var response = await AuthFormTestHelpers.PostFormAsync(client, "/account/complete-reset",
            new[] { new KeyValuePair<string, string>("new_password", "brand-new-password") },
            extraCookies: new[] { new KeyValuePair<string, string>(SessionCookie.Name, token) });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account", response.Headers.Location!.OriginalString);
        Assert.False((await factory.Users.GetByIdAsync(user.Id))!.ForcePasswordReset);
    }

    [Fact]
    public async Task LimitedSessionCanPostLogout()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("pf-lo", forcePasswordReset: true);
        var token = factory.SeedSession(user, SessionAuthState.ForceResetLimited);
        using var client = NoRedirect(factory);

        // /logout GET only redirects, so scrape the antiforgery token from the complete-reset page the
        // limited session is allowed to GET.
        var response = await AuthFormTestHelpers.PostFormAsync(client, "/logout",
            Array.Empty<KeyValuePair<string, string>>(),
            extraCookies: new[] { new KeyValuePair<string, string>(SessionCookie.Name, token) },
            getPath: "/account/complete-reset");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(AuthFormTestHelpers.ClearsCookie(response, SessionCookie.Name));
    }

    [Fact]
    public async Task LimitedSessionOnOtherPageRouteIsRedirectedToCompleteReset()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("pf-other", forcePasswordReset: true);
        var token = factory.SeedSession(user, SessionAuthState.ForceResetLimited);
        using var client = NoRedirect(factory);

        // /account/password/change is a real protected page the limited session is NOT allowed to reach.
        using var request = Get("/account/password/change", token);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/complete-reset", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task LimitedSessionOnNonPageMinimalEndpointGetsByteIdenticalJson403()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("pf-root", forcePasswordReset: true);
        var token = factory.SeedSession(user, SessionAuthState.ForceResetLimited);
        using var client = NoRedirect(factory);

        // `/` is the hello-world minimal endpoint, not a Razor Page: it must keep the JSON 403, not 302.
        using var request = Get("/", token);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Headers.Location);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("password-reset-required", body);
        Assert.Contains("set a new password before using other endpoints", body);
    }

    [Fact]
    public async Task LimitedSessionOnOtherApiRouteGetsByteIdenticalJson403()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("pf-api", forcePasswordReset: true);
        using var client = factory.CreateAuthenticatedClient(user, SessionAuthState.ForceResetLimited);

        var response = await client.GetAsync($"{ApiPrefix}/auth/sessions/anything");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Headers.Location);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("password-reset-required", body);
        Assert.Contains("set a new password before using other endpoints", body);
    }

    [Fact]
    public async Task AuthPagePostSucceedsUnderReadOnly()
    {
        using var factory = new AuthWebFactory { ReadOnly = true };
        var user = AuthWebFactory.MakeUser("pf-ro");
        SeedLogin(factory, user);
        using var client = NoRedirect(factory);

        // /login is a marked auth page POST; read-only mode must not 409 it.
        var response = await AuthFormTestHelpers.PostFormAsync(client, "/login",
            new[] { new KeyValuePair<string, string>("email", user.Email), new("password", "password") });

        Assert.NotEqual(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task NonAuthMutatingRouteStill409sUnderReadOnly()
    {
        using var factory = new AuthWebFactory { ReadOnly = true, IncludeTestProbe = true };
        using var client = factory.CreateClient();

        using var content = new StringContent(string.Empty);
        var response = await client.PostAsync(TestProbeEndpointDataSource.Path, content);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private static void SeedLogin(AuthWebFactory factory, UserRow user, string password = "password")
    {
        factory.Users.Add(user);
        factory.Credentials.SetAsync(user.Id, factory.Hasher.Hash(password), 1).GetAwaiter().GetResult();
    }
}
