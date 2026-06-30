using System.Net;
using System.Text;
using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// The MFA-management screens: status, TOTP enroll/activate/remove, recovery regenerate, and the sudo
/// step-up interstitial. Every mutating action is sudo-gated in-handler (pipeline sudo policies do not
/// run for in-process page handlers), so a stale step-up redirects to /account/sudo and does NOT
/// perform the action. First-factor activation and regenerate surface recovery codes exactly once via
/// the server-held one-time display: the codes ride neither the URL, a cookie, nor client storage, and
/// a second view shows nothing. The full passkey register/sudo ceremony needs a virtual authenticator
/// and is covered by the Playwright E2E; here the sudo gate and option-render paths are asserted.
/// </summary>
public sealed class MfaManagementScreenTests
{
    private static HttpClient NoRedirect(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static KeyValuePair<string, string>[] SessionCookieFor(string token)
        => new[] { new KeyValuePair<string, string>(SessionCookie.Name, token) };

    private static void StampSudo(AuthWebFactory factory, UserRow user, DateTime at)
        => factory.Sessions.SetSudoAtAsync(AuthWebFactory.SessionIdFor(user), at).GetAwaiter().GetResult();

    [Fact]
    public async Task MfaStatusRendersEnrolledFactors()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("mfa-status");
        var token = factory.SeedSession(user);
        factory.Totp.SetConfirmed(user.Id);
        factory.WebAuthn.Seed(user.Id, Encoding.UTF8.GetBytes("cred-id"));
        factory.Recovery.Seed(user.Id, "code-1", "code-2", "code-3");
        using var client = NoRedirect(factory);

        using var get = new HttpRequestMessage(HttpMethod.Get, "/account/mfa");
        get.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        var response = await client.SendAsync(get);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("authenticator app is set up", html);
        Assert.Contains("Remove", html); // passkey present -> a remove control renders.
        Assert.Contains("3 recovery code(s) remaining", html);
    }

    [Fact]
    public async Task TotpEnrollActivateShowsRecoveryCodesOnceThenNothing()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("mfa-totp-enroll");
        var token = factory.SeedSession(user);
        StampSudo(factory, user, DateTime.UtcNow); // recent step-up: the gate passes.
        using var client = NoRedirect(factory);

        // An explicit begin POST stages the secret; the bare GET has no side effect.
        var enrollCookies = await BeginEnrollAsync(client, token);
        using var get = new HttpRequestMessage(HttpMethod.Get, "/account/mfa/totp");
        get.Headers.Add("Cookie", CookieHeader(token, enrollCookies));
        var enroll = await client.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);
        Assert.Contains("otpauth://", await enroll.Content.ReadAsStringAsync());

        // POST activates with the confirming code: first factor, so recovery codes are stashed and the
        // user is redirected to the one-time display page; the codes are NOT in this response body.
        var activate = await AuthFormTestHelpers.PostFormAsync(client, "/account/mfa/totp",
            new[] { new KeyValuePair<string, string>("code", "123456") },
            extraCookies: WithEnrollCookies(token, enrollCookies));
        Assert.Equal(HttpStatusCode.Redirect, activate.StatusCode);
        Assert.Equal("/account/mfa/recovery-codes", activate.Headers.Location!.OriginalString);
        var displayNonce = Assert.Single(
            AuthFormTestHelpers.ParseSetCookies(activate), c => c.Key == SessionCookie.RecoveryDisplayName).Value;

        // The display page shows the codes exactly once.
        var first = await GetDisplayAsync(client, token, displayNonce);
        var firstHtml = await first.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains($"{user.Id}-recovery-01", firstHtml);

        // A second view shows nothing: the server entry was read-and-cleared, and the nonce cookie too.
        var second = await GetDisplayAsync(client, token, displayNonce);
        var secondHtml = await second.Content.ReadAsStringAsync();
        Assert.DoesNotContain($"{user.Id}-recovery-01", secondHtml);
        Assert.Contains("no recovery codes to show", secondHtml);
    }

    [Fact]
    public async Task TotpEnrollWithoutRecentSudoRedirectsToSudo()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("mfa-totp-nosudo");
        var token = factory.SeedSession(user); // no recent sudo_at.
        using var client = NoRedirect(factory);

        using var get = new HttpRequestMessage(HttpMethod.Get, "/account/mfa/totp");
        get.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        var response = await client.SendAsync(get);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/account/sudo", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task TotpActivateWithoutRecentSudoIsNotPerformed()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("mfa-totp-noact");
        var token = factory.SeedSession(user); // no recent sudo_at.
        using var client = NoRedirect(factory);

        // A valid antiforgery pair (scraped from /login) and a valid code are not enough: the in-handler
        // sudo gate redirects before activation, so the factor must NOT be confirmed.
        var response = await AuthFormTestHelpers.PostFormAsync(client, "/account/mfa/totp",
            new[] { new KeyValuePair<string, string>("code", "123456") },
            extraCookies: SessionCookieFor(token), getPath: "/login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/account/sudo", response.Headers.Location!.OriginalString);
        Assert.False(await factory.Totp.IsConfirmedAsync(user.Id));
    }

    [Fact]
    public async Task TotpRemoveWithoutRecentSudoIsNotPerformed()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("mfa-totp-rm");
        var token = factory.SeedSession(user);
        factory.Totp.SetConfirmed(user.Id);
        using var client = NoRedirect(factory);

        var response = await AuthFormTestHelpers.PostFormAsync(client, "/account/mfa/totp/remove",
            Array.Empty<KeyValuePair<string, string>>(),
            extraCookies: SessionCookieFor(token), getPath: "/login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/account/sudo", response.Headers.Location!.OriginalString);
        Assert.True(await factory.Totp.IsConfirmedAsync(user.Id)); // the gate ran before the delete.
    }

    [Fact]
    public async Task RecoveryRegenerateShowsCodesOnceWithSudo()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("mfa-recov");
        var token = factory.SeedSession(user);
        StampSudo(factory, user, DateTime.UtcNow);
        using var client = NoRedirect(factory);

        var regen = await AuthFormTestHelpers.PostFormAsync(client, "/account/mfa/recovery",
            Array.Empty<KeyValuePair<string, string>>(),
            extraCookies: SessionCookieFor(token), getPath: "/login");
        Assert.Equal(HttpStatusCode.Redirect, regen.StatusCode);
        Assert.Equal("/account/mfa/recovery-codes", regen.Headers.Location!.OriginalString);
        var displayNonce = Assert.Single(
            AuthFormTestHelpers.ParseSetCookies(regen), c => c.Key == SessionCookie.RecoveryDisplayName).Value;

        var first = await GetDisplayAsync(client, token, displayNonce);
        Assert.Contains($"{user.Id}-recovery-01", await first.Content.ReadAsStringAsync());
        var second = await GetDisplayAsync(client, token, displayNonce);
        Assert.DoesNotContain($"{user.Id}-recovery-01", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RecoveryRegenerateWithoutRecentSudoIsNotPerformed()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("mfa-recov-nosudo");
        var token = factory.SeedSession(user); // no recent sudo_at.
        factory.Recovery.Seed(user.Id, "old-1", "old-2");
        using var client = NoRedirect(factory);

        var response = await AuthFormTestHelpers.PostFormAsync(client, "/account/mfa/recovery",
            Array.Empty<KeyValuePair<string, string>>(),
            extraCookies: SessionCookieFor(token), getPath: "/login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/account/sudo", response.Headers.Location!.OriginalString);
        // The old codes are untouched: regenerate did not run.
        Assert.Equal(2, await factory.Recovery.CountRemainingAsync(user.Id));
    }

    [Fact]
    public async Task SudoStepUpRequiredThenResumesReturnUrl()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("sudo-resume"); // non-MFA user: step up via password.
        var token = factory.SeedSession(user, password: "pw");
        using var client = NoRedirect(factory);

        // A sudo-gated action with a stale step-up funnels to /account/sudo carrying the return target.
        using var gated = new HttpRequestMessage(HttpMethod.Get, "/account/mfa/totp");
        gated.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        var redirect = await client.SendAsync(gated);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/account/sudo?returnUrl=/account/mfa/totp", redirect.Headers.Location!.OriginalString);

        // The step-up page offers the password factor; confirming it stamps sudo and resumes the target.
        var stepUp = await AuthFormTestHelpers.PostFormAsync(client, "/account/sudo?returnUrl=/account/mfa/totp",
            new[]
            {
                new KeyValuePair<string, string>("factor", "password"),
                new("password", "pw"),
                new("returnUrl", "/account/mfa/totp"),
            },
            extraCookies: SessionCookieFor(token), getPath: "/account/sudo?returnUrl=/account/mfa/totp");

        Assert.Equal(HttpStatusCode.Redirect, stepUp.StatusCode);
        Assert.Equal("/account/mfa/totp", stepUp.Headers.Location!.OriginalString);
        // sudo_at is now recent, so the gate would pass on the resumed action.
        Assert.True(await SudoRecency.IsRecentAsync(
            factory.Sessions, AuthWebFactory.SessionIdFor(user), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task SudoExpiryRePromptsTheGatedAction()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("sudo-expiry");
        var token = factory.SeedSession(user);
        // A stale step-up (older than the 5-minute TTL) must re-prompt, not pass.
        StampSudo(factory, user, DateTime.UtcNow.AddMinutes(-30));
        using var client = NoRedirect(factory);

        using var gated = new HttpRequestMessage(HttpMethod.Get, "/account/mfa/totp");
        gated.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        var response = await client.SendAsync(gated);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/account/sudo", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task SudoStepUpWrongPasswordReprompts()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("sudo-wrong");
        var token = factory.SeedSession(user, password: "pw");
        using var client = NoRedirect(factory);

        var stepUp = await AuthFormTestHelpers.PostFormAsync(client, "/account/sudo",
            new[]
            {
                new KeyValuePair<string, string>("factor", "password"),
                new("password", "wrong"),
            },
            extraCookies: SessionCookieFor(token), getPath: "/account/sudo");

        Assert.Equal(HttpStatusCode.OK, stepUp.StatusCode); // re-render with a generic error, not a redirect.
        Assert.False(await SudoRecency.IsRecentAsync(
            factory.Sessions, AuthWebFactory.SessionIdFor(user), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task SudoStepUpThenPasskeyRegisterRendersOptions()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("sudo-pk"); // non-MFA user: step up via password.
        var token = factory.SeedSession(user, password: "pw");
        using var client = NoRedirect(factory);

        // Step up, then the sudo-gated passkey register GET renders its ceremony options (the gate
        // passes). The actual create/attestation ceremony needs a virtual authenticator (E2E).
        var stepUp = await AuthFormTestHelpers.PostFormAsync(client, "/account/sudo?returnUrl=/account/mfa/passkey",
            new[]
            {
                new KeyValuePair<string, string>("factor", "password"),
                new("password", "pw"),
                new("returnUrl", "/account/mfa/passkey"),
            },
            extraCookies: SessionCookieFor(token), getPath: "/account/sudo?returnUrl=/account/mfa/passkey");
        Assert.Equal("/account/mfa/passkey", stepUp.Headers.Location!.OriginalString);

        using var get = new HttpRequestMessage(HttpMethod.Get, "/account/mfa/passkey");
        get.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        var register = await client.SendAsync(get);
        var html = await register.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        Assert.Contains("data-passkey-register", html);
        Assert.Contains("/js/passkey.js", html);
    }

    [Fact]
    public async Task TotpInvalidActivationKeepsSameSecretThenValidActivates()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("mfa-totp-retry");
        var token = factory.SeedSession(user);
        StampSudo(factory, user, DateTime.UtcNow);
        using var client = NoRedirect(factory);

        // Begin enrollment once and capture the staged provisioning URI.
        var enrollCookies = await BeginEnrollAsync(client, token);
        var stagedUri = ProvisioningUriFrom(await GetEnrollPageAsync(client, token, enrollCookies));
        Assert.Contains("otpauth://", stagedUri);

        // A wrong code re-renders the SAME provisioning URI: the staged secret is not rotated.
        var wrong = await AuthFormTestHelpers.PostFormAsync(client, "/account/mfa/totp",
            new[] { new KeyValuePair<string, string>("code", "000000") },
            extraCookies: WithEnrollCookies(token, enrollCookies));
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Equal(stagedUri, ProvisioningUriFrom(await wrong.Content.ReadAsStringAsync()));

        // The secret was staged exactly once across begin + GET + wrong retry (no rotation).
        Assert.Equal(1, factory.Totp.EnrollCount);

        // A valid code then activates against the already-staged secret.
        var activate = await AuthFormTestHelpers.PostFormAsync(client, "/account/mfa/totp",
            new[] { new KeyValuePair<string, string>("code", "123456") },
            extraCookies: WithEnrollCookies(token, enrollCookies));
        Assert.Equal(HttpStatusCode.Redirect, activate.StatusCode);
        Assert.True(await factory.Totp.IsConfirmedAsync(user.Id));
    }

    /// <summary>POSTs the begin handler to stage a secret; returns the enrollment nonce cookie(s) set.</summary>
    private static async Task<IReadOnlyList<KeyValuePair<string, string>>> BeginEnrollAsync(HttpClient client, string token)
    {
        var begin = await AuthFormTestHelpers.PostFormAsync(client, "/account/mfa/totp?handler=Begin",
            Array.Empty<KeyValuePair<string, string>>(),
            extraCookies: SessionCookieFor(token), getPath: "/account/mfa/totp");
        return AuthFormTestHelpers.ParseSetCookies(begin)
            .Where(c => c.Key == SessionCookie.TotpEnrollName).ToList();
    }

    private static KeyValuePair<string, string>[] WithEnrollCookies(
        string token, IReadOnlyList<KeyValuePair<string, string>> enrollCookies)
        => SessionCookieFor(token).Concat(enrollCookies).ToArray();

    private static string CookieHeader(string token, IReadOnlyList<KeyValuePair<string, string>> enrollCookies)
        => string.Join("; ", WithEnrollCookies(token, enrollCookies).Select(c => $"{c.Key}={c.Value}"));

    private static async Task<string> GetEnrollPageAsync(
        HttpClient client, string token, IReadOnlyList<KeyValuePair<string, string>> enrollCookies)
    {
        using var get = new HttpRequestMessage(HttpMethod.Get, "/account/mfa/totp");
        get.Headers.Add("Cookie", CookieHeader(token, enrollCookies));
        var response = await client.SendAsync(get);
        return await response.Content.ReadAsStringAsync();
    }

    private static string ProvisioningUriFrom(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(html, "otpauth://[^<\"]+");
        return match.Success ? match.Value : string.Empty;
    }

    private static async Task<HttpResponseMessage> GetDisplayAsync(HttpClient client, string token, string nonce)
    {
        using var get = new HttpRequestMessage(HttpMethod.Get, "/account/mfa/recovery-codes");
        get.Headers.Add("Cookie", $"{SessionCookie.Name}={token}; {SessionCookie.RecoveryDisplayName}={nonce}");
        return await client.SendAsync(get);
    }
}
