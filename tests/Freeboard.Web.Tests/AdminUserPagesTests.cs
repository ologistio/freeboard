using System.Net;
using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// The admin user-management pages: authentication funnel, in-page admin-role gate, antiforgery,
/// the one-time temp-password handoff, the email-invite handoff and its gate, the read-only
/// exemption, and that the plaintext never leaks into logs, the redirect Location, or a cookie.
///
/// Untagged: the Unit tier is selected by exclusion (Category!=Integration&Category!=E2E&Category!=NFR),
/// so fast tests carry no Category trait, matching the project convention.
/// </summary>
public sealed class AdminUserPagesTests
{
    private const string UsersPath = "/admin/users";
    private const string CredentialPath = "/admin/usercredential";

    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static void Cookie(HttpRequestMessage request, string token)
        => request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");

    private static IEnumerable<KeyValuePair<string, string>> SessionCookieFor(string token)
        => [new KeyValuePair<string, string>(SessionCookie.Name, token)];

    // ---- authentication and admin-role gate ----

    [Fact]
    public async Task UnauthenticatedGetRedirectsToLogin()
    {
        using var factory = new AuthWebFactory();
        using var client = NoRedirectClient(factory);

        var response = await client.GetAsync(UsersPath);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task LimitedSessionGetIsFunnelledToCompleteReset()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("lim", role: "admin", forcePasswordReset: true);
        var token = factory.SeedSession(user, SessionAuthState.ForceResetLimited);
        using var client = NoRedirectClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, UsersPath);
        Cookie(request, token);

        var response = await client.SendAsync(request);

        // The limited-session guard funnels the browser to /account/complete-reset before any admin
        // handler (even the admin-role check) runs.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/account/complete-reset", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task NonAdminGetIsForbidden()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("member1", role: "member");
        var token = factory.SeedSession(user);
        using var client = NoRedirectClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, UsersPath);
        Cookie(request, token);

        var response = await client.SendAsync(request);

        // A bare 403 (no scheme), NOT a redirect to /account/sudo.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task NonAdminPostWithValidTokenIsForbiddenAndDoesNotMutate()
    {
        using var factory = new AuthWebFactory();
        var admin = AuthWebFactory.MakeUser("member2", role: "member");
        var token = factory.SeedSession(admin);
        using var client = NoRedirectClient(factory);

        // The admin page GET 403s for a non-admin, so the antiforgery token is scraped from a page the
        // member CAN reach (/account). The token/cookie pair is not page-scoped, so it validates on the
        // admin POST - a 403 then proves the admin-role check denies it, not antiforgery.
        var before = (await factory.Users.ListAsync()).Count;
        var response = await AuthFormTestHelpers.PostFormAsync(
            client, $"{UsersPath}?handler=Create",
            [new("email", "x@example.com"), new("name", "X"), new("global_role", "member"), new("handoff", "temp")],
            extraCookies: SessionCookieFor(token).ToList(), getPath: "/account");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, (await factory.Users.ListAsync()).Count);
    }

    [Fact]
    public async Task AdminGetRendersSeededUsers()
    {
        using var factory = new AuthWebFactory();
        var admin = AuthWebFactory.MakeUser("admin1", role: "admin");
        var token = factory.SeedSession(admin);
        factory.Users.Add(AuthWebFactory.MakeUser("seeded", role: "member"));
        using var client = NoRedirectClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, UsersPath);
        Cookie(request, token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("seeded@example.com", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ---- create: temp-password handoff + one-time display ----

    [Fact]
    public async Task AdminCreateShowsTempPasswordOnceAndStoresOnlyHash()
    {
        using var factory = new AuthWebFactory();
        var admin = AuthWebFactory.MakeUser("admin2", role: "admin");
        var token = factory.SeedSession(admin);
        using var client = NoRedirectClient(factory);

        var create = await AuthFormTestHelpers.PostFormAsync(
            client, $"{UsersPath}?handler=Create",
            [new("email", "created@example.com"), new("name", "Created"), new("global_role", "member"), new("handoff", "temp")],
            extraCookies: SessionCookieFor(token).ToList(), getPath: UsersPath);

        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        Assert.Equal(CredentialPath, create.Headers.Location!.OriginalString);

        var created = await factory.Users.GetByEmailAsync("created@example.com");
        Assert.NotNull(created);
        Assert.True(created!.ForcePasswordReset);

        // The displayed plaintext is NOT in the redirect Location or any Set-Cookie value.
        Assert.DoesNotContain("created@example.com", create.Headers.Location.OriginalString, StringComparison.Ordinal);
        var nonceCookie = AuthFormTestHelpers.ParseSetCookies(create)
            .Single(c => c.Key == SessionCookie.AdminTempPasswordName);

        // Follow the display page once with the nonce cookie; the temp password renders.
        var display = await GetCredentialPageAsync(client, token, nonceCookie.Value);
        Assert.Equal(HttpStatusCode.OK, display.StatusCode);
        var html = await display.Content.ReadAsStringAsync();
        var shown = ExtractTempPassword(html);
        Assert.False(string.IsNullOrEmpty(shown));

        // The displayed value is the value that was hashed and stored.
        var credential = await factory.Credentials.GetAsync(created.Id);
        Assert.NotNull(credential);
        Assert.True(factory.Hasher.Verify(shown, credential!.PasswordHash));

        // A refresh shows nothing (one-time).
        var refresh = await GetCredentialPageAsync(client, token, nonceCookie.Value);
        Assert.DoesNotContain("temp-password", await refresh.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonAdminCannotConsumeTheNonce()
    {
        using var factory = new AuthWebFactory();
        var store = factory.Services.GetRequiredService<TempPasswordDisplayStore>();
        var nonce = store.Stash("ABCDE-FGHJK-MNPQR-STVWX");

        var adminToken = factory.SeedSession(AuthWebFactory.MakeUser("admin3", role: "admin"));
        var memberToken = factory.SeedSession(AuthWebFactory.MakeUser("member3", role: "member"));
        using var client = NoRedirectClient(factory);

        // Non-admin holding the nonce cookie is 403'd BEFORE Take, so the value is not consumed.
        var asMember = await GetCredentialPageAsync(client, memberToken, nonce);
        Assert.Equal(HttpStatusCode.Forbidden, asMember.StatusCode);

        // The admin can still see it.
        var asAdmin = await GetCredentialPageAsync(client, adminToken, nonce);
        Assert.Equal(HttpStatusCode.OK, asAdmin.StatusCode);
        Assert.Contains("ABCDE-FGHJK-MNPQR-STVWX", await asAdmin.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ---- create: email-invite handoff ----

    [Fact]
    public async Task AdminCreateWithInviteSendsLinkAndShowsNoTempPassword()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        var admin = AuthWebFactory.MakeUser("admin4", role: "admin");
        var token = factory.SeedSession(admin);
        using var client = NoRedirectClient(factory);

        var create = await AuthFormTestHelpers.PostFormAsync(
            client, $"{UsersPath}?handler=Create",
            [new("email", "invitee@example.com"), new("name", "Invitee"), new("global_role", "member"), new("handoff", "invite")],
            extraCookies: SessionCookieFor(token).ToList(), getPath: UsersPath);

        // No redirect to the display page: there is nothing to display.
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        Assert.Contains("Invite sent to invitee@example.com", await create.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var invitee = await factory.Users.GetByEmailAsync("invitee@example.com");
        Assert.NotNull(invitee);
        Assert.True(invitee!.ForcePasswordReset);
        // No password credential row was set on the invite path.
        Assert.Null(await factory.Credentials.GetAsync(invitee.Id));

        var message = Assert.Single(factory.Email.PasswordResets);
        var inviteToken = RecordingEmailSender.TokenOf(message);

        // The token is a real, single-use, force-reset-clearing reset token.
        var consume = await AuthFlows.ResetPasswordAsync(
            inviteToken, "their-own-password", factory.Resets, factory.Credentials, factory.Hasher, factory.Services, default);
        Assert.IsType<AuthFlows.PasswordResult.Ok>(consume);
        Assert.False((await factory.Users.GetByIdAsync(invitee.Id))!.ForcePasswordReset);

        var second = await AuthFlows.ResetPasswordAsync(
            inviteToken, "again", factory.Resets, factory.Credentials, factory.Hasher, factory.Services, default);
        Assert.IsType<AuthFlows.PasswordResult.Invalid>(second);
    }

    [Fact]
    public async Task InviteOptionDisabledWhenEmailUnconfiguredAndForgedPostFallsBackToTempPassword()
    {
        using var factory = new AuthWebFactory(); // no email transport
        var admin = AuthWebFactory.MakeUser("admin5", role: "admin");
        var token = factory.SeedSession(admin);
        using var client = NoRedirectClient(factory);

        using var get = new HttpRequestMessage(HttpMethod.Get, UsersPath);
        Cookie(get, token);
        var page = await client.SendAsync(get);
        Assert.Contains("disabled", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A forged invite-path POST falls back to the temp-password path: a temp password is shown
        // (redirect to the display page) and a credential hash is stored; nothing is sent.
        var create = await AuthFormTestHelpers.PostFormAsync(
            client, $"{UsersPath}?handler=Create",
            [new("email", "forced@example.com"), new("name", "Forced"), new("global_role", "member"), new("handoff", "invite")],
            extraCookies: SessionCookieFor(token).ToList(), getPath: UsersPath);

        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        Assert.Equal(CredentialPath, create.Headers.Location!.OriginalString);
        var forced = await factory.Users.GetByEmailAsync("forced@example.com");
        Assert.NotNull(await factory.Credentials.GetAsync(forced!.Id));
        Assert.Empty(factory.Email.Sent);
    }

    [Fact]
    public async Task InviteIgnoresPasswordResetEnabledToggle()
    {
        // Email configured but the PUBLIC reset toggle off: an admin create-with-invite still mints a
        // consumable token. PasswordResetEnabled=false while email is registered drives the real wired
        // app with the toggle off, proving the invite branch does not read the public self-serve toggle.
        using var factory = new AuthWebFactory { RegisterEmailSender = true, PasswordResetEnabled = false };
        var admin = AuthWebFactory.MakeUser("admin14", role: "admin");
        var token = factory.SeedSession(admin);
        using var client = NoRedirectClient(factory);

        var create = await AuthFormTestHelpers.PostFormAsync(
            client, $"{UsersPath}?handler=Create",
            [new("email", "toggle@example.com"), new("name", "Toggle"), new("global_role", "member"), new("handoff", "invite")],
            extraCookies: SessionCookieFor(token).ToList(), getPath: UsersPath);

        // Invited: the page re-renders with the in-page confirmation, not a redirect to the display page.
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        Assert.Contains("Invite sent to toggle@example.com", await create.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var invitee = await factory.Users.GetByEmailAsync("toggle@example.com");
        Assert.NotNull(invitee);
        Assert.True(invitee!.ForcePasswordReset);

        var inviteToken = RecordingEmailSender.TokenOf(Assert.Single(factory.Email.PasswordResets));

        // The /reset-password?token= link is consumable: the new user sets a password, clearing force-reset.
        var consume = await AuthFlows.ResetPasswordAsync(
            inviteToken, "their-own-password", factory.Resets, factory.Credentials, factory.Hasher, factory.Services, default);
        Assert.IsType<AuthFlows.PasswordResult.Ok>(consume);
        Assert.False((await factory.Users.GetByIdAsync(invitee.Id))!.ForcePasswordReset);
    }

    [Fact]
    public async Task InviteSendFailureReturnsInviteSendFailedWithRowPresent()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true, EmailSenderThrows = true };

        var result = await AuthFlows.CreateUserAsync(
            "sendfail@example.com", "SendFail", "member", AuthFlows.CreateUserHandoff.EmailInvite,
            factory.Users, factory.Credentials, factory.Hasher, factory.Resets, factory.Services, default);

        var failed = Assert.IsType<AuthFlows.CreateUserResult.InviteSendFailed>(result);
        Assert.True(failed.User.ForcePasswordReset);
        var row = await factory.Users.GetByEmailAsync("sendfail@example.com");
        Assert.NotNull(row);
        Assert.Null(await factory.Credentials.GetAsync(row!.Id)); // no usable credential.
    }

    [Fact]
    public async Task InviteTokenMintFailureReturnsInviteSendFailedWithRowPresent()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };

        var result = await AuthFlows.CreateUserAsync(
            "mintfail@example.com", "MintFail", "member", AuthFlows.CreateUserHandoff.EmailInvite,
            factory.Users, factory.Credentials, factory.Hasher, new ThrowingResetStore(), factory.Services, default);

        var failed = Assert.IsType<AuthFlows.CreateUserResult.InviteSendFailed>(result);
        Assert.True(failed.User.ForcePasswordReset);
        Assert.Empty(factory.Email.Sent); // never reached the send.
        var row = await factory.Users.GetByEmailAsync("mintfail@example.com");
        Assert.NotNull(row);
        Assert.Null(await factory.Credentials.GetAsync(row!.Id));
    }

    // ---- reset / disable / enable / duplicate / stale ----

    [Fact]
    public async Task AdminResetPasswordShowsTempPasswordOnceSetsForceResetAndRevokes()
    {
        using var factory = new AuthWebFactory();
        var admin = AuthWebFactory.MakeUser("admin6", role: "admin");
        var token = factory.SeedSession(admin);
        var target = factory.Users.Add(AuthWebFactory.MakeUser("target1", role: "member"));
        await factory.Credentials.SetAsync(target.Id, factory.Hasher.Hash("old"), 1);
        await factory.Sessions.CreateAsync(target.Id, [9, 9, 9], 1, SessionAuthState.Full, DateTime.UtcNow.AddHours(1));
        using var client = NoRedirectClient(factory);

        var reset = await AuthFormTestHelpers.PostFormAsync(
            client, $"{UsersPath}?handler=ResetPassword",
            [new("id", target.Id)],
            extraCookies: SessionCookieFor(token).ToList(), getPath: UsersPath);

        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.Equal(CredentialPath, reset.Headers.Location!.OriginalString);
        Assert.True((await factory.Users.GetByIdAsync(target.Id))!.ForcePasswordReset);
        Assert.Empty(await factory.Sessions.ListByUserAsync(target.Id));

        var nonceCookie = AuthFormTestHelpers.ParseSetCookies(reset)
            .Single(c => c.Key == SessionCookie.AdminTempPasswordName);
        var display = await GetCredentialPageAsync(client, token, nonceCookie.Value);
        var shown = ExtractTempPassword(await display.Content.ReadAsStringAsync());
        var credential = await factory.Credentials.GetAsync(target.Id);
        Assert.True(factory.Hasher.Verify(shown, credential!.PasswordHash));
    }

    [Fact]
    public async Task AdminDisableRevokesSessionsAndEnableClearsDisabled()
    {
        using var factory = new AuthWebFactory();
        var admin = AuthWebFactory.MakeUser("admin7", role: "admin");
        var token = factory.SeedSession(admin);
        var target = factory.Users.Add(AuthWebFactory.MakeUser("target2", role: "member"));
        await factory.Sessions.CreateAsync(target.Id, [8, 8, 8], 1, SessionAuthState.Full, DateTime.UtcNow.AddHours(1));
        using var client = NoRedirectClient(factory);

        var disable = await AuthFormTestHelpers.PostFormAsync(
            client, $"{UsersPath}?handler=Disable", [new("id", target.Id)],
            extraCookies: SessionCookieFor(token).ToList(), getPath: UsersPath);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.False((await factory.Users.GetByIdAsync(target.Id))!.Enabled);
        Assert.Empty(await factory.Sessions.ListByUserAsync(target.Id));

        var enable = await AuthFormTestHelpers.PostFormAsync(
            client, $"{UsersPath}?handler=Enable", [new("id", target.Id)],
            extraCookies: SessionCookieFor(token).ToList(), getPath: UsersPath);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.True((await factory.Users.GetByIdAsync(target.Id))!.Enabled);
    }

    [Fact]
    public async Task AdminCannotResetOwnPasswordAndKeepsSession()
    {
        using var factory = new AuthWebFactory();
        var admin = AuthWebFactory.MakeUser("admin-self-reset", role: "admin");
        var token = factory.SeedSession(admin);
        using var client = NoRedirectClient(factory);

        var reset = await AuthFormTestHelpers.PostFormAsync(
            client, $"{UsersPath}?handler=ResetPassword", [new("id", admin.Id)],
            extraCookies: SessionCookieFor(token).ToList(), getPath: UsersPath);

        // Self-reset would revoke the acting admin's sessions and redirect to a credential page the
        // now-logged-out admin could not load. The handler re-renders the list with a notice instead.
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Contains("your own password", await reset.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.NotEmpty(await factory.Sessions.ListByUserAsync(admin.Id));
        Assert.False((await factory.Users.GetByIdAsync(admin.Id))!.ForcePasswordReset);
    }

    [Fact]
    public async Task AdminCannotDisableOwnAccount()
    {
        using var factory = new AuthWebFactory();
        var admin = AuthWebFactory.MakeUser("admin-self-disable", role: "admin");
        var token = factory.SeedSession(admin);
        using var client = NoRedirectClient(factory);

        var disable = await AuthFormTestHelpers.PostFormAsync(
            client, $"{UsersPath}?handler=Disable", [new("id", admin.Id)],
            extraCookies: SessionCookieFor(token).ToList(), getPath: UsersPath);

        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.Contains("your own account", await disable.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True((await factory.Users.GetByIdAsync(admin.Id))!.Enabled);
        Assert.NotEmpty(await factory.Sessions.ListByUserAsync(admin.Id));
    }

    [Fact]
    public async Task DuplicateEmailReRendersCreateErrorAndAddsNoUser()
    {
        using var factory = new AuthWebFactory();
        var admin = AuthWebFactory.MakeUser("admin8", role: "admin");
        var token = factory.SeedSession(admin);
        factory.Users.Add(AuthWebFactory.MakeUser("dupe", role: "member")); // dupe@example.com
        using var client = NoRedirectClient(factory);

        var before = (await factory.Users.ListAsync()).Count;
        var create = await AuthFormTestHelpers.PostFormAsync(
            client, $"{UsersPath}?handler=Create",
            [new("email", "dupe@example.com"), new("name", "Again"), new("global_role", "member"), new("handoff", "temp")],
            extraCookies: SessionCookieFor(token).ToList(), getPath: UsersPath);

        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        Assert.Contains("already exists", await create.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(before, (await factory.Users.ListAsync()).Count);
    }

    [Fact]
    public async Task StaleIdReRendersListWithNotice()
    {
        using var factory = new AuthWebFactory();
        var admin = AuthWebFactory.MakeUser("admin9", role: "admin");
        var token = factory.SeedSession(admin);
        using var client = NoRedirectClient(factory);

        foreach (var handler in new[] { "ResetPassword", "Disable", "Enable" })
        {
            var response = await AuthFormTestHelpers.PostFormAsync(
                client, $"{UsersPath}?handler={handler}", [new("id", "ghost-id")],
                extraCookies: SessionCookieFor(token).ToList(), getPath: UsersPath);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no longer exists", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    // ---- antiforgery, read-only, no-leak ----

    [Fact]
    public async Task PostWithoutAntiforgeryTokenIsRejected()
    {
        using var factory = new AuthWebFactory();
        var admin = AuthWebFactory.MakeUser("admin10", role: "admin");
        var token = factory.SeedSession(admin);
        using var client = NoRedirectClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{UsersPath}?handler=Create")
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("email", "x@example.com")]),
        };
        Cookie(request, token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyModeExemptsAdminPostAndTheEffectLands()
    {
        using var factory = new AuthWebFactory { ReadOnly = true };
        var admin = AuthWebFactory.MakeUser("admin11", role: "admin");
        var token = factory.SeedSession(admin);
        var target = factory.Users.Add(AuthWebFactory.MakeUser("target3", role: "member"));
        using var client = NoRedirectClient(factory);

        // A seeded disable, then enable: not 409'd, and the effect (Enabled flips) lands.
        var disable = await AuthFormTestHelpers.PostFormAsync(
            client, $"{UsersPath}?handler=Disable", [new("id", target.Id)],
            extraCookies: SessionCookieFor(token).ToList(), getPath: UsersPath);
        Assert.NotEqual(HttpStatusCode.Conflict, disable.StatusCode);
        Assert.False((await factory.Users.GetByIdAsync(target.Id))!.Enabled);

        var enable = await AuthFormTestHelpers.PostFormAsync(
            client, $"{UsersPath}?handler=Enable", [new("id", target.Id)],
            extraCookies: SessionCookieFor(token).ToList(), getPath: UsersPath);
        Assert.NotEqual(HttpStatusCode.Conflict, enable.StatusCode);
        Assert.True((await factory.Users.GetByIdAsync(target.Id))!.Enabled);
    }

    [Fact]
    public async Task TempPasswordNeverAppearsInLogsLocationOrCookies()
    {
        using var factory = new AuthWebFactory();
        var admin = AuthWebFactory.MakeUser("admin12", role: "admin");
        var token = factory.SeedSession(admin);
        using var client = NoRedirectClient(factory);

        var create = await AuthFormTestHelpers.PostFormAsync(
            client, $"{UsersPath}?handler=Create",
            [new("email", "leak@example.com"), new("name", "Leak"), new("global_role", "member"), new("handoff", "temp")],
            extraCookies: SessionCookieFor(token).ToList(), getPath: UsersPath);

        var nonceCookie = AuthFormTestHelpers.ParseSetCookies(create)
            .Single(c => c.Key == SessionCookie.AdminTempPasswordName);
        var display = await GetCredentialPageAsync(client, token, nonceCookie.Value);
        var shown = ExtractTempPassword(await display.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrEmpty(shown));

        // The plaintext is not in the redirect Location, not in any Set-Cookie value, not in the logs.
        Assert.DoesNotContain(shown, create.Headers.Location!.OriginalString, StringComparison.Ordinal);
        foreach (var setCookie in AuthFormTestHelpers.ParseSetCookies(create))
        {
            Assert.DoesNotContain(shown, setCookie.Value, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(factory.Logs.Entries, e => e.Text.Contains(shown, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InviteResponseAndLogsCarryNoToken()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        var admin = AuthWebFactory.MakeUser("admin13", role: "admin");
        var token = factory.SeedSession(admin);
        using var client = NoRedirectClient(factory);

        var create = await AuthFormTestHelpers.PostFormAsync(
            client, $"{UsersPath}?handler=Create",
            [new("email", "inv2@example.com"), new("name", "Inv2"), new("global_role", "member"), new("handoff", "invite")],
            extraCookies: SessionCookieFor(token).ToList(), getPath: UsersPath);

        var inviteToken = RecordingEmailSender.TokenOf(Assert.Single(factory.Email.PasswordResets));
        var body = await create.Content.ReadAsStringAsync();

        Assert.DoesNotContain(inviteToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Logs.Entries, e => e.Text.Contains(inviteToken, StringComparison.Ordinal));
    }

    private static async Task<HttpResponseMessage> GetCredentialPageAsync(HttpClient client, string sessionToken, string nonce)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CredentialPath);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={sessionToken}; {SessionCookie.AdminTempPasswordName}={nonce}");
        return await client.SendAsync(request);
    }

    // The display page wraps the value in <p class="temp-password">...</p>.
    private static string ExtractTempPassword(string html)
    {
        const string open = "class=\"temp-password\">";
        var start = html.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += open.Length;
        var end = html.IndexOf('<', start);
        return html[start..end].Trim();
    }
}

/// <summary>An <see cref="IPasswordResetStore"/> whose CreateAsync throws, to model a token-mint outage.</summary>
internal sealed class ThrowingResetStore : IPasswordResetStore
{
    public Task<MintedPasswordReset> CreateAsync(string userId, DateTime expiresAt, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("simulated token-store failure");

    public Task<string?> ConsumeAsync(string token, DateTime now, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}
