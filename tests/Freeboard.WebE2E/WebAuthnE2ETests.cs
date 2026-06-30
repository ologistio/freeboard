using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace Freeboard.WebE2E;

/// <summary>
/// Browser E2E for the WebAuthn ceremonies driven through the CDP virtual authenticator, so no real
/// hardware key is needed. Gated like the rest of the suite: with no browser / no
/// <c>FREEBOARD_TEST_E2E</c> these skip cleanly.
///
/// The virtual authenticator is installed via Chrome DevTools Protocol
/// (<c>WebAuthn.enable</c> + <c>WebAuthn.addVirtualAuthenticator</c>), which makes
/// <c>navigator.credentials.create/get</c> resolve in headless Chromium with a software key. The
/// app's RP id and origin are pinned to the exact Kestrel origin (see <c>E2EAppFixture</c>) so
/// Fido2's origin check passes.
/// </summary>
public sealed class WebAuthnE2ETests : E2ETestBase
{
    [SkippableFact]
    public async Task PasskeyRegister_AddsACredential()
    {
        Gate();

        await using var context = await NewContextAsync();
        await SignInWithRecentSudoAsync(context, "pkreg");

        var page = await context.NewPageAsync();
        await AddVirtualAuthenticatorAsync(page);

        await page.GotoAsync($"{App.BaseUrl}/account/mfa/passkey");
        await page.ClickAsync("[data-passkey-go]");

        // The shim follows the server's JSON { redirect } on success. This passkey is the user's first
        // strong factor, so the redirect lands on the one-time recovery-codes display. Waiting for that
        // distinct page (not a substring of the current /account/mfa/passkey URL) proves the POST
        // completed before asserting the credential was stored.
        await page.WaitForURLAsync(u =>
            u.Contains("/account/mfa/recovery-codes", StringComparison.Ordinal), new() { Timeout = 15000 });
        var registered = await App.WebAuthn.ListByUserAsync("pkreg");
        Assert.NotEmpty(registered);
    }

    [SkippableFact]
    public async Task PasskeyLoginChallenge_CompletesWithRegisteredCredential()
    {
        Gate();

        // The user has MFA enabled and a password, so a password login yields the passkey challenge.
        const string password = "pklogin password value";

        await using var context = await NewContextAsync();
        var page = await context.NewPageAsync();
        var authenticator = await AddVirtualAuthenticatorAsync(page);

        // Register a passkey first (under a fresh-sudo session) so the user has a credential to assert.
        // Seed the sudo session with the SAME password so the later real login still verifies.
        await AddSessionCookieAsync(
            context, App.SeedSessionWithSudo(E2EAppFixture.MakeUser("pklogin", mfaEnabled: true), password));
        await page.GotoAsync($"{App.BaseUrl}/account/mfa/passkey");
        await page.ClickAsync("[data-passkey-go]");
        // First strong factor -> the one-time recovery-codes display; this distinct URL proves the
        // registration POST completed (it is not a substring of the current /account/mfa/passkey URL).
        await page.WaitForURLAsync(
            u => u.Contains("/account/mfa/recovery-codes", StringComparison.Ordinal), new() { Timeout = 15000 });

        // Drop the registration session so the next sign-in is a fresh, unauthenticated login.
        await context.ClearCookiesAsync();

        // Do a REAL password login. An MFA-enabled user with a passkey yields a 202 challenge and is
        // redirected to the passkey login page (the mfa_token is held server-side via the nonce cookie).
        await page.GotoAsync($"{App.BaseUrl}/login");
        await page.FillAsync("#email", "pklogin@example.com");
        await page.FillAsync("#password", password);
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync($"{App.BaseUrl}/login/mfa/passkey", new() { Timeout = 15000 });

        // Complete the passkey assertion via the virtual authenticator; success mints the full session
        // and lands on /account.
        await page.ClickAsync("[data-passkey-go]");
        await page.WaitForURLAsync($"{App.BaseUrl}/account", new() { Timeout = 15000 });
        Assert.Contains("pklogin@example.com", await page.ContentAsync(), StringComparison.Ordinal);
        Assert.NotNull(authenticator);
    }

    [SkippableFact]
    public async Task PasskeySudoStepUp_RaisesSudo()
    {
        Gate();

        await using var context = await NewContextAsync();
        var page = await context.NewPageAsync();
        await AddVirtualAuthenticatorAsync(page);

        // Register a passkey under the seeded fresh-sudo session so the user has a credential to assert.
        await SignInWithRecentSudoAsync(context, "pksudo");
        await page.GotoAsync($"{App.BaseUrl}/account/mfa/passkey");
        await page.ClickAsync("[data-passkey-go]");
        // First strong factor -> recovery-codes display; this distinct URL proves the registration POST
        // completed (it is not a substring of the current /account/mfa/passkey URL).
        await page.WaitForURLAsync(
            u => u.Contains("/account/mfa/recovery-codes", StringComparison.Ordinal), new() { Timeout = 15000 });

        // Make this session's sudo STALE so a sudo-gated action must step up again. A 10-minute-old
        // stamp is older than the default 5-minute sudo TTL.
        var sessionId = SudoSessionId("pksudo");
        await App.Sessions.SetSudoAtAsync(sessionId, DateTime.UtcNow.AddMinutes(-10));
        var staleSudoAt = (await App.Sessions.GetByIdAsync(sessionId))!.SudoAt;

        // A sudo-gated action with stale sudo is funnelled to the step-up page (it does not perform
        // the action).
        await page.GotoAsync($"{App.BaseUrl}/account/mfa/passkey");
        await page.WaitForURLAsync(u => u.Contains("/account/sudo", StringComparison.Ordinal), new() { Timeout = 15000 });

        // Complete the passkey step-up. On success the shim follows the server's JSON { redirect }
        // back to the gated action and the step-up page is gone; a FAILED verify re-renders at
        // /account/sudo (whose returnUrl query still contains the target substring), so wait for the
        // exact register page and require we are no longer on /account/sudo.
        await page.ClickAsync("[data-passkey-assert] [data-passkey-go]");
        await page.WaitForURLAsync(
            $"{App.BaseUrl}/account/mfa/passkey", new() { Timeout = 15000 });

        // Assert sudo actually advanced: the session's sudo_at is now newer than the stale stamp.
        var freshSudoAt = (await App.Sessions.GetByIdAsync(sessionId))!.SudoAt;
        Assert.NotNull(freshSudoAt);
        Assert.True(freshSudoAt > staleSudoAt, "Sudo step-up did not advance sudo_at.");
    }

    /// <summary>
    /// Installs an internal CTAP2 virtual authenticator with resident keys + user verification, so the
    /// create/get ceremonies resolve without hardware. Returns the authenticator id.
    /// </summary>
    private static async Task<string> AddVirtualAuthenticatorAsync(IPage page)
    {
        var client = await page.Context.NewCDPSessionAsync(page);
        await client.SendAsync("WebAuthn.enable");
        var result = await client.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
        {
            ["options"] = new Dictionary<string, object>
            {
                ["protocol"] = "ctap2",
                ["transport"] = "internal",
                ["hasResidentKey"] = true,
                ["hasUserVerification"] = true,
                ["isUserVerified"] = true,
                ["automaticPresenceSimulation"] = true,
            },
        });

        return result is JsonElement json && json.TryGetProperty("authenticatorId", out var id)
            ? id.GetString() ?? string.Empty
            : string.Empty;
    }
}
