using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Freeboard.Persistence.Auth;

namespace Freeboard.Web.Tests;

/// <summary>
/// Hardening tests for auth endpoints: session-state gating, forwarded-header
/// trust, bootstrap-secret checks, atomic password/session changes, and session reads.
/// </summary>
public sealed class AuthHardeningTests
{
    private const string Prefix = "/api/v1/freeboard";

    [Fact]
    public async Task FullSessionCannotUseAccountPassword()
    {
        using var factory = new AuthWebFactory();
        // A NORMAL full session must not bypass /auth/password/change's old_password proof.
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("full1"));

        var response = await client.PostAsJsonAsync($"{Prefix}/account/password", new { new_password = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LimitedSessionHappyPathStillWorks()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("lim1", forcePasswordReset: true);
        using var client = factory.CreateAuthenticatedClient(user, SessionAuthState.ForceResetLimited);

        var response = await client.PostAsJsonAsync($"{Prefix}/account/password", new { new_password = "fresh" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LimitedClaimButForceResetAlreadyClearedIsForbidden()
    {
        using var factory = new AuthWebFactory();
        // Session claim says limited, but the user row no longer has force_password_reset (a stale
        // claim). The store re-check must reject it.
        var user = AuthWebFactory.MakeUser("lim2", forcePasswordReset: false);
        using var client = factory.CreateAuthenticatedClient(user, SessionAuthState.ForceResetLimited);

        var response = await client.PostAsJsonAsync($"{Prefix}/account/password", new { new_password = "fresh" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SpoofedXForwardedForDoesNotChangeRateLimitIp()
    {
        using var factory = new AuthWebFactory(); // no Auth:ForwardedHeaders config
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "9.9.9.9");

        var response = await client.PostAsJsonAsync(
            $"{Prefix}/auth/login", new { email = "nobody@example.com", password = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The IP bucket must NOT key off the spoofed header value; the socket IP is used.
        Assert.DoesNotContain("9.9.9.9", factory.RateLimit.IpKeysSeen);
    }

    [Fact]
    public async Task WrongBootstrapSecretReturns401WithoutTouchingTheDatabase()
    {
        using var factory = new AuthWebFactory { BootstrapSecret = "right-secret" };
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"{Prefix}/setup", new
        {
            email = "a@e.com",
            name = "A",
            password = "p",
            bootstrap_secret = "wrong-secret",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // No rate-limit bucket was touched (secret checked first, before any DB work).
        Assert.Empty(factory.RateLimit.IpKeysSeen);
        // No admin was created.
        Assert.Equal(0, await factory.Users.CountAsync());
    }

    [Fact]
    public async Task DifferentLengthBootstrapSecretIsAlso401NoOracle()
    {
        using var factory = new AuthWebFactory { BootstrapSecret = "right-secret" };
        using var client = factory.CreateClient();

        // A much shorter presented secret must still be a uniform 401 (digests are fixed length).
        var response = await client.PostAsJsonAsync($"{Prefix}/setup", new
        {
            email = "a@e.com",
            name = "A",
            password = "p",
            bootstrap_secret = "x",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnconfiguredBootstrapSecretDisablesSetup()
    {
        using var factory = new AuthWebFactory { BootstrapSecret = string.Empty };
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"{Prefix}/setup", new
        {
            email = "a@e.com",
            name = "A",
            password = "p",
            bootstrap_secret = "anything",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PasswordChangeRevokesOtherSessionsKeepsCurrent()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("ch1");
        using var client = factory.CreateAuthenticatedClient(user, password: "old");

        // A second, OTHER session for the same user.
        var other = await factory.Sessions.CreateAsync(
            user.Id, [7, 7, 7], 1, SessionAuthState.Full, DateTime.UtcNow.AddHours(1));

        var response = await client.PostAsJsonAsync(
            $"{Prefix}/auth/password/change", new { old_password = "old", new_password = "newpass" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The other session is gone; the caller's current session survives.
        Assert.Null(await factory.Sessions.GetByIdAsync(other.Id));
        var live = await factory.Sessions.ListByUserAsync(user.Id);
        Assert.Single(live);
    }

    [Fact]
    public async Task PasswordResetRevokesAllSessions()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        var user = AuthWebFactory.MakeUser("rs1");
        factory.Users.Add(user);
        await factory.Credentials.SetAsync(user.Id, factory.Hasher.Hash("password"), 1);
        await factory.Sessions.CreateAsync(user.Id, [1, 2, 3], 1, SessionAuthState.Full, DateTime.UtcNow.AddHours(1));
        await factory.Sessions.CreateAsync(user.Id, [4, 5, 6], 1, SessionAuthState.Full, DateTime.UtcNow.AddHours(1));

        using var client = factory.CreateClient();
        await client.PostAsJsonAsync($"{Prefix}/auth/password/forgot", new { email = user.Email });
        var token = factory.Email.PasswordResets.Single().Token;

        var response = await client.PostAsJsonAsync(
            $"{Prefix}/auth/password/reset", new { token, new_password = "brandnew" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await factory.Sessions.ListByUserAsync(user.Id));
    }

    [Fact]
    public async Task RehashFailureDoesNotBlockLogin()
    {
        using var factory = new AuthWebFactory();
        factory.Hasher.RehashNeeded = true;          // force the rehash path
        factory.Credentials.ThrowOnUpdateHash = true; // and make it fail

        var user = AuthWebFactory.MakeUser("rh1");
        factory.Users.Add(user);
        await factory.Credentials.SetAsync(user.Id, factory.Hasher.Hash("password"), 1);

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"{Prefix}/auth/login", new { email = user.Email, password = "password" });

        // A correct login still succeeds despite the rehash DB failure.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ProbeRouteNotShippedInProduction()
    {
        using var factory = new AuthWebFactory(); // IncludeTestProbe defaults to false
        using var client = factory.CreateClient();

        var response = await client.PostAsync(TestProbeEndpointDataSource.Path, null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListExcludesExpiredSessions()
    {
        using var factory = new AuthWebFactory();
        var admin = AuthWebFactory.MakeUser("adm21", role: "admin");
        using var client = factory.CreateAuthenticatedClient(admin);

        factory.Users.Add(AuthWebFactory.MakeUser("target21"));
        await factory.Sessions.CreateAsync("target21", [1], 1, SessionAuthState.Full, DateTime.UtcNow.AddHours(1));  // live
        await factory.Sessions.CreateAsync("target21", [2], 1, SessionAuthState.Full, DateTime.UtcNow.AddHours(-1)); // expired

        var json = await client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{Prefix}/users/target21/sessions");
        Assert.Equal(1, json.GetArrayLength());
    }

    [Fact]
    public async Task GetExpiredSessionIs404()
    {
        using var factory = new AuthWebFactory();
        var admin = AuthWebFactory.MakeUser("adm21b", role: "admin");
        using var client = factory.CreateAuthenticatedClient(admin);

        factory.Users.Add(AuthWebFactory.MakeUser("target21b"));
        var expired = await factory.Sessions.CreateAsync(
            "target21b", [3], 1, SessionAuthState.Full, DateTime.UtcNow.AddHours(-1));

        var response = await client.GetAsync($"{Prefix}/auth/sessions/{expired.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BearerAuthUpdatesLastSeen()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("ls30");
        using var client = factory.CreateAuthenticatedClient(user);

        // The seeded session starts with no last-seen.
        var before = await factory.Sessions.GetByIdAsync($"sess-{user.Id}");
        Assert.Null(before!.LastSeenAt);

        var response = await client.GetAsync($"{Prefix}/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // After an authenticated request the bearer handler has touched last_seen_at.
        var after = await factory.Sessions.GetByIdAsync($"sess-{user.Id}");
        Assert.NotNull(after!.LastSeenAt);
    }
}
