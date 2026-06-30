using Freeboard.Web.Tests;
using Microsoft.Playwright;
using Xunit;
using Freeboard.TestInfrastructure;

namespace Freeboard.WebE2E;

/// <summary>
/// Browser E2E for the admin user-management pages. Gated: a plain <c>dotnet test</c> with no browser
/// skips. The temp-password path proves the displayed value is the credential that was set (the new
/// user logs in with it). The invite path reads the link from the in-memory recording sender (no real
/// SMTP), so both tests are gated only on the browser/env.
/// </summary>
[Trait("Category", TestCategories.E2E)]
public sealed class AdminUserPagesE2ETests : E2ETestBase
{
    private void SeedAdminWithPassword(string id, string password)
    {
        var admin = E2EAppFixture.MakeUser(id, role: "admin");
        App.Users.Add(admin);
        App.Credentials.SetAsync(admin.Id, App.Hasher.Hash(password), 1).GetAwaiter().GetResult();
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task AdminCreatesUser_SeesTempPasswordOnce_NewUserCanLogIn()
    {
        Gate();
        SeedAdminWithPassword("rootadmin", "admin password value");

        await using var context = await NewContextAsync();
        var page = await context.NewPageAsync();

        // Log in as the admin.
        await page.GotoAsync($"{App.BaseUrl}/login");
        await page.FillAsync("#email", "rootadmin@example.com");
        await page.FillAsync("#password", "admin password value");
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync($"{App.BaseUrl}/account");

        // Create a user via the temp-password handoff.
        await page.GotoAsync($"{App.BaseUrl}/admin/users");
        await page.FillAsync("#email", "newhire@example.com");
        await page.FillAsync("#name", "New Hire");
        await page.CheckAsync("input[name=handoff][value=temp]");
        await page.ClickAsync("button:has-text('Create user')");

        // The one-time display page shows the temp password once.
        await page.WaitForURLAsync($"{App.BaseUrl}/admin/usercredential");
        var tempPassword = (await page.InnerTextAsync(".temp-password")).Trim();
        Assert.False(string.IsNullOrEmpty(tempPassword));

        // A refresh of the display page shows nothing.
        await page.GotoAsync($"{App.BaseUrl}/admin/usercredential");
        Assert.Equal(0, await page.Locator(".temp-password").CountAsync());

        // The new user appears in the list.
        await page.GotoAsync($"{App.BaseUrl}/admin/users");
        Assert.Contains("newhire@example.com", await page.ContentAsync(), StringComparison.Ordinal);

        // Log out, then log in as the new user with the displayed temp password and complete the
        // forced reset - proving the displayed value is the credential that was set.
        await page.GotoAsync($"{App.BaseUrl}/logout");
        var fresh = await context.NewPageAsync();
        await fresh.GotoAsync($"{App.BaseUrl}/login");
        await fresh.FillAsync("#email", "newhire@example.com");
        await fresh.FillAsync("#password", tempPassword);
        await fresh.ClickAsync("button[type=submit]");
        await fresh.WaitForURLAsync($"{App.BaseUrl}/account/complete-reset");
        await fresh.FillAsync("#new_password", "new hire chosen password");
        await fresh.ClickAsync("button[type=submit]");
        await fresh.WaitForURLAsync($"{App.BaseUrl}/account");
    }

    [RequiresEnvVarFact(EnvVar = E2EGate.EnvVar)]
    public async Task AdminInvitesUser_NewUserSetsPasswordViaInviteLink_AndLogsIn()
    {
        Gate();
        // Like the rest of the WebE2E suite, this uses the in-memory recording email sender (the E2E
        // fixture wires email through it, not a real SMTP server). The invite link is read from that
        // sender, so the test is gated only on the browser/env, with no SMTP dependency.
        SeedAdminWithPassword("rootadmin2", "admin password value");

        await using var context = await NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{App.BaseUrl}/login");
        await page.FillAsync("#email", "rootadmin2@example.com");
        await page.FillAsync("#password", "admin password value");
        await page.ClickAsync("button[type=submit]");
        await page.WaitForURLAsync($"{App.BaseUrl}/account");

        await page.GotoAsync($"{App.BaseUrl}/admin/users");
        await page.FillAsync("#email", "invitee@example.com");
        await page.FillAsync("#name", "Invitee");
        await page.CheckAsync("input[name=handoff][value=invite]");
        await page.ClickAsync("button:has-text('Create user')");

        // The invite-sent confirmation renders in place: the Create handler returns Page() for the
        // Invited result, so the browser stays on the Users page and shows the panel. There is no
        // redirect to the display page and no separate navigation to wait for.
        var confirmation = page.Locator("[data-invite-confirmation]");
        await confirmation.WaitForAsync();
        Assert.Contains(
            "Invite sent to invitee@example.com", await confirmation.InnerTextAsync(), StringComparison.Ordinal);

        var message = Assert.Single(App.Email.PasswordResets);
        var token = RecordingEmailSender.TokenOf(message);

        // Open the invite link, set a password, and log in as the new user.
        var fresh = await context.NewPageAsync();
        await fresh.GotoAsync($"{App.BaseUrl}/reset-password?token={Uri.EscapeDataString(token)}");
        await fresh.WaitForURLAsync($"{App.BaseUrl}/reset-password");
        await fresh.FillAsync("#new_password", "invitee chosen password");
        await fresh.ClickAsync("button[type=submit]");
        await fresh.WaitForSelectorAsync(".success");

        await fresh.GotoAsync($"{App.BaseUrl}/login");
        await fresh.FillAsync("#email", "invitee@example.com");
        await fresh.FillAsync("#password", "invitee chosen password");
        await fresh.ClickAsync("button[type=submit]");
        await fresh.WaitForURLAsync($"{App.BaseUrl}/account");
    }
}
