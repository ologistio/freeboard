using System.Net;
using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Freeboard.Web.Tests;

/// <summary>
/// The login-MFA challenge screens (TOTP, recovery, magic-link send, magic-link landing). Each factor
/// reads the held mfa_token via the nonce cookie, issues the full session on success, makes the nonce
/// single-use (a second verify with the same nonce fails), restarts at /login when the attempt cap
/// consumes the challenge, and never lets the mfa_token reach the client. The magic-link landing
/// scrubs the link token from the URL then lands-and-completes in the same browser.
/// </summary>
public sealed class MfaChallengeScreenTests
{
    private static HttpClient NoRedirect(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static void SeedLogin(AuthWebFactory factory, UserRow user, string password = "password")
    {
        factory.Users.Add(user);
        factory.Credentials.SetAsync(user.Id, factory.Hasher.Hash(password), 1).GetAwaiter().GetResult();
    }

    /// <summary>Logs the user in (forcing a 202 MFA challenge) and returns the issued nonce cookie value.</summary>
    private static async Task<string> LoginAndGetNonceAsync(AuthWebFactory factory, HttpClient client, UserRow user)
    {
        var response = await AuthFormTestHelpers.PostFormAsync(client, "/login",
            new[] { new KeyValuePair<string, string>("email", user.Email), new("password", "password") });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var nonce = Assert.Single(
            AuthFormTestHelpers.ParseSetCookies(response), c => c.Key == SessionCookie.MfaNonceName);
        return nonce.Value;
    }

    private static KeyValuePair<string, string>[] NonceCookie(string nonce)
        => new[] { new KeyValuePair<string, string>(SessionCookie.MfaNonceName, nonce) };

    [Fact]
    public async Task TotpChallengeSuccessIssuesSessionAndNonceIsSingleUse()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("mfa-totp", mfaEnabled: true);
        SeedLogin(factory, user);
        factory.Totp.SetConfirmed(user.Id);
        using var client = NoRedirect(factory);
        var nonce = await LoginAndGetNonceAsync(factory, client, user);

        var ok = await AuthFormTestHelpers.PostFormAsync(client, "/login/mfa/totp",
            new[] { new KeyValuePair<string, string>("code", "123456") }, extraCookies: NonceCookie(nonce));

        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal("/account", ok.Headers.Location!.OriginalString);
        Assert.Contains(AuthFormTestHelpers.ParseSetCookies(ok), c => c.Key == SessionCookie.Name);
        // The nonce entry was removed on success (single-use).
        Assert.Null(factory.Services.GetRequiredService<PendingMfaStore>().Peek(nonce));

        // A second verify with the same nonce cannot succeed - the entry is gone, so the GET shows the
        // restart message (no form), and the page never issues a session.
        var replay = await AuthFormTestHelpers.PostFormAsync(client, "/login/mfa/totp",
            new[] { new KeyValuePair<string, string>("code", "123456") }, extraCookies: NonceCookie(nonce));
        Assert.DoesNotContain(AuthFormTestHelpers.ParseSetCookies(replay), c => c.Key == SessionCookie.Name);
        Assert.NotEqual("/account", replay.Headers.Location?.OriginalString);

        // The GET alone confirms the restart message renders once the nonce is consumed.
        using var getAfter = new HttpRequestMessage(HttpMethod.Get, "/login/mfa/totp");
        getAfter.Headers.Add("Cookie", $"{SessionCookie.MfaNonceName}={nonce}");
        var afterRender = await client.SendAsync(getAfter);
        Assert.Contains("could not be continued", await afterRender.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RecoveryChallengeSuccessIssuesSessionAndNonceIsSingleUse()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("mfa-rec", mfaEnabled: true);
        SeedLogin(factory, user);
        factory.Recovery.Seed(user.Id, "code-one", "code-two");
        using var client = NoRedirect(factory);
        var nonce = await LoginAndGetNonceAsync(factory, client, user);

        var ok = await AuthFormTestHelpers.PostFormAsync(client, "/login/mfa/recovery",
            new[] { new KeyValuePair<string, string>("recovery_code", "code-one") }, extraCookies: NonceCookie(nonce));

        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal("/account", ok.Headers.Location!.OriginalString);
        Assert.Contains(AuthFormTestHelpers.ParseSetCookies(ok), c => c.Key == SessionCookie.Name);
        Assert.Null(factory.Services.GetRequiredService<PendingMfaStore>().Peek(nonce));

        var replay = await AuthFormTestHelpers.PostFormAsync(client, "/login/mfa/recovery",
            new[] { new KeyValuePair<string, string>("recovery_code", "code-two") }, extraCookies: NonceCookie(nonce));
        Assert.DoesNotContain(AuthFormTestHelpers.ParseSetCookies(replay), c => c.Key == SessionCookie.Name);
        Assert.NotEqual("/account", replay.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task TotpAttemptCapExhaustionReturnsToLogin()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("mfa-cap", mfaEnabled: true);
        SeedLogin(factory, user);
        factory.Totp.SetConfirmed(user.Id);
        using var client = NoRedirect(factory);
        var nonce = await LoginAndGetNonceAsync(factory, client, user);

        // The shared challenge consumes itself after the 5th failed attempt; the page then restarts login.
        var last = await AuthFormTestHelpers.PostFormAsync(client, "/login/mfa/totp",
            new[] { new KeyValuePair<string, string>("code", "000000") }, extraCookies: NonceCookie(nonce));
        for (var i = 1; i < 5; i++)
        {
            last = await AuthFormTestHelpers.PostFormAsync(client, "/login/mfa/totp",
                new[] { new KeyValuePair<string, string>("code", "000000") }, extraCookies: NonceCookie(nonce));
        }

        Assert.Equal(HttpStatusCode.Redirect, last.StatusCode);
        Assert.Equal("/login", last.Headers.Location!.OriginalString);
        Assert.Null(factory.Services.GetRequiredService<PendingMfaStore>().Peek(nonce));
    }

    [Fact]
    public async Task MagicLinkLandingAttemptCapExhaustionReturnsToLogin()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        var user = AuthWebFactory.MakeUser("ml-cap", mfaEnabled: true);
        SeedLogin(factory, user);
        using var client = NoRedirect(factory);
        var nonce = await LoginAndGetNonceAsync(factory, client, user);

        // Send a real link so a magic-link challenge exists, then drive the landing POST with a WRONG
        // link cookie. The shared challenge consumes itself after the 5th failed attempt, so the
        // landing restarts login - mirroring the TOTP/recovery cap cleanup.
        await AuthFormTestHelpers.PostFormAsync(client, "/login/mfa/magic-link",
            Array.Empty<KeyValuePair<string, string>>(), extraCookies: NonceCookie(nonce));

        var badCookies = new[]
        {
            new KeyValuePair<string, string>(SessionCookie.MfaNonceName, nonce),
            new(SessionCookie.MagicLinkTokenName, "not-the-real-token"),
        };

        HttpResponseMessage? last = null;
        for (var i = 0; i < 5; i++)
        {
            last = await AuthFormTestHelpers.PostFormAsync(client, "/auth/magic-link",
                Array.Empty<KeyValuePair<string, string>>(), extraCookies: badCookies);
        }

        Assert.Equal(HttpStatusCode.Redirect, last!.StatusCode);
        Assert.Equal("/login", last.Headers.Location!.OriginalString);
        // The cap consumed the challenge: the nonce entry is dropped and both transient cookies cleared.
        Assert.Null(factory.Services.GetRequiredService<PendingMfaStore>().Peek(nonce));
        Assert.True(AuthFormTestHelpers.ClearsCookie(last, SessionCookie.MfaNonceName));
        Assert.True(AuthFormTestHelpers.ClearsCookie(last, SessionCookie.MagicLinkTokenName));
    }

    [Fact]
    public async Task LocalReturnUrlResumedAfterMfaCompletion()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("ret-local", mfaEnabled: true);
        SeedLogin(factory, user);
        factory.Totp.SetConfirmed(user.Id);
        using var client = NoRedirect(factory);

        // Login carrying a LOCAL returnUrl (the form's hidden field): the target is held server-side
        // with the mfa_token so it survives the MFA step.
        var login = await AuthFormTestHelpers.PostFormAsync(client, "/login",
            new[]
            {
                new KeyValuePair<string, string>("email", user.Email),
                new("password", "password"),
                new("returnUrl", "/account/sessions"),
            });
        var nonce = Assert.Single(
            AuthFormTestHelpers.ParseSetCookies(login), c => c.Key == SessionCookie.MfaNonceName).Value;

        var ok = await AuthFormTestHelpers.PostFormAsync(client, "/login/mfa/totp",
            new[] { new KeyValuePair<string, string>("code", "123456") }, extraCookies: NonceCookie(nonce));

        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal("/account/sessions", ok.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task OffSiteReturnUrlDroppedToAccountAfterMfaCompletion()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("ret-offsite", mfaEnabled: true);
        SeedLogin(factory, user);
        factory.Totp.SetConfirmed(user.Id);
        using var client = NoRedirect(factory);

        // An off-site returnUrl is rejected at login (sanitized to /account), so MFA completion lands
        // on /account, never the attacker's host.
        var login = await AuthFormTestHelpers.PostFormAsync(client, "/login",
            new[]
            {
                new KeyValuePair<string, string>("email", user.Email),
                new("password", "password"),
                new("returnUrl", "https://evil.example/x"),
            });
        var nonce = Assert.Single(
            AuthFormTestHelpers.ParseSetCookies(login), c => c.Key == SessionCookie.MfaNonceName).Value;

        var ok = await AuthFormTestHelpers.PostFormAsync(client, "/login/mfa/totp",
            new[] { new KeyValuePair<string, string>("code", "123456") }, extraCookies: NonceCookie(nonce));

        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal("/account", ok.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task MagicLinkSendRendersUniformConfirmation()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        // No strong factor, MFA enabled, sender registered -> magic-link is the offered factor.
        var user = AuthWebFactory.MakeUser("mfa-ml", mfaEnabled: true);
        SeedLogin(factory, user);
        using var client = NoRedirect(factory);
        var nonce = await LoginAndGetNonceAsync(factory, client, user);

        var sent = await AuthFormTestHelpers.PostFormAsync(client, "/login/mfa/magic-link",
            Array.Empty<KeyValuePair<string, string>>(), extraCookies: NonceCookie(nonce));

        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Contains("check your email", (await sent.Content.ReadAsStringAsync()).ToLowerInvariant());
        Assert.Single(factory.Email.MagicLinks);
    }

    [Fact]
    public async Task MagicLinkLandingScrubsTokenThenLandsAndCompletesInSameBrowser()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        var user = AuthWebFactory.MakeUser("mfa-land", mfaEnabled: true);
        SeedLogin(factory, user);
        using var client = NoRedirect(factory);
        var nonce = await LoginAndGetNonceAsync(factory, client, user);

        // Send the link, then pull the emailed token.
        await AuthFormTestHelpers.PostFormAsync(client, "/login/mfa/magic-link",
            Array.Empty<KeyValuePair<string, string>>(), extraCookies: NonceCookie(nonce));
        var token = RecordingEmailSender.TokenOf(factory.Email.MagicLinks.Single());

        // The landing GET scrubs the token out of the URL: 302 to the bare path, token moved to a cookie.
        using var landingRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/auth/magic-link?token={Uri.EscapeDataString(token)}");
        landingRequest.Headers.Add("Cookie", $"{SessionCookie.MfaNonceName}={nonce}");
        var landing = await client.SendAsync(landingRequest);
        Assert.Equal(HttpStatusCode.Redirect, landing.StatusCode);
        Assert.Equal("/auth/magic-link", landing.Headers.Location!.OriginalString);
        Assert.DoesNotContain("token", landing.Headers.Location.OriginalString, StringComparison.Ordinal);
        var linkCookie = Assert.Single(
            AuthFormTestHelpers.ParseSetCookies(landing), c => c.Key == SessionCookie.MagicLinkTokenName);

        // POST in the same browser (nonce + scrubbed link cookie) completes the verify and issues the session.
        var done = await AuthFormTestHelpers.PostFormAsync(client, "/auth/magic-link",
            Array.Empty<KeyValuePair<string, string>>(),
            extraCookies: new[]
            {
                new KeyValuePair<string, string>(SessionCookie.MfaNonceName, nonce),
                new(SessionCookie.MagicLinkTokenName, linkCookie.Value),
            });

        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        Assert.Equal("/account", done.Headers.Location!.OriginalString);
        Assert.Contains(AuthFormTestHelpers.ParseSetCookies(done), c => c.Key == SessionCookie.Name);
        Assert.Null(factory.Services.GetRequiredService<PendingMfaStore>().Peek(nonce));
    }

    [Fact]
    public async Task MagicLinkLandingWithNoPendingContextShowsRestart()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        using var client = NoRedirect(factory);

        // No nonce cookie and no scrubbed link cookie: the landing GET renders the restart message.
        var landing = await client.GetAsync("/auth/magic-link");

        Assert.Equal(HttpStatusCode.OK, landing.StatusCode);
        Assert.Contains("could not be used here", await landing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MfaTokenNeverAppearsInChallengeResponses()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("mfa-leak", mfaEnabled: true);
        SeedLogin(factory, user);
        factory.Totp.SetConfirmed(user.Id);
        using var client = NoRedirect(factory);
        var nonce = await LoginAndGetNonceAsync(factory, client, user);

        var mfaToken = factory.Services.GetRequiredService<PendingMfaStore>().Peek(nonce);
        Assert.False(string.IsNullOrEmpty(mfaToken));

        // The GET form render must not carry the held mfa_token anywhere client-visible.
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, "/login/mfa/totp");
        getRequest.Headers.Add("Cookie", $"{SessionCookie.MfaNonceName}={nonce}");
        var get = await client.SendAsync(getRequest);

        var headers = string.Join("\n", get.Headers.Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));
        Assert.DoesNotContain(mfaToken, headers, StringComparison.Ordinal);
        Assert.DoesNotContain(mfaToken, await get.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
