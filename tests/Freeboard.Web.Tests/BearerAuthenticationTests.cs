using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Freeboard.Persistence.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Freeboard.Web.Tests;

/// <summary>
/// Bearer-handler tests: no/malformed/unknown-key bearer -> uniform 401 with no DB lookup;
/// a valid session authenticates (GET /auth/me -> 200); expired session and disabled user -> 401;
/// a limited (force-reset) session is blocked (403) from a non-allowlisted endpoint.
/// </summary>
public sealed class BearerAuthenticationTests
{
    private const string Me = "/api/v1/freeboard/auth/me";

    [Fact]
    public async Task NoBearerIsUnauthorized()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Me);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("v999.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // unknown key id
    [InlineData("garbage.with.dots")]
    public async Task MalformedOrUnknownKeyBearerIsUniform401(string token)
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync(Me);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidSessionAuthenticates()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("user-01"));

        var json = await client.GetFromJsonAsync<JsonElement>(Me);
        Assert.Equal("user-01", json.GetProperty("id").GetString());
    }

    [Fact]
    public async Task LimitedSessionBlockedFromNormalEndpoint()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("user-02", forcePasswordReset: true);
        using var client = factory.CreateAuthenticatedClient(user, SessionAuthState.ForceResetLimited);

        // A non-allowlisted bearer-protected endpoint: the limited guard returns 403.
        var response = await client.GetAsync($"/api/v1/freeboard/users/{user.Id}/sessions");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredSessionIs401()
    {
        using var factory = new AuthWebFactory();
        factory.Users.Add(AuthWebFactory.MakeUser("user-03"));

        var hasher = factory.Services.GetRequiredService<ITokenHasher>();
        var minted = hasher.MintPrefixed();
        factory.Sessions.Add(
            new SessionRow("sess-03", "user-03", minted.KeyVersion, SessionAuthState.Full, 1, null,
                DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1), null),
            minted.Hash);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", minted.Token);

        var response = await client.GetAsync(Me);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DisabledUserIs401()
    {
        using var factory = new AuthWebFactory();
        factory.Users.Add(AuthWebFactory.MakeUser("user-04", enabled: false));

        var hasher = factory.Services.GetRequiredService<ITokenHasher>();
        var minted = hasher.MintPrefixed();
        factory.Sessions.Add(
            new SessionRow("sess-04", "user-04", minted.KeyVersion, SessionAuthState.Full, 1, null,
                DateTime.UtcNow, DateTime.UtcNow.AddHours(1), null),
            minted.Hash);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", minted.Token);

        var response = await client.GetAsync(Me);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
