using System.Net;
using System.Net.Http.Json;
using System.Text;
using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Freeboard.Web.Tests;

/// <summary>
/// The passkey web screens: the login-MFA passkey challenge and the sudo-gated register/remove
/// actions. The JS shim POSTs the ceremony JSON via fetch and sends the antiforgery token in the
/// RequestVerificationToken header, so a POST without that header is rejected by the global
/// antiforgery convention. The full WebAuthn ceremony (a real assertion/attestation) needs a virtual
/// authenticator and is covered by the Playwright E2E; here we assert options render, the antiforgery
/// requirement, and the in-handler sudo gate.
/// </summary>
public sealed class PasskeyScreenTests
{
    private static HttpClient NoRedirect(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static void SeedLogin(AuthWebFactory factory, UserRow user, string password = "password")
    {
        factory.Users.Add(user);
        factory.Credentials.SetAsync(user.Id, factory.Hasher.Hash(password), 1).GetAwaiter().GetResult();
    }

    private static async Task<string> LoginWithPasskeyAndGetNonceAsync(
        AuthWebFactory factory, HttpClient client, UserRow user)
    {
        var response = await AuthFormTestHelpers.PostFormAsync(client, "/login",
            new[] { new KeyValuePair<string, string>("email", user.Email), new("password", "password") });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login/mfa/passkey", response.Headers.Location!.OriginalString);
        return Assert.Single(
            AuthFormTestHelpers.ParseSetCookies(response), c => c.Key == SessionCookie.MfaNonceName).Value;
    }

    [Fact]
    public async Task LoginPasskeyChallengeRendersAssertionOptions()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("pk-login", mfaEnabled: true);
        SeedLogin(factory, user);
        // A registered passkey makes "passkey" the offered login factor.
        factory.WebAuthn.Seed(user.Id, Encoding.UTF8.GetBytes("cred-id"));
        using var client = NoRedirect(factory);
        var nonce = await LoginWithPasskeyAndGetNonceAsync(factory, client, user);

        using var get = new HttpRequestMessage(HttpMethod.Get, "/login/mfa/passkey");
        get.Headers.Add("Cookie", $"{SessionCookie.MfaNonceName}={nonce}");
        var render = await client.SendAsync(get);
        var html = await render.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, render.StatusCode);
        // The assertion options (with a challenge) are rendered for the JS shim plus the script tag.
        Assert.Contains("data-passkey-assert", html);
        Assert.Contains("challenge", html);
        Assert.Contains("/js/passkey.js", html);
    }

    [Fact]
    public async Task LoginPasskeyPostWithoutAntiforgeryHeaderIsRejected()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("pk-csrf", mfaEnabled: true);
        SeedLogin(factory, user);
        factory.WebAuthn.Seed(user.Id, Encoding.UTF8.GetBytes("cred-id"));
        using var client = NoRedirect(factory);
        var nonce = await LoginWithPasskeyAndGetNonceAsync(factory, client, user);

        // A passkey POST with no RequestVerificationToken header (and no antiforgery cookie) is rejected.
        using var post = new HttpRequestMessage(HttpMethod.Post, "/login/mfa/passkey")
        {
            Content = JsonContent.Create(new { assertion = "{}" }),
        };
        post.Headers.Add("Cookie", $"{SessionCookie.MfaNonceName}={nonce}");
        var response = await client.SendAsync(post);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(AuthFormTestHelpers.ParseSetCookies(response), c => c.Key == SessionCookie.Name);
    }

    [Fact]
    public async Task PasskeyRegisterWithoutRecentSudoRedirectsToSudo()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("pk-reg");
        var token = factory.SeedSession(user); // full session, but no recent sudo_at.
        using var client = NoRedirect(factory);

        using var get = new HttpRequestMessage(HttpMethod.Get, "/account/mfa/passkey");
        get.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        var response = await client.SendAsync(get);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/account/sudo", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task RecoveryCodeDisplayShowsCodesOnceThenNothing()
    {
        // The passkey register POST surfaces first-factor recovery codes through the same server-held
        // one-time display the shim follows via its JSON { redirect } response. The full attestation
        // ceremony needs a virtual authenticator (Playwright E2E), so this asserts the display seam the
        // register response targets: the codes are stashed server-side (never in the URL/cookie/client
        // storage), shown once, then read-and-cleared so a refresh shows nothing.
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("pk-display");
        var token = factory.SeedSession(user);
        await factory.Sessions.SetSudoAtAsync(AuthWebFactory.SessionIdFor(user), DateTime.UtcNow);
        using var client = NoRedirect(factory);

        var store = factory.Services.GetRequiredService<RecoveryCodeDisplayStore>();
        var nonce = store.Stash(new[] { "alpha-code", "beta-code" });

        var first = await GetDisplayAsync(client, token, nonce);
        var firstHtml = await first.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("alpha-code", firstHtml);

        var second = await GetDisplayAsync(client, token, nonce);
        var secondHtml = await second.Content.ReadAsStringAsync();
        Assert.DoesNotContain("alpha-code", secondHtml);
        Assert.Contains("no recovery codes to show", secondHtml);
    }

    private static async Task<HttpResponseMessage> GetDisplayAsync(HttpClient client, string token, string nonce)
    {
        using var get = new HttpRequestMessage(HttpMethod.Get, "/account/mfa/recovery-codes");
        get.Headers.Add("Cookie", $"{SessionCookie.Name}={token}; {SessionCookie.RecoveryDisplayName}={nonce}");
        return await client.SendAsync(get);
    }

    [Fact]
    public async Task PasskeyRegisterWithRecentSudoRendersRegistrationOptions()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("pk-reg2");
        var token = factory.SeedSession(user);
        await factory.Sessions.SetSudoAtAsync(AuthWebFactory.SessionIdFor(user), DateTime.UtcNow);
        using var client = NoRedirect(factory);

        using var get = new HttpRequestMessage(HttpMethod.Get, "/account/mfa/passkey");
        get.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        var response = await client.SendAsync(get);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-passkey-register", html);
        Assert.Contains("/js/passkey.js", html);
    }

    [Fact]
    public async Task PasskeyRemoveWithoutRecentSudoRedirectsToSudoAndKeepsCredential()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("pk-rm");
        var token = factory.SeedSession(user); // no recent sudo_at.
        // Seed a real owned credential so the test proves the sudo gate runs BEFORE the delete: if the
        // gate were skipped, the owned credential would be removed.
        var credentialId = factory.WebAuthn.Seed(user.Id, Encoding.UTF8.GetBytes("cred-id"));
        using var client = NoRedirect(factory);

        // Even with a valid antiforgery token, the in-handler sudo gate redirects before the delete.
        // The antiforgery pair is scraped from /login (anonymous, always renders a form); the pair is
        // app-wide, so it satisfies validation on the remove POST.
        var response = await AuthFormTestHelpers.PostFormAsync(client, "/account/mfa/passkey/remove",
            new[] { new KeyValuePair<string, string>("id", credentialId) },
            extraCookies: new[] { new KeyValuePair<string, string>(SessionCookie.Name, token) },
            getPath: "/login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/account/sudo", response.Headers.Location!.OriginalString);
        Assert.True(factory.WebAuthn.Exists(credentialId)); // the gate ran before the delete.
    }
}
