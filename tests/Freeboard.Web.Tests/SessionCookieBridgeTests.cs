using System.Net;
using System.Net.Http.Headers;
using Freeboard.Web;
using Microsoft.AspNetCore.Http;

namespace Freeboard.Web.Tests;

/// <summary>
/// The cookie-to-bearer bridge: a session cookie authenticates a non-API page route via the
/// unchanged bearer handler, but never the JSON API and never over a real bearer header. Also
/// pins the session cookie's security attributes.
/// </summary>
public sealed class SessionCookieBridgeTests
{
    private const string ApiPrefix = "/api/v1/freeboard";

    [Fact]
    public async Task ValidSessionCookieAuthenticatesAPageRoute()
    {
        using var factory = new AuthWebFactory { IncludeTestPageProbe = true };
        var user = AuthWebFactory.MakeUser("cb1");
        var token = factory.SeedSession(user);
        using var client = factory.CreateClient();

        var response = await GetWithCookie(client, TestAuthenticatedPageRoute.Path, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(user.Id, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MissingCookieDoesNotAuthenticateAPageRoute()
    {
        using var factory = new AuthWebFactory { IncludeTestPageProbe = true };
        using var client = factory.CreateClient();

        var response = await client.GetAsync(TestAuthenticatedPageRoute.Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredSessionCookieDoesNotAuthenticate()
    {
        using var factory = new AuthWebFactory { IncludeTestPageProbe = true };
        var user = AuthWebFactory.MakeUser("cb2");
        var token = factory.SeedSession(user, expiresAt: DateTime.UtcNow.AddHours(-1));
        using var client = factory.CreateClient();

        var response = await GetWithCookie(client, TestAuthenticatedPageRoute.Path, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RevokedSessionCookieDoesNotAuthenticate()
    {
        using var factory = new AuthWebFactory { IncludeTestPageProbe = true };
        var user = AuthWebFactory.MakeUser("cb3");
        var token = factory.SeedSession(user);
        await factory.Sessions.DeleteAsync(AuthWebFactory.SessionIdFor(user));
        using var client = factory.CreateClient();

        var response = await GetWithCookie(client, TestAuthenticatedPageRoute.Path, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PresentAuthorizationHeaderIsNeverOverwrittenByTheCookie()
    {
        using var factory = new AuthWebFactory { IncludeTestPageProbe = true };
        var headerUser = AuthWebFactory.MakeUser("cbh");
        var headerToken = factory.SeedSession(headerUser);
        var cookieUser = AuthWebFactory.MakeUser("cbc");
        var cookieToken = factory.SeedSession(cookieUser);

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, TestAuthenticatedPageRoute.Path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", headerToken);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={cookieToken}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The real bearer header wins; the cookie's user is not used.
        Assert.Equal(headerUser.Id, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CookieDoesNotAuthenticateAnApiRoute()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("cba");
        var token = factory.SeedSession(user);
        using var client = factory.CreateClient();

        // An authenticated API route: a cookie-only request must NOT be bridged, so it stays 401.
        var response = await GetWithCookie(client, $"{ApiPrefix}/auth/me", token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void SessionCookieCarriesHostPrefixedStrictSecureAttributes()
    {
        var ctx = new DefaultHttpContext();
        SessionCookie.Set(ctx.Response, "v1.token", DateTimeOffset.UtcNow.AddHours(1));

        var setCookie = Assert.Single(ctx.Response.Headers.SetCookie!);
        Assert.StartsWith($"{SessionCookie.Name}=", setCookie);
        Assert.Equal("__Host-freeboard-session", SessionCookie.Name);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EachSeededSessionMintsAFreshDistinctToken()
    {
        using var factory = new AuthWebFactory();
        var first = factory.SeedSession(AuthWebFactory.MakeUser("ft1"));
        var second = factory.SeedSession(AuthWebFactory.MakeUser("ft2"));

        // SessionIssuer mints a brand-new token per session, so no pre-auth identifier is reused.
        Assert.NotEqual(first, second);
    }

    private static Task<HttpResponseMessage> GetWithCookie(HttpClient client, string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        return client.SendAsync(request);
    }
}
