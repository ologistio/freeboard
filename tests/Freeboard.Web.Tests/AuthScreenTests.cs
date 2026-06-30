using System.Net;
using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Freeboard.Web.Tests;

/// <summary>
/// The login / logout / password / forgot / reset / forced-reset web screens. Asserts the
/// cookie/redirect behaviour, that the login mfa_token never reaches the client, enumeration-safety
/// (generic errors, uniform forgot), the reset-token scrub, and open-redirect rejection.
/// </summary>
public sealed class AuthScreenTests
{
    private static HttpClient NoRedirect(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static void SeedLogin(AuthWebFactory factory, UserRow user, string password = "password")
    {
        factory.Users.Add(user);
        factory.Credentials.SetAsync(user.Id, factory.Hasher.Hash(password), 1).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task LoginWithoutMfaSetsSessionCookieAndRedirectsToAccount()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("lg1");
        SeedLogin(factory, user);
        using var client = NoRedirect(factory);

        var response = await AuthFormTestHelpers.PostFormAsync(client, "/login",
            new[] { new KeyValuePair<string, string>("email", user.Email), new("password", "password") });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account", response.Headers.Location!.OriginalString);
        var cookies = AuthFormTestHelpers.ParseSetCookies(response);
        Assert.Contains(cookies, c => c.Key == SessionCookie.Name);
    }

    [Fact]
    public async Task LoginMfaRequiredSetsNonceCookieRedirectsToChallengeAndNeverLeaksMfaToken()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("lg2", mfaEnabled: true);
        SeedLogin(factory, user);
        factory.Totp.SetConfirmed(user.Id); // makes TOTP an available factor

        using var client = NoRedirect(factory);
        var response = await AuthFormTestHelpers.PostFormAsync(client, "/login",
            new[] { new KeyValuePair<string, string>("email", user.Email), new("password", "password") });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login/mfa/", response.Headers.Location!.OriginalString);

        var cookies = AuthFormTestHelpers.ParseSetCookies(response);
        var nonce = Assert.Single(cookies, c => c.Key == SessionCookie.MfaNonceName);
        // No session cookie yet (not fully authenticated).
        Assert.DoesNotContain(cookies, c => c.Key == SessionCookie.Name);

        // The ACTUAL server-held mfa_token (recovered via the nonce) must not leak: it is what the
        // browser would need to complete the verify, so assert it appears nowhere client-side.
        var mfaToken = factory.Services.GetRequiredService<PendingMfaStore>().Peek(nonce.Value);
        Assert.False(string.IsNullOrEmpty(mfaToken));

        var headers = string.Join("\n", response.Headers.Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));
        Assert.DoesNotContain(mfaToken, headers, StringComparison.Ordinal);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(mfaToken, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginWithInvalidCredentialsShowsGenericMessage()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("lg3");
        SeedLogin(factory, user);
        using var client = NoRedirect(factory);

        var response = await AuthFormTestHelpers.PostFormAsync(client, "/login",
            new[] { new KeyValuePair<string, string>("email", user.Email), new("password", "wrong") });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid email or password", html);
    }

    [Fact]
    public async Task LoginWhenRateLimitedShowsTheSameGenericMessage()
    {
        using var factory = new AuthWebFactory();
        factory.RateLimit.ForceLimited = true;
        var user = AuthWebFactory.MakeUser("lg4");
        SeedLogin(factory, user);
        using var client = NoRedirect(factory);

        var response = await AuthFormTestHelpers.PostFormAsync(client, "/login",
            new[] { new KeyValuePair<string, string>("email", user.Email), new("password", "password") });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Invalid email or password", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LoginForceResetFunnelsToCompleteReset()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("lg5", forcePasswordReset: true);
        SeedLogin(factory, user);
        using var client = NoRedirect(factory);

        var response = await AuthFormTestHelpers.PostFormAsync(client, "/login",
            new[] { new KeyValuePair<string, string>("email", user.Email), new("password", "password") });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/complete-reset", response.Headers.Location!.OriginalString);
        // The session cookie is still set so the limited session is authenticated to complete the reset.
        Assert.Contains(AuthFormTestHelpers.ParseSetCookies(response), c => c.Key == SessionCookie.Name);
    }

    [Theory]
    [InlineData("https://evil.example/phish")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    public async Task LoginRejectsOffSiteReturnUrl(string returnUrl)
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("lg6");
        SeedLogin(factory, user);
        using var client = NoRedirect(factory);

        var response = await AuthFormTestHelpers.PostFormAsync(client, "/login",
            new[]
            {
                new KeyValuePair<string, string>("email", user.Email),
                new("password", "password"),
                new("returnUrl", returnUrl),
            });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task LoginHonoursLocalReturnUrl()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("lg7");
        SeedLogin(factory, user);
        using var client = NoRedirect(factory);

        var response = await AuthFormTestHelpers.PostFormAsync(client, "/login",
            new[]
            {
                new KeyValuePair<string, string>("email", user.Email),
                new("password", "password"),
                new("returnUrl", "/account/sessions"),
            });

        Assert.Equal("/account/sessions", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task ForgotPasswordRendersIdenticalConfirmationForKnownAndUnknownAccount()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        var user = AuthWebFactory.MakeUser("fp1");
        SeedLogin(factory, user);
        using var client = NoRedirect(factory);

        var known = await AuthFormTestHelpers.PostFormAsync(client, "/forgot-password",
            new[] { new KeyValuePair<string, string>("email", user.Email) });
        var unknown = await AuthFormTestHelpers.PostFormAsync(client, "/forgot-password",
            new[] { new KeyValuePair<string, string>("email", "nobody@example.com") });

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        // A real account got a link; an unknown one did not - the page body is identical regardless.
        Assert.Single(factory.Email.PasswordResets);

        // No header-level enumeration leak either: not just the NAME set but the VALUES of the
        // non-excluded headers and the raw Set-Cookie strings (value + attributes) must match between
        // branches. A same-name but branch-dependent value now fails the test. Only Date (varies per
        // response) and the request-scoped .AspNetCore.Antiforgery.* cookie are excluded.
        Assert.Equal(HeaderLines(known), HeaderLines(unknown));
        Assert.Equal(SetCookieLines(known), SetCookieLines(unknown));
    }

    private static IReadOnlyList<string> HeaderLines(HttpResponseMessage response)
        => response.Headers
            .Concat(response.Content.Headers)
            // Date varies per response, not per branch; Set-Cookie is compared separately (raw).
            .Where(h => !string.Equals(h.Key, "Date", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(h.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
            .Select(h => $"{h.Key}: {string.Join(",", h.Value)}")
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> SetCookieLines(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            return [];
        }

        return setCookies
            // The antiforgery cookie is per-request, not branch-dependent; compare the rest verbatim
            // (name, value, and every attribute) so a branch-dependent value or attribute fails.
            .Where(raw => !raw.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal))
            .OrderBy(raw => raw, StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public async Task ResetLandingScrubsTokenFromUrlThenResetsWithValidToken()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        var user = AuthWebFactory.MakeUser("rp1");
        SeedLogin(factory, user);
        using var client = NoRedirect(factory);

        // Mint a real reset token via forgot-password and pull it from the captured email.
        await AuthFormTestHelpers.PostFormAsync(client, "/forgot-password",
            new[] { new KeyValuePair<string, string>("email", user.Email) });
        var token = RecordingEmailSender.TokenOf(factory.Email.PasswordResets.Single());

        // The landing GET scrubs the token: 302 to the bare path, token moved into a cookie.
        var landing = await client.GetAsync($"/reset-password?token={Uri.EscapeDataString(token)}");
        Assert.Equal(HttpStatusCode.Redirect, landing.StatusCode);
        Assert.Equal("/reset-password", landing.Headers.Location!.OriginalString);
        var resetCookie = Assert.Single(
            AuthFormTestHelpers.ParseSetCookies(landing), c => c.Key == SessionCookie.ResetTokenName);
        Assert.DoesNotContain("token", landing.Headers.Location.OriginalString, StringComparison.Ordinal);

        // POST the new password carrying the scrubbed cookie; the token is consumed.
        var posted = await AuthFormTestHelpers.PostFormAsync(client, "/reset-password",
            new[] { new KeyValuePair<string, string>("new_password", "brand-new") },
            extraCookies: new[] { new KeyValuePair<string, string>(SessionCookie.ResetTokenName, resetCookie.Value) });

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.Contains("has been reset", await posted.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ResetWithInvalidTokenShowsGenericMessage()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        using var client = NoRedirect(factory);

        var posted = await AuthFormTestHelpers.PostFormAsync(client, "/reset-password",
            new[] { new KeyValuePair<string, string>("new_password", "brand-new") },
            extraCookies: new[] { new KeyValuePair<string, string>(SessionCookie.ResetTokenName, "not-a-real-token") });

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        Assert.Contains("expired or invalid", await posted.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ChangePasswordSucceedsForCorrectOldPassword()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("cp1");
        var token = factory.SeedSession(user, password: "old-password");
        using var client = NoRedirect(factory);

        var response = await AuthFormTestHelpers.PostFormAsync(client, "/account/password/change",
            new[]
            {
                new KeyValuePair<string, string>("old_password", "old-password"),
                new("new_password", "new-password"),
            },
            extraCookies: new[] { new KeyValuePair<string, string>(SessionCookie.Name, token) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("has been changed", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ChangePasswordWithWrongOldPasswordShowsGenericMessage()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("cp2");
        var token = factory.SeedSession(user, password: "old-password");
        using var client = NoRedirect(factory);

        var response = await AuthFormTestHelpers.PostFormAsync(client, "/account/password/change",
            new[]
            {
                new KeyValuePair<string, string>("old_password", "wrong"),
                new("new_password", "new-password"),
            },
            extraCookies: new[] { new KeyValuePair<string, string>(SessionCookie.Name, token) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("current password is incorrect", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ForcedResetCompletionUpgradesTheSessionToFull()
    {
        // The completion page drives AuthFlows.AccountPasswordAsync (the same flow the API's
        // account/password uses): a force-reset-limited session sets a new password, the flow clears
        // the force-reset flag and upgrades THIS session to full. The web page is the caller; this
        // asserts the flow it invokes performs the upgrade.
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("fr1", forcePasswordReset: true);
        factory.SeedSession(user, SessionAuthState.ForceResetLimited);
        var sessionId = AuthWebFactory.SessionIdFor(user);

        var result = await AuthFlows.AccountPasswordAsync(
            user.Id, sessionId, isForceResetLimited: true, "fresh-password",
            factory.Users, factory.Credentials, factory.Hasher, factory.Services, default);

        Assert.IsType<AuthFlows.PasswordResult.Ok>(result);
        var session = await factory.Sessions.GetByIdAsync(sessionId);
        Assert.Equal(SessionAuthState.Full, session!.AuthState);
        Assert.False((await factory.Users.GetByIdAsync(user.Id))!.ForcePasswordReset);
    }

    [Fact]
    public async Task LimitedSessionCanReachCompleteResetPage()
    {
        // The force-reset guard now allows a limited session onto /account/complete-reset (the marker
        // on that page route), so the funnel page is HTTP-reachable instead of 403'd.
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("fr2", forcePasswordReset: true);
        var token = factory.SeedSession(user, SessionAuthState.ForceResetLimited);
        using var client = NoRedirect(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/account/complete-reset");
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LogoutClearsCookieEvenWhenServerDeleteIsANoOp()
    {
        using var factory = new AuthWebFactory();
        using var client = NoRedirect(factory);

        // No session is seeded, so the server-side delete is a no-op; the cookie must still be cleared.
        // Scrape the antiforgery token from /login (the /logout GET only redirects, so it has no form).
        var response = await AuthFormTestHelpers.PostFormAsync(client, "/logout",
            Array.Empty<KeyValuePair<string, string>>(), getPath: "/login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(AuthFormTestHelpers.ClearsCookie(response, SessionCookie.Name));
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData(null)]
    public void IsLocalRejectsOffSiteAndEmptyUrls(string? url)
        => Assert.False(LocalRedirect.IsLocal(url));

    [Theory]
    [InlineData("/account")]
    [InlineData("/account/sessions")]
    [InlineData("~/account")]
    public void IsLocalAcceptsRelativePaths(string url)
        => Assert.True(LocalRedirect.IsLocal(url));
}
