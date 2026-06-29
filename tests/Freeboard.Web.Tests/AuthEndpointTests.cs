using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Freeboard.Persistence.Auth;

namespace Freeboard.Web.Tests;

/// <summary>
/// Auth-endpoint tests with store doubles (no MySQL): login 200/401/202/429 and the
/// uniform-verify proof; me/logout; password change/forgot/reset; account/password upgrade;
/// session IDOR.
/// </summary>
public sealed class AuthEndpointTests
{
    private const string Prefix = "/api/v1/freeboard";

    private static void SeedUser(AuthWebFactory f, UserRow user, string password = "password")
    {
        f.Users.Add(user);
        f.Credentials.SetAsync(user.Id, f.Hasher.Hash(password), 1).GetAwaiter().GetResult();
    }

    // ---- login ----

    [Fact]
    public async Task LoginSucceedsReturnsUserAndToken()
    {
        using var factory = new AuthWebFactory();
        SeedUser(factory, AuthWebFactory.MakeUser("u1"));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"{Prefix}/auth/login", new { email = "u1@example.com", password = "password" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("u1", json.GetProperty("user").GetProperty("id").GetString());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("token").GetString()));
        Assert.True(factory.Hasher.VerifyCalls >= 1);
    }

    [Fact]
    public async Task LoginWrongPasswordIs401()
    {
        using var factory = new AuthWebFactory();
        SeedUser(factory, AuthWebFactory.MakeUser("u1"));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"{Prefix}/auth/login", new { email = "u1@example.com", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LoginUnknownUserInvokesDecoyVerifier()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"{Prefix}/auth/login", new { email = "ghost@example.com", password = "x" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // An unknown account still runs the decoy verifier (constant work + uniform shape).
        Assert.Equal(1, factory.Hasher.VerifyDecoyCalls);
    }

    [Fact]
    public async Task LoginDisabledUserInvokesDecoyVerifier()
    {
        using var factory = new AuthWebFactory();
        SeedUser(factory, AuthWebFactory.MakeUser("u2", enabled: false));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"{Prefix}/auth/login", new { email = "u2@example.com", password = "password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // A disabled account also runs the decoy verifier, not the real verify.
        Assert.Equal(1, factory.Hasher.VerifyDecoyCalls);
    }

    [Fact]
    public async Task LoginWithMfaReturns202WithTokenAndFactors()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("u3", mfaEnabled: true);
        SeedUser(factory, user);
        factory.Totp.SetConfirmed(user.Id); // a strong factor so the factor list is non-empty.
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"{Prefix}/auth/login", new { email = "u3@example.com", password = "password" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("mfa_required").GetBoolean());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("mfa_token").GetString()));
        var factors = json.GetProperty("factors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("totp", factors);
    }

    [Fact]
    public async Task LoginRateLimitedIs429()
    {
        using var factory = new AuthWebFactory();
        factory.RateLimit.ForceLimited = true;
        SeedUser(factory, AuthWebFactory.MakeUser("u1"));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"{Prefix}/auth/login", new { email = "u1@example.com", password = "password" });
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    // ---- me / logout ----

    [Fact]
    public async Task MeReturnsUserObjectFields()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("me1"));

        var json = await client.GetFromJsonAsync<JsonElement>($"{Prefix}/auth/me");
        Assert.Equal("me1", json.GetProperty("id").GetString());
        Assert.Equal("me1@example.com", json.GetProperty("email").GetString());
        Assert.True(json.TryGetProperty("global_role", out _));
        Assert.True(json.TryGetProperty("mfa_enabled", out _));
    }

    [Fact]
    public async Task LogoutRevokesCurrentSession()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("lo1"));

        var response = await client.PostAsync($"{Prefix}/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The token is now revoked: a follow-up me is 401.
        var me = await client.GetAsync($"{Prefix}/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    // ---- password change ----

    [Fact]
    public async Task PasswordChangeWrongOldIs422()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("pc1"), password: "old");

        var response = await client.PostAsJsonAsync(
            $"{Prefix}/auth/password/change", new { old_password = "nope", new_password = "newpass" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task PasswordChangeSucceedsWithoutSudo()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("pc2");
        using var client = factory.CreateAuthenticatedClient(user, password: "old");

        // No sudo stamp on the session; change still succeeds (old_password is the proof).
        var response = await client.PostAsJsonAsync(
            $"{Prefix}/auth/password/change", new { old_password = "old", new_password = "newpass" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- forgot / reset ----

    [Fact]
    public async Task ForgotPasswordUniform200ForKnownAndUnknown()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        SeedUser(factory, AuthWebFactory.MakeUser("fp1"));
        using var client = factory.CreateClient();

        var known = await client.PostAsJsonAsync($"{Prefix}/auth/password/forgot", new { email = "fp1@example.com" });
        var unknown = await client.PostAsJsonAsync($"{Prefix}/auth/password/forgot", new { email = "ghost@example.com" });

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        // Only the known account triggered an email, sent to that account's address, and the body
        // carries the absolute reset URL.
        var message = factory.Email.PasswordResets.Single();
        Assert.Equal("fp1@example.com", message.To);
        Assert.Contains($"{AuthWebFactory.AuthBaseUrl}/reset-password?token=", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgotPasswordStays200WhenSenderThrows()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true, EmailSenderThrows = true };
        SeedUser(factory, AuthWebFactory.MakeUser("fp2"));
        using var client = factory.CreateClient();

        // A throwing send for a REAL account must not surface as a 500: that would be an
        // enumeration oracle. It stays the same uniform 200 an unknown account gets.
        var known = await client.PostAsJsonAsync($"{Prefix}/auth/password/forgot", new { email = "fp2@example.com" });
        var unknown = await client.PostAsJsonAsync($"{Prefix}/auth/password/forgot", new { email = "ghost@example.com" });

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
    }

    [Fact]
    public async Task ForgotPasswordSendFailureNeverLogsTheToken()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true, EmailSenderThrows = true };
        SeedUser(factory, AuthWebFactory.MakeUser("fp3"));
        using var client = factory.CreateClient();

        // The throwing sender embeds the reset token in its exception Message. The failure log must
        // record only the recipient and a sanitized error identity, never the token - a credential
        // must not appear in any entry at information level or above. FakeResetStore mints the first
        // token as "reset-token-1".
        var known = await client.PostAsJsonAsync($"{Prefix}/auth/password/forgot", new { email = "fp3@example.com" });
        Assert.Equal(HttpStatusCode.OK, known.StatusCode);

        Assert.DoesNotContain(
            factory.Logs.Entries, e => e.Text.Contains("reset-token-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResetPasswordConsumesTokenAndRevokesSessions()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        SeedUser(factory, AuthWebFactory.MakeUser("rp1"));
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync($"{Prefix}/auth/password/forgot", new { email = "rp1@example.com" });
        var token = RecordingEmailSender.TokenOf(factory.Email.PasswordResets.Single());

        var ok = await client.PostAsJsonAsync(
            $"{Prefix}/auth/password/reset", new { token, new_password = "brandnew" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // Single-use: the same token is now rejected.
        var again = await client.PostAsJsonAsync(
            $"{Prefix}/auth/password/reset", new { token, new_password = "another" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
    }

    // ---- account/password (force-reset limited) ----

    [Fact]
    public async Task LimitedSessionUsesAccountPasswordThenSameTokenWorks()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("ar1", forcePasswordReset: true);
        using var client = factory.CreateAuthenticatedClient(user, SessionAuthState.ForceResetLimited);

        // A normal endpoint is blocked while limited.
        var blocked = await client.GetAsync($"{Prefix}/users/{user.Id}/sessions");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

        // Setting a new password upgrades the session in place (same token).
        var set = await client.PostAsJsonAsync($"{Prefix}/account/password", new { new_password = "fresh" });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        // The SAME token now reaches a normal endpoint.
        var allowed = await client.GetAsync($"{Prefix}/users/{user.Id}/sessions");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    // ---- session IDOR ----

    [Fact]
    public async Task SessionIdorReturns404ForNonOwned()
    {
        using var factory = new AuthWebFactory();
        // Seed a victim user + session owned by someone else.
        factory.Users.Add(AuthWebFactory.MakeUser("victim"));
        var victimSession = await factory.Sessions.CreateAsync(
            "victim", [9, 9, 9], 1, SessionAuthState.Full, DateTime.UtcNow.AddHours(1));

        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("attacker"));

        var response = await client.GetAsync($"{Prefix}/auth/sessions/{victimSession.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdminCanReadAnotherUsersSessions()
    {
        using var factory = new AuthWebFactory();
        factory.Users.Add(AuthWebFactory.MakeUser("target"));
        await factory.Sessions.CreateAsync("target", [1, 1, 1], 1, SessionAuthState.Full, DateTime.UtcNow.AddHours(1));

        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("boss", role: "admin"));

        var json = await client.GetFromJsonAsync<JsonElement>($"{Prefix}/users/target/sessions");
        Assert.True(json.GetArrayLength() >= 1);
    }
}
