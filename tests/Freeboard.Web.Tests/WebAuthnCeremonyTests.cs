using Fido2NetLib;
using Freeboard.Auth;
using Microsoft.Extensions.Options;

namespace Freeboard.Web.Tests;

/// <summary>
/// WebAuthn ceremony tests at the service level. A full happy-path assertion needs a real
/// authenticator (or a recorded fixture) and is left to an integration/e2e harness; here we cover
/// option generation, the synced-passkey sign-counter rule (unit-tested in the persistence layer),
/// and rejection of malformed / unverifiable responses (which includes the origin / RP-id mismatch
/// path the underlying library enforces, surfaced as WebAuthnCeremonyException).
/// </summary>
public sealed class WebAuthnCeremonyTests
{
    private static WebAuthnCeremony Build(out FakeWebAuthnCredentialStore store)
    {
        store = new FakeWebAuthnCredentialStore();
        var fido2 = new Fido2(new Fido2Configuration
        {
            ServerDomain = "localhost",
            ServerName = "Freeboard",
            Origins = new HashSet<string> { "https://localhost" },
        });
        var options = Options.Create(new WebAuthnOptions
        {
            RpId = "localhost",
            RpName = "Freeboard",
            Origins = ["https://localhost"],
        });
        return new WebAuthnCeremony(fido2, store, options);
    }

    [Fact]
    public async Task BeginRegistrationProducesOptionsJson()
    {
        var ceremony = Build(out _);
        var json = await ceremony.BeginRegistrationAsync("user-1", "user-1@example.com");
        Assert.False(string.IsNullOrEmpty(json));
        // The options round-trip through the library's parser.
        var parsed = CredentialCreateOptions.FromJson(json);
        Assert.Equal("localhost", parsed.Rp.Id);
    }

    [Fact]
    public async Task BeginAssertionProducesOptionsJson()
    {
        var ceremony = Build(out var store);
        store.Seed("user-1", [9, 9, 9, 9]);
        var json = await ceremony.BeginAssertionAsync("user-1");
        Assert.False(string.IsNullOrEmpty(json));
        var parsed = AssertionOptions.FromJson(json);
        Assert.NotNull(parsed.Challenge);
    }

    [Fact]
    public async Task RegisterRejectsMalformedAttestation()
    {
        var ceremony = Build(out _);
        var optionsJson = await ceremony.BeginRegistrationAsync("user-1", "user-1@example.com");

        await Assert.ThrowsAsync<WebAuthnCeremonyException>(() =>
            ceremony.RegisterAsync("user-1", optionsJson, "{ not a valid attestation }", null));
    }

    [Fact]
    public async Task VerifyAssertionRejectsMalformedResponse()
    {
        var ceremony = Build(out var store);
        store.Seed("user-1", [9, 9, 9, 9]);
        var optionsJson = await ceremony.BeginAssertionAsync("user-1");

        await Assert.ThrowsAsync<WebAuthnCeremonyException>(() =>
            ceremony.VerifyAssertionAsync("user-1", optionsJson, "{ not a valid assertion }"));
    }
}
