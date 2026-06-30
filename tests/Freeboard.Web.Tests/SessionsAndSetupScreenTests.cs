using System.Net;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// The session-management and first-admin-setup screens. The session list shows only the user's own
/// sessions and marks the current one; revoke is IDOR-safe (a user cannot revoke another user's
/// session) and clears the session cookie when it revokes the current session or all sessions. Setup
/// drives the bootstrap flow: the happy path sets a full-session cookie and lands on /account, a
/// second attempt shows the uniform "already set up" message, and a wrong secret surfaces a generic
/// error without revealing the initialization state.
/// </summary>
public sealed class SessionsAndSetupScreenTests
{
    private static HttpClient NoRedirect(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static KeyValuePair<string, string>[] SessionCookieFor(string token)
        => new[] { new KeyValuePair<string, string>(SessionCookie.Name, token) };

    [Fact]
    public async Task SessionListRendersAndMarksCurrent()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("sess-list");
        var token = factory.SeedSession(user);

        // A second live session for the same user (not the current one).
        var minted = factory.Services.GetRequiredService<ITokenHasher>().MintPrefixed();
        factory.Sessions.Add(
            new SessionRow("sess-other", user.Id, minted.KeyVersion, SessionAuthState.Full, 1, null,
                DateTime.UtcNow, DateTime.UtcNow.AddHours(1), null),
            minted.Hash);

        using var client = NoRedirect(factory);
        using var get = new HttpRequestMessage(HttpMethod.Get, "/account/sessions");
        get.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        var response = await client.SendAsync(get);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(AuthWebFactory.SessionIdFor(user), html);
        Assert.Contains("sess-other", html);
        Assert.Contains("(this session)", html); // the current session is marked.
        // The bearer token never appears in the rendered list.
        Assert.DoesNotContain(token, html);
    }

    [Fact]
    public async Task RevokeOwnSessionRemovesIt()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("sess-revoke-own");
        var token = factory.SeedSession(user);

        var minted = factory.Services.GetRequiredService<ITokenHasher>().MintPrefixed();
        factory.Sessions.Add(
            new SessionRow("sess-other", user.Id, minted.KeyVersion, SessionAuthState.Full, 1, null,
                DateTime.UtcNow, DateTime.UtcNow.AddHours(1), null),
            minted.Hash);

        using var client = NoRedirect(factory);
        var response = await AuthFormTestHelpers.PostFormAsync(client, "/account/sessions/revoke",
            new[] { new KeyValuePair<string, string>("sessionId", "sess-other") },
            extraCookies: SessionCookieFor(token), getPath: "/account/sessions");

        // Revoking a non-current session lands back on the list and does not clear the cookie.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/sessions", response.Headers.Location!.OriginalString);
        Assert.Null(await factory.Sessions.GetByIdAsync("sess-other"));
        Assert.NotNull(await factory.Sessions.GetByIdAsync(AuthWebFactory.SessionIdFor(user)));
    }

    [Fact]
    public async Task RevokeAnotherUsersSessionIsNoOp()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("sess-idor");
        var token = factory.SeedSession(user);

        // A session belonging to a DIFFERENT user.
        var other = AuthWebFactory.MakeUser("sess-victim");
        factory.Users.Add(other);
        var minted = factory.Services.GetRequiredService<ITokenHasher>().MintPrefixed();
        factory.Sessions.Add(
            new SessionRow("sess-victim-1", other.Id, minted.KeyVersion, SessionAuthState.Full, 1, null,
                DateTime.UtcNow, DateTime.UtcNow.AddHours(1), null),
            minted.Hash);

        using var client = NoRedirect(factory);
        var response = await AuthFormTestHelpers.PostFormAsync(client, "/account/sessions/revoke",
            new[] { new KeyValuePair<string, string>("sessionId", "sess-victim-1") },
            extraCookies: SessionCookieFor(token), getPath: "/account/sessions");

        // The foreign session is untouched: the IDOR-safe flow only deletes a session the caller owns.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/sessions", response.Headers.Location!.OriginalString);
        Assert.NotNull(await factory.Sessions.GetByIdAsync("sess-victim-1"));
    }

    [Fact]
    public async Task RevokeCurrentSessionClearsCookieAndRedirectsToLogin()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("sess-revoke-current");
        var token = factory.SeedSession(user);

        using var client = NoRedirect(factory);
        var response = await AuthFormTestHelpers.PostFormAsync(client, "/account/sessions/revoke",
            new[] { new KeyValuePair<string, string>("sessionId", AuthWebFactory.SessionIdFor(user)) },
            extraCookies: SessionCookieFor(token), getPath: "/account/sessions");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.OriginalString);
        Assert.True(AuthFormTestHelpers.ClearsCookie(response, SessionCookie.Name));
        Assert.Null(await factory.Sessions.GetByIdAsync(AuthWebFactory.SessionIdFor(user)));
    }

    [Fact]
    public async Task RevokeAllClearsCookieAndRedirectsToLogin()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("sess-revoke-all");
        var token = factory.SeedSession(user);

        var minted = factory.Services.GetRequiredService<ITokenHasher>().MintPrefixed();
        factory.Sessions.Add(
            new SessionRow("sess-other", user.Id, minted.KeyVersion, SessionAuthState.Full, 1, null,
                DateTime.UtcNow, DateTime.UtcNow.AddHours(1), null),
            minted.Hash);

        using var client = NoRedirect(factory);
        var response = await AuthFormTestHelpers.PostFormAsync(client, "/account/sessions/revoke",
            new[] { new KeyValuePair<string, string>("all", "true") },
            extraCookies: SessionCookieFor(token), getPath: "/account/sessions");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.OriginalString);
        Assert.True(AuthFormTestHelpers.ClearsCookie(response, SessionCookie.Name));
        // Every session for the user is gone.
        Assert.Empty((await factory.Sessions.ListByUserAsync(user.Id)));
    }

    [Fact]
    public async Task SetupHappyPathSetsCookieAndRedirectsToAccount()
    {
        using var factory = new AuthWebFactory { BootstrapSecret = "the-secret" };
        using var client = NoRedirect(factory);

        var response = await AuthFormTestHelpers.PostFormAsync(client, "/setup",
            new[]
            {
                new KeyValuePair<string, string>("name", "Admin"),
                new("email", "admin@example.com"),
                new("password", "adminpass"),
                new("bootstrap_secret", "the-secret"),
            });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account", response.Headers.Location!.OriginalString);
        // A full-session cookie is set (the __Host- session cookie carries the minted token).
        Assert.Single(
            AuthFormTestHelpers.ParseSetCookies(response), c => c.Key == SessionCookie.Name);
        Assert.Equal(1, await factory.Users.CountAsync());
    }

    [Fact]
    public async Task SetupAlreadyInitializedShowsGenericMessage()
    {
        using var factory = new AuthWebFactory { BootstrapSecret = "the-secret" };
        using var client = NoRedirect(factory);

        var fields = new[]
        {
            new KeyValuePair<string, string>("name", "Admin"),
            new("email", "admin@example.com"),
            new("password", "adminpass"),
            new("bootstrap_secret", "the-secret"),
        };

        var first = await AuthFormTestHelpers.PostFormAsync(client, "/setup", fields);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);

        var second = await AuthFormTestHelpers.PostFormAsync(client, "/setup", fields);
        var html = await second.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, second.StatusCode); // re-render, not a redirect.
        Assert.Contains("already set up", html);
        // No second cookie was issued.
        Assert.DoesNotContain(
            AuthFormTestHelpers.ParseSetCookies(second), c => c.Key == SessionCookie.Name);
    }

    [Fact]
    public async Task SetupWrongSecretShowsGenericError()
    {
        using var factory = new AuthWebFactory { BootstrapSecret = "the-secret" };
        using var client = NoRedirect(factory);

        var response = await AuthFormTestHelpers.PostFormAsync(client, "/setup",
            new[]
            {
                new KeyValuePair<string, string>("name", "Admin"),
                new("email", "admin@example.com"),
                new("password", "adminpass"),
                new("bootstrap_secret", "wrong"),
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // re-render with a generic error.
        Assert.DoesNotContain(
            AuthFormTestHelpers.ParseSetCookies(response), c => c.Key == SessionCookie.Name);
        Assert.Equal(0, await factory.Users.CountAsync());
    }
}
