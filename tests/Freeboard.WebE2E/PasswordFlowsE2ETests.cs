using Freeboard.Web.Tests;
using Microsoft.Playwright;
using Xunit;
using Freeboard.TestInfrastructure;

namespace Freeboard.WebE2E;

/// <summary>
/// Browser E2E for the non-WebAuthn auth flows: password login, forgot/reset round-trip, forced
/// reset, and session revoke. All gated: a plain <c>dotnet test</c> with no browser skips them.
/// </summary>
[Trait("Category", TestCategories.E2E)]
public sealed class PasswordFlowsE2ETests : E2ETestBase
{
    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task PasswordLogin_LandsOnAccount()
    {
        Gate();
        SeedUserWithPassword("alice", "correct horse battery");

        await using var context = await NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{App.BaseUrl}/login");

        await page.FillAsync("#email", "alice@example.com");
        await page.FillAsync("#password", "correct horse battery");
        await page.ClickAsync("button[type=submit]");

        await page.WaitForURLAsync($"{App.BaseUrl}/account");
        Assert.Contains("alice@example.com", await page.ContentAsync(), StringComparison.Ordinal);
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task ForgotPassword_ResetRoundTrip_LetsUserLogInWithNewPassword()
    {
        Gate();
        SeedUserWithPassword("bob", "old password value");

        await using var context = await NewContextAsync();
        var page = await context.NewPageAsync();

        // Request a reset link.
        await page.GotoAsync($"{App.BaseUrl}/forgot-password");
        await page.FillAsync("#email", "bob@example.com");
        await page.ClickAsync("button[type=submit]");
        await page.WaitForSelectorAsync(".success");

        // Capture the emailed reset token from the in-memory recording sender (no real SMTP).
        var message = Assert.Single(App.Email.PasswordResets);
        var token = RecordingEmailSender.TokenOf(message);

        // Follow the emailed link: the GET scrubs the token out of the URL, then the form sets a new
        // password.
        await page.GotoAsync($"{App.BaseUrl}/reset-password?token={Uri.EscapeDataString(token)}");
        await page.WaitForURLAsync($"{App.BaseUrl}/reset-password");
        Assert.DoesNotContain("token=", page.Url, StringComparison.Ordinal);

        await page.FillAsync("#new_password", "brand new password");
        await page.ClickAsync("button[type=submit]");
        await page.WaitForSelectorAsync(".success");

        // The new password works; the old one no longer does.
        await page.GotoAsync($"{App.BaseUrl}/login");
        await page.FillAsync("#email", "bob@example.com");
        await page.FillAsync("#password", "brand new password");
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync($"{App.BaseUrl}/account");
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task ForcedReset_CompletesAndUpgradesSessionToFull()
    {
        Gate();
        SeedUserWithPassword("carol", "temp password 123", forceReset: true);

        await using var context = await NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{App.BaseUrl}/login");
        await page.FillAsync("#email", "carol@example.com");
        await page.FillAsync("#password", "temp password 123");
        await page.ClickAsync("button[type=submit]");

        // A force-reset-limited session is funnelled to set a new password before anything else.
        await page.WaitForURLAsync($"{App.BaseUrl}/account/complete-reset");

        // Set the new password; on success the session is upgraded to full and lands on /account.
        await page.FillAsync("#new_password", "carol fresh password");
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync($"{App.BaseUrl}/account");

        // Prove the session is now FULL, not still force-reset-limited: a limited session is funnelled
        // to /account/complete-reset on any other protected page, but a full session reaches the
        // change-password page (which carries no limited-session-allowed marker).
        await page.GotoAsync($"{App.BaseUrl}/account/password/change");
        await page.WaitForURLAsync($"{App.BaseUrl}/account/password/change");
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task RevokeAllSessions_LogsOutOfProtectedPages()
    {
        Gate();
        SeedUserWithPassword("dave", "dave password value");

        await using var context = await NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{App.BaseUrl}/login");
        await page.FillAsync("#email", "dave@example.com");
        await page.FillAsync("#password", "dave password value");
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync($"{App.BaseUrl}/account");

        await page.GotoAsync($"{App.BaseUrl}/account/sessions");
        await page.ClickAsync("button:has-text('Sign out everywhere')");

        // After revoking the current session the cookie is cleared, so the next protected nav is
        // redirected to /login.
        await page.GotoAsync($"{App.BaseUrl}/account");
        await page.WaitForURLAsync(u => u.Contains("/login", StringComparison.Ordinal));
    }
}
