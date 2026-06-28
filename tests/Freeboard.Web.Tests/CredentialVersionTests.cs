using System.Net;
using System.Net.Http.Json;
using Freeboard.Persistence.Auth;

namespace Freeboard.Web.Tests;

/// <summary>
/// The credential-version epoch. A session stores the credential epoch it was issued under;
/// the bearer handler rejects any session whose stored epoch is stale, so a password change
/// invalidates all prior-epoch sessions race-free. The force-reset completion path keeps the
/// current session working because it stamps the kept session's epoch to the new value.
/// </summary>
public sealed class CredentialVersionTests
{
    private const string Prefix = "/api/v1/freeboard";

    [Fact]
    public async Task SessionAtStaleEpochIsRejected()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("cv1");
        // CreateAuthenticatedClient seeds a credential at epoch 1 and a session stamped epoch 1.
        using var client = factory.CreateAuthenticatedClient(user);

        // The session works while epochs match.
        var ok = await client.GetAsync($"{Prefix}/auth/me");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // A password change bumps the user's credential epoch to 2 WITHOUT updating this session
        // (models a revoke that raced with a concurrent login leaving a prior-epoch session).
        factory.Credentials.BumpCredentialVersionOnly(user.Id);

        // The same bearer token is now rejected: stale epoch -> 401.
        var rejected = await client.GetAsync($"{Prefix}/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [Fact]
    public async Task PasswordChangeKeepsCurrentSessionWorking()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("cv2");
        using var client = factory.CreateAuthenticatedClient(user, password: "old");

        // Change password via the endpoint: epoch bumps AND the current session's stored epoch is
        // stamped to the new value, so the same token keeps working.
        var change = await client.PostAsJsonAsync(
            $"{Prefix}/auth/password/change", new { old_password = "old", new_password = "newpass" });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);

        var stillWorks = await client.GetAsync($"{Prefix}/auth/me");
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
    }

    [Fact]
    public async Task ForceResetAccountPasswordKeepsCurrentSessionWorking()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("cv3", forcePasswordReset: true);
        using var client = factory.CreateAuthenticatedClient(user, SessionAuthState.ForceResetLimited);

        // Completing the forced reset upgrades the session to full AND stamps its epoch, so the
        // just-upgraded session keeps working (the token is unchanged).
        var set = await client.PostAsJsonAsync($"{Prefix}/account/password", new { new_password = "fresh" });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        // A now-normal endpoint (not in the limited allowlist) works with the SAME token.
        var sessions = await client.GetAsync($"{Prefix}/users/{user.Id}/sessions");
        Assert.Equal(HttpStatusCode.OK, sessions.StatusCode);
    }

    // Verified epoch is threaded through, not re-read at issue.

    private static void SeedLoginUser(AuthWebFactory f, UserRow user, string password = "password")
    {
        f.Users.Add(user);
        f.Credentials.SetAsync(user.Id, f.Hasher.Hash(password), 1).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task NonMfaLogin_EpochBumpBetweenVerifyAndIssue_SessionRejected()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("i22a");
        SeedLoginUser(factory, user);
        // A concurrent password change lands right after the verify read.
        factory.Credentials.BumpOnGet = true;

        var login = await factory.CreateClient().PostAsJsonAsync(
            $"{Prefix}/auth/login", new { email = user.Email, password = "password" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("token").GetString();

        // The session was stamped with the VERIFIED epoch (1), but the credential epoch is now higher;
        // the bearer epoch check rejects it. (With the old re-read-at-issue code it would be accepted.)
        using var authed = factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var me = await authed.GetAsync($"{Prefix}/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    // The opportunistic login rehash is a compare-and-swap and never clobbers a
    // newer password set by a concurrent change/reset in the verify->rehash window.

    [Fact]
    public async Task LoginRehash_RowChangedUnderneath_DoesNotClobberNewPassword()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("i27a");
        SeedLoginUser(factory, user, password: "oldpw");

        // Force the rehash path, and simulate a concurrent password change/reset landing AFTER the
        // verify read but BEFORE the rehash: a new hash at a bumped credential epoch.
        factory.Hasher.RehashNeeded = true;
        factory.Hasher.OnNeedsRehash = () =>
            factory.Credentials.SetAsync(user.Id, factory.Hasher.Hash("newpw"), 9).GetAwaiter().GetResult();
        // SetAsync preserves the epoch; bump it so the CAS sees a changed credential epoch too.
        factory.Hasher.OnNeedsRehash += () => factory.Credentials.BumpCredentialVersionOnly(user.Id);

        var login = await factory.CreateClient().PostAsJsonAsync(
            $"{Prefix}/auth/login", new { email = user.Email, password = "oldpw" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // The rehash must NOT have written the old-password hash back. The stored hash is the NEW one.
        var stored = await factory.Credentials.GetAsync(user.Id);
        Assert.Equal(factory.Hasher.Hash("newpw"), stored!.PasswordHash);
    }

    [Fact]
    public async Task LoginRehash_RowUnchanged_UpgradesHashSameEpoch()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("i27b");
        SeedLoginUser(factory, user, password: "oldpw");
        factory.Hasher.RehashNeeded = true; // no concurrent change; the row is unchanged.

        var before = await factory.Credentials.GetAsync(user.Id);

        var login = await factory.CreateClient().PostAsJsonAsync(
            $"{Prefix}/auth/login", new { email = user.Email, password = "oldpw" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // The hash is upgraded (re-hashed same password) at the SAME credential epoch (no bump).
        var after = await factory.Credentials.GetAsync(user.Id);
        Assert.Equal(factory.Hasher.Hash("oldpw"), after!.PasswordHash);
        Assert.Equal(before!.CredentialVersion, after.CredentialVersion);
    }

    [Fact]
    public async Task NonMfaLogin_NoBump_SessionWorks()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("i22c");
        SeedLoginUser(factory, user);

        var login = await factory.CreateClient().PostAsJsonAsync(
            $"{Prefix}/auth/login", new { email = user.Email, password = "password" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("token").GetString();

        using var authed = factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var me = await authed.GetAsync($"{Prefix}/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task MfaCompletion_EpochChangedAfter202_Rejected()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("i22b", mfaEnabled: true);
        SeedLoginUser(factory, user);
        factory.Totp.SetConfirmed(user.Id);
        using var client = factory.CreateClient();

        // Login returns a 202 with the challenge stamped at the verified epoch (1).
        var login = await client.PostAsJsonAsync($"{Prefix}/auth/login", new { email = user.Email, password = "password" });
        Assert.Equal(HttpStatusCode.Accepted, login.StatusCode);
        var mfaToken = (await login.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("mfa_token").GetString();

        // The password changes after the 202 (epoch -> 2).
        factory.Credentials.BumpCredentialVersionOnly(user.Id);

        // MFA completion re-checks the epoch, finds it changed, and rejects (401) - the challenge is consumed.
        var verify = await client.PostAsJsonAsync($"{Prefix}/auth/mfa/totp", new { mfa_token = mfaToken, code = "123456" });
        Assert.Equal(HttpStatusCode.Unauthorized, verify.StatusCode);

        // A retry with the same (now-consumed) challenge also fails.
        var retry = await client.PostAsJsonAsync($"{Prefix}/auth/mfa/totp", new { mfa_token = mfaToken, code = "123456" });
        Assert.Equal(HttpStatusCode.Unauthorized, retry.StatusCode);
    }

    [Fact]
    public async Task MfaCompletion_NoEpochChange_IssuesWorkingSession()
    {
        using var factory = new AuthWebFactory();
        var user = AuthWebFactory.MakeUser("i22d", mfaEnabled: true);
        SeedLoginUser(factory, user);
        factory.Totp.SetConfirmed(user.Id);
        using var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync($"{Prefix}/auth/login", new { email = user.Email, password = "password" });
        var mfaToken = (await login.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("mfa_token").GetString();

        var verify = await client.PostAsJsonAsync($"{Prefix}/auth/mfa/totp", new { mfa_token = mfaToken, code = "123456" });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var token = (await verify.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("token").GetString();

        using var authed = factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync($"{Prefix}/auth/me")).StatusCode);
    }
}
