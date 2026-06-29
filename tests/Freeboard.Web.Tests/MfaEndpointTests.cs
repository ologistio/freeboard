using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Freeboard.Persistence.Auth;

namespace Freeboard.Web.Tests;

/// <summary>
/// MFA two-step login + enrollment + sudo tests. Store doubles; no DB.
/// </summary>
public sealed class MfaEndpointTests
{
    private const string Prefix = "/api/v1/freeboard";

    private static void SeedMfaUser(AuthWebFactory f, UserRow user, string password = "password")
    {
        f.Users.Add(user);
        f.Credentials.SetAsync(user.Id, f.Hasher.Hash(password), 1).GetAwaiter().GetResult();
    }

    private static async Task<string> LoginForMfaTokenAsync(HttpClient client, string email)
    {
        var login = await client.PostAsJsonAsync($"{Prefix}/auth/login", new { email, password = "password" });
        Assert.Equal(HttpStatusCode.Accepted, login.StatusCode);
        var json = await login.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("mfa_token").GetString()!;
    }

    [Fact]
    public async Task TotpTwoStepYieldsFullSession()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("totp1", mfaEnabled: true);
        SeedMfaUser(factory, user);
        factory.Totp.SetConfirmed(user.Id);
        using var client = factory.CreateClient();

        var mfaToken = await LoginForMfaTokenAsync(client, user.Email);

        var verify = await client.PostAsJsonAsync(
            $"{Prefix}/auth/mfa/totp", new { mfa_token = mfaToken, code = "123456" });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var json = await verify.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(user.Id, json.GetProperty("user").GetProperty("id").GetString());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("token").GetString()));
    }

    [Fact]
    public async Task RecoveryTwoStepYieldsFullSession()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("rec1", mfaEnabled: true);
        SeedMfaUser(factory, user);
        factory.Recovery.Seed(user.Id, "code-aaa", "code-bbb");
        using var client = factory.CreateClient();

        var mfaToken = await LoginForMfaTokenAsync(client, user.Email);

        var verify = await client.PostAsJsonAsync(
            $"{Prefix}/auth/mfa/recovery", new { mfa_token = mfaToken, recovery_code = "code-aaa" });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.False(string.IsNullOrEmpty((await verify.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()));
    }

    [Fact]
    public async Task WrongTotpCodeIs401AndFiveAttemptsInvalidatesChallenge()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("totp2", mfaEnabled: true);
        SeedMfaUser(factory, user);
        factory.Totp.SetConfirmed(user.Id);
        using var client = factory.CreateClient();

        var mfaToken = await LoginForMfaTokenAsync(client, user.Email);

        // 5 wrong attempts consume the challenge.
        for (var i = 0; i < 5; i++)
        {
            var bad = await client.PostAsJsonAsync($"{Prefix}/auth/mfa/totp", new { mfa_token = mfaToken, code = "000000" });
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }

        // The challenge is now consumed: even a correct code fails.
        var correct = await client.PostAsJsonAsync($"{Prefix}/auth/mfa/totp", new { mfa_token = mfaToken, code = "123456" });
        Assert.Equal(HttpStatusCode.Unauthorized, correct.StatusCode);
    }

    [Fact]
    public async Task MagicLinkFallbackTwoStepYieldsFullSession()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        var user = AuthWebFactory.MakeUser("ml1", mfaEnabled: true); // no passkey, no totp -> fallback
        SeedMfaUser(factory, user);
        using var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync($"{Prefix}/auth/login", new { email = user.Email, password = "password" });
        var loginJson = await login.Content.ReadFromJsonAsync<JsonElement>();
        var mfaToken = loginJson.GetProperty("mfa_token").GetString()!;
        var factors = loginJson.GetProperty("factors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("magic_link", factors);

        var send = await client.PostAsJsonAsync($"{Prefix}/auth/mfa/magic-link/send", new { mfa_token = mfaToken });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        var linkToken = RecordingEmailSender.TokenOf(factory.Email.MagicLinks.Single());

        var verify = await client.PostAsJsonAsync(
            $"{Prefix}/auth/mfa/magic-link/verify", new { mfa_token = mfaToken, link_token = linkToken });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        Assert.False(string.IsNullOrEmpty((await verify.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()));
    }

    [Fact]
    public async Task MagicLinkNotOfferedWithoutSender()
    {
        using var factory = new AuthWebFactory(); // no email sender
        var user = AuthWebFactory.MakeUser("ml2", mfaEnabled: true);
        SeedMfaUser(factory, user);
        using var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync($"{Prefix}/auth/login", new { email = user.Email, password = "password" });
        var factors = (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("factors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("magic_link", factors);
    }

    [Fact]
    public async Task MagicLinkSendIsCapped()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        var user = AuthWebFactory.MakeUser("ml3", mfaEnabled: true);
        SeedMfaUser(factory, user);
        using var client = factory.CreateClient();

        var mfaToken = await LoginForMfaTokenAsync(client, user.Email);

        // Default cap is 3 sends; the 4th is rejected.
        for (var i = 0; i < 3; i++)
        {
            var ok = await client.PostAsJsonAsync($"{Prefix}/auth/mfa/magic-link/send", new { mfa_token = mfaToken });
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var capped = await client.PostAsJsonAsync($"{Prefix}/auth/mfa/magic-link/send", new { mfa_token = mfaToken });
        Assert.Equal(HttpStatusCode.TooManyRequests, capped.StatusCode);
    }

    [Fact]
    public async Task ChallengeTokenRejectedAsBearer()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("ct1", mfaEnabled: true);
        SeedMfaUser(factory, user);
        factory.Totp.SetConfirmed(user.Id);
        using var client = factory.CreateClient();

        var mfaToken = await LoginForMfaTokenAsync(client, user.Email);

        // Present the body-only challenge token as a bearer: it is not a session, so 401.
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mfaToken);
        var me = await client.GetAsync($"{Prefix}/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    // ---- enrollment + sudo ----

    [Fact]
    public async Task TotpEnrollRequiresSudoThenWorksAfterSudo()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("en1"); // non-MFA user; sudo via password.
        using var client = factory.CreateAuthenticatedClient(user, password: "pw");

        // Without a recent sudo_at, an MFA-state change is 403.
        var blocked = await client.PostAsJsonAsync($"{Prefix}/auth/mfa/totp/enroll", new { });
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

        // Step up via password (the user has no MFA factors yet).
        var sudo = await client.PostAsJsonAsync($"{Prefix}/auth/sudo", new { factor = "password", password = "pw" });
        Assert.Equal(HttpStatusCode.OK, sudo.StatusCode);

        // Now enrollment is allowed.
        var enroll = await client.PostAsJsonAsync($"{Prefix}/auth/mfa/totp/enroll", new { });
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);
        Assert.False(string.IsNullOrEmpty((await enroll.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("provisioning_uri").GetString()));

        // Activate returns recovery codes once (first factor) and marks mfa_enabled.
        var activate = await client.PostAsJsonAsync($"{Prefix}/auth/mfa/totp/activate", new { code = "123456" });
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        var json = await activate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("recovery_codes", out var codes));
        Assert.Equal(10, codes.GetArrayLength());
        Assert.True((await factory.Users.GetByIdAsync(user.Id))!.MfaEnabled);
    }

    [Fact]
    public async Task MagicLinkOnlyUserStepsUpViaMagicLinkToEnrollStrongFactor()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        // An MFA-enabled user with NO strong factor: magic-link is the only step-up.
        var user = AuthWebFactory.MakeUser("mlonly", mfaEnabled: true);
        using var client = factory.CreateAuthenticatedClient(user, password: "pw");

        // Strong-factor enrollment is blocked without sudo.
        var blocked = await client.PostAsJsonAsync($"{Prefix}/auth/mfa/totp/enroll", new { });
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

        // Step up via magic-link: send mints a sudo challenge + emails a token.
        var send = await client.PostAsJsonAsync($"{Prefix}/auth/sudo/magic-link/send", new { });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        var challengeId = (await send.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challenge_id").GetString();
        var linkToken = RecordingEmailSender.TokenOf(factory.Email.MagicLinks.Single());

        var sudo = await client.PostAsJsonAsync(
            $"{Prefix}/auth/sudo", new { factor = "magic_link", challenge_id = challengeId, link_token = linkToken });
        Assert.Equal(HttpStatusCode.OK, sudo.StatusCode);

        // Now the magic-link-only user can enroll a strong factor.
        var enroll = await client.PostAsJsonAsync($"{Prefix}/auth/mfa/totp/enroll", new { });
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);
    }
}
