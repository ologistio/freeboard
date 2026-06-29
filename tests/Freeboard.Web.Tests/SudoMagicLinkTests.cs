using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Freeboard.Persistence.Auth;

namespace Freeboard.Web.Tests;

/// <summary>
/// Sudo magic-link tests: single-use consume, user-binding, factor-gate, and
/// effective re-send cap. A magic-link-only user can step up via magic-link (kept in
/// MfaEndpointTests); these cover the abuse cases.
/// </summary>
public sealed class SudoMagicLinkTests
{
    private const string Prefix = "/api/v1/freeboard";

    // A user WITH a passkey or TOTP is NOT offered sudo magic-link.
    [Fact]
    public async Task UserWithTotpCannotGetSudoMagicLink()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        var user = AuthWebFactory.MakeUser("smt1", mfaEnabled: true);
        using var client = factory.CreateAuthenticatedClient(user, password: "pw");
        factory.Totp.SetConfirmed(user.Id); // a strong factor exists.

        var send = await client.PostAsJsonAsync($"{Prefix}/auth/sudo/magic-link/send", new { });
        Assert.Equal(HttpStatusCode.BadRequest, send.StatusCode);
        Assert.Empty(factory.Email.MagicLinks);
    }

    [Fact]
    public async Task UserWithPasskeyCannotGetSudoMagicLink()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        var user = AuthWebFactory.MakeUser("smp1", mfaEnabled: true);
        using var client = factory.CreateAuthenticatedClient(user, password: "pw");
        factory.WebAuthn.Seed(user.Id, [1, 2, 3, 4]); // a passkey exists.

        var send = await client.PostAsJsonAsync($"{Prefix}/auth/sudo/magic-link/send", new { });
        Assert.Equal(HttpStatusCode.BadRequest, send.StatusCode);
    }

    // A sudo magic-link token is single-use.
    [Fact]
    public async Task SudoMagicLinkTokenIsSingleUse()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        var user = AuthWebFactory.MakeUser("sms1", mfaEnabled: true); // magic-link-only.
        using var client = factory.CreateAuthenticatedClient(user, password: "pw");

        var send = await client.PostAsJsonAsync($"{Prefix}/auth/sudo/magic-link/send", new { });
        var challengeId = (await send.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challenge_id").GetString();
        var linkToken = RecordingEmailSender.TokenOf(factory.Email.MagicLinks.Single());

        var first = await client.PostAsJsonAsync(
            $"{Prefix}/auth/sudo", new { factor = "magic_link", challenge_id = challengeId, link_token = linkToken });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Replay the same token: it was consumed, so the second sudo fails.
        var second = await client.PostAsJsonAsync(
            $"{Prefix}/auth/sudo", new { factor = "magic_link", challenge_id = challengeId, link_token = linkToken });
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    // Each send emails its OWN token, so a resend does not invalidate the first email's link.
    [Fact]
    public async Task EarlierSudoMagicLinkStillWorksAfterAResend()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        var user = AuthWebFactory.MakeUser("smr1", mfaEnabled: true); // magic-link-only.
        using var client = factory.CreateAuthenticatedClient(user, password: "pw");

        var send1 = await client.PostAsJsonAsync($"{Prefix}/auth/sudo/magic-link/send", new { });
        var challengeId = (await send1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challenge_id").GetString();
        var firstToken = RecordingEmailSender.TokenOf(factory.Email.MagicLinks.Single());

        // A second send (resend) for the same challenge.
        var send2 = await client.PostAsJsonAsync($"{Prefix}/auth/sudo/magic-link/send", new { });
        Assert.Equal(HttpStatusCode.OK, send2.StatusCode);

        // The FIRST email's link still completes the step-up - it was not clobbered by the resend.
        var stepUp = await client.PostAsJsonAsync(
            $"{Prefix}/auth/sudo", new { factor = "magic_link", challenge_id = challengeId, link_token = firstToken });
        Assert.Equal(HttpStatusCode.OK, stepUp.StatusCode);
    }

    // A sudo magic-link challenge for user A cannot stamp sudo on user B.
    [Fact]
    public async Task SudoMagicLinkIsBoundToTheChallengeUser()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        var victim = AuthWebFactory.MakeUser("victimA", mfaEnabled: true);
        var attacker = AuthWebFactory.MakeUser("attackerB", mfaEnabled: true);

        using var victimClient = factory.CreateAuthenticatedClient(victim, password: "pw");
        using var attackerClient = factory.CreateAuthenticatedClient(attacker, password: "pw");

        // The victim requests a sudo magic-link.
        var send = await victimClient.PostAsJsonAsync($"{Prefix}/auth/sudo/magic-link/send", new { });
        var challengeId = (await send.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challenge_id").GetString();
        var linkToken = RecordingEmailSender.TokenOf(factory.Email.MagicLinks.Single());

        // The attacker tries to use the victim's challenge id + token to stamp sudo on THEIR session.
        var attack = await attackerClient.PostAsJsonAsync(
            $"{Prefix}/auth/sudo", new { factor = "magic_link", challenge_id = challengeId, link_token = linkToken });
        Assert.Equal(HttpStatusCode.Unauthorized, attack.StatusCode);
    }

    // The per-challenge re-send cap is effective because the send reuses one challenge.
    [Fact]
    public async Task SudoMagicLinkResendIsCapped()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        var user = AuthWebFactory.MakeUser("smc1", mfaEnabled: true);
        using var client = factory.CreateAuthenticatedClient(user, password: "pw");

        // Default cap is 3 sends across the reused challenge; the 4th is rejected.
        for (var i = 0; i < 3; i++)
        {
            var ok = await client.PostAsJsonAsync($"{Prefix}/auth/sudo/magic-link/send", new { });
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var capped = await client.PostAsJsonAsync($"{Prefix}/auth/sudo/magic-link/send", new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, capped.StatusCode);
    }

    // Concurrent FIRST sends (no pre-existing challenge) must converge on ONE challenge row,
    // so the re-send cap cannot be multiplied by a race. The find-or-create + send increment is one
    // atomic store call; here the in-memory store serializes it exactly like the SQL unique key.
    [Fact]
    public async Task ConcurrentFirstSendsCreateOneChallengeAndKeepTheCap()
    {
        using var factory = new AuthWebFactory { RegisterEmailSender = true };
        var user = AuthWebFactory.MakeUser("smcc1", mfaEnabled: true); // magic-link-only.
        using var client = factory.CreateAuthenticatedClient(user, password: "pw");

        // Fire 8 sends concurrently with no challenge yet. A pre-fix bug would create several rows,
        // each granting the full send budget; the fix means exactly one row exists afterwards.
        var sends = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            client.PostAsJsonAsync($"{Prefix}/auth/sudo/magic-link/send", new { })));

        Assert.Equal(1, factory.Challenges.SudoMagicLinkChallengeCount(user.Id));

        // The default cap is 3, so at most 3 of the concurrent sends are accepted; the rest are
        // rejected with 429. With multiple rows the accepted count would exceed the cap.
        var accepted = sends.Count(r => r.StatusCode == HttpStatusCode.OK);
        var capped = sends.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal(3, accepted);
        Assert.Equal(5, capped);
    }
}
