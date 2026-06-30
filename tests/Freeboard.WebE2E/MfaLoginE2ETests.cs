using Freeboard.Persistence.Auth;
using Freeboard.Web.Tests;
using Microsoft.Playwright;
using Xunit;

namespace Freeboard.WebE2E;

/// <summary>
/// Browser E2E for the non-passkey login-MFA factors (TOTP, magic-link, recovery), giving them the
/// same real-browser parity the passkey login flow already has. Each test drives a password login,
/// reaches the factor challenge page, completes it, and asserts a FULL session (lands on /account and
/// a protected page is reachable). Gated like the rest of the suite: with no browser /
/// FREEBOARD_TEST_E2E these skip cleanly.
///
/// Page transitions are awaited on a DOM element unique to the destination, not on
/// <c>WaitForURLAsync</c>. These auth pages render and load in under a millisecond, so a fast
/// navigation can complete before a URL-load wait attaches and then hang waiting for a load event
/// that already fired; waiting on the destination's heading polls the DOM and is race-proof.
/// </summary>
public sealed class MfaLoginE2ETests : E2ETestBase
{
    private const string Password = "mfa login password value";

    [SkippableFact]
    public async Task TotpLogin_CompletesAndLandsOnAccount()
    {
        Gate();

        // A confirmed TOTP factor (no passkey) makes TOTP the first offered factor, so the password
        // login redirects to /login/mfa/totp. The in-memory TOTP store accepts a fixed code rather than
        // a real RFC 6238 computation, so the valid current code is exactly App.Totp.ValidCode.
        var user = E2EAppFixture.MakeUser("e2e-totp", mfaEnabled: true);
        SeedLoginUser(user);
        App.Totp.ValidCode = "246802";
        App.Totp.SetConfirmed(user.Id);

        await using var context = await NewContextAsync();
        var page = await context.NewPageAsync();
        await PasswordLoginAsync(page, user);
        await ExpectPathAsync(page, "/login/mfa/totp");

        await page.FillAsync("#code", App.Totp.ValidCode);
        await page.ClickAsync("button[type=submit]");

        await AssertFullSessionAsync(page, user);
    }

    [SkippableFact]
    public async Task MagicLinkLogin_CompletesAndLandsOnAccount()
    {
        Gate();

        // No passkey and no TOTP, MFA enabled, with an email sender registered (the fixture registers
        // one), makes magic-link the offered fallback factor, so the password login redirects to
        // /login/mfa/magic-link.
        var user = E2EAppFixture.MakeUser("e2e-magic", mfaEnabled: true);
        SeedLoginUser(user);

        await using var context = await NewContextAsync();
        var page = await context.NewPageAsync();
        await PasswordLoginAsync(page, user);
        await ExpectPathAsync(page, "/login/mfa/magic-link");

        // Trigger the send, then capture the emailed token from the recording sender. The email body
        // builds the link from the configured auth base URL, not the Kestrel origin, so navigate the
        // SAME browser context to the local origin carrying that token (as the forgot/reset E2E does).
        await page.ClickAsync("button[type=submit]");
        await page.WaitForSelectorAsync(".success");
        var message = Assert.Single(App.Email.MagicLinks);
        var token = RecordingEmailSender.TokenOf(message);

        await page.GotoAsync($"{App.BaseUrl}/auth/magic-link?token={Uri.EscapeDataString(token)}");
        await ExpectPathAsync(page, "/auth/magic-link");
        // The landing GET scrubs the token out of the URL before rendering the confirm form.
        Assert.DoesNotContain("token=", page.Url, StringComparison.Ordinal);

        await page.ClickAsync("button[type=submit]");

        await AssertFullSessionAsync(page, user);
    }

    [SkippableFact]
    public async Task RecoveryCodeLogin_CompletesAndLandsOnAccount()
    {
        Gate();

        // Seeded recovery codes with no passkey/TOTP make recovery the first offered factor, so the
        // password login redirects to /login/mfa/recovery.
        var user = E2EAppFixture.MakeUser("e2e-recovery", mfaEnabled: true);
        SeedLoginUser(user);
        App.Recovery.Seed(user.Id, "recovery-code-one", "recovery-code-two");

        await using var context = await NewContextAsync();
        var page = await context.NewPageAsync();
        await PasswordLoginAsync(page, user);
        await ExpectPathAsync(page, "/login/mfa/recovery");

        await page.FillAsync("#recovery_code", "recovery-code-one");
        await page.ClickAsync("button[type=submit]");

        await AssertFullSessionAsync(page, user);
    }

    /// <summary>Seeds a user with a password credential (no session) using the shared MFA password.</summary>
    private void SeedLoginUser(UserRow user)
    {
        App.Users.Add(user);
        App.Credentials.SetAsync(user.Id, App.Hasher.Hash(Password), 1).GetAwaiter().GetResult();
    }

    /// <summary>Submits the password-login form for the seeded user (the 202 MFA-challenge path).</summary>
    private async Task PasswordLoginAsync(IPage page, UserRow user)
    {
        await page.GotoAsync($"{App.BaseUrl}/login");
        await page.FillAsync("#email", user.Email);
        await page.FillAsync("#password", Password);
        await page.ClickAsync("button[type=submit]");
    }

    /// <summary>
    /// Waits for the page to settle on <paramref name="path"/> by polling for the page heading (the one
    /// element every auth/account page renders), then asserts the URL. Race-proof for fast navigations.
    /// </summary>
    private async Task ExpectPathAsync(IPage page, string path)
    {
        await page.WaitForSelectorAsync("h1", new() { Timeout = 15000 });
        await page.WaitForFunctionAsync(
            "p => location.pathname === p", path, new() { Timeout = 15000 });
        Assert.Equal($"{App.BaseUrl}{path}", page.Url.Split('?')[0]);
    }

    /// <summary>
    /// Proves the session is FULL: the account landing shows the user, and a protected page (which
    /// carries no limited-session marker) is reachable without being redirected back to /login.
    /// </summary>
    private async Task AssertFullSessionAsync(IPage page, UserRow user)
    {
        await ExpectPathAsync(page, "/account");
        Assert.Contains(user.Email, await page.ContentAsync(), StringComparison.Ordinal);

        await page.GotoAsync($"{App.BaseUrl}/account/password/change");
        await ExpectPathAsync(page, "/account/password/change");
    }
}
