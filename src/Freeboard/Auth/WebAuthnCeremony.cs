using System.Text;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Freeboard.Persistence.Auth;
using Microsoft.Extensions.Options;

namespace Freeboard.Auth;

/// <summary>
/// Runs the WebAuthn/FIDO2 ceremonies on top of <see cref="IFido2"/>. Registration verifies
/// an attestation and returns the data to persist; assertion verifies against a stored public key
/// and returns the accepted sign counter. User verification is REQUIRED. RP id / allowed origins
/// are the EXPLICIT configured values (see <see cref="WebAuthnOptions"/>); the underlying library
/// rejects a mismatched origin or RP-id hash on BOTH registration and assertion, surfaced here as
/// <see cref="WebAuthnCeremonyException"/>. In-flight options are correlated to the caller via the
/// caller-held challenge/correlation key (login: the mfa challenge row; enrollment: a short-lived
/// store) and passed back in on completion.
/// </summary>
public sealed class WebAuthnCeremony(
    IFido2 fido2,
    IWebAuthnCredentialStore credentials,
    IOptions<WebAuthnOptions> options)
{
    private readonly WebAuthnOptions _options = options.Value;

    /// <summary>Builds registration (attestation) options for a user. Returns the options JSON to hand the client and store.</summary>
    public async Task<string> BeginRegistrationAsync(string userId, string accountName, CancellationToken ct = default)
    {
        var existing = await credentials.ListByUserAsync(userId, ct).ConfigureAwait(false);
        var excludeCredentials = existing
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToList();

        var user = new Fido2User
        {
            Id = Encoding.UTF8.GetBytes(userId),
            Name = accountName,
            DisplayName = accountName,
        };

        var createOptions = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = user,
            ExcludeCredentials = excludeCredentials,
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        return createOptions.ToJson();
    }

    /// <summary>
    /// Verifies a registration response against the stored options and persists the new credential.
    /// Throws <see cref="WebAuthnCeremonyException"/> on a mismatched origin/RP-id or invalid attestation.
    /// </summary>
    public async Task RegisterAsync(
        string userId, string optionsJson, string responseJson, string? nickname, CancellationToken ct = default)
    {
        var createOptions = CredentialCreateOptions.FromJson(optionsJson);
        AuthenticatorAttestationRawResponse response;
        try
        {
            response = System.Text.Json.JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(responseJson)
                ?? throw new WebAuthnCeremonyException("Empty attestation response.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new WebAuthnCeremonyException("Malformed attestation response.", ex);
        }

        RegisteredPublicKeyCredential result;
        try
        {
            result = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = createOptions,
                IsCredentialIdUniqueToUserCallback = async (args, _) =>
                    await credentials.FindByCredentialIdAsync(args.CredentialId, ct).ConfigureAwait(false) is null,
            }, ct).ConfigureAwait(false);
        }
        catch (Fido2VerificationException ex)
        {
            // Covers mismatched origin / RP-id hash / failed attestation.
            throw new WebAuthnCeremonyException("WebAuthn registration verification failed.", ex);
        }

        await credentials.AddAsync(new NewWebAuthnCredential(
            userId,
            result.Id,
            result.PublicKey,
            (long)result.SignCount,
            Encoding.UTF8.GetBytes(userId),
            result.AaGuid.ToString(),
            result.Transports is { Length: > 0 } t ? string.Join(",", t) : null,
            result.Type.ToString(),
            result.IsBackupEligible,
            result.IsBackedUp,
            nickname), ct).ConfigureAwait(false);
    }

    /// <summary>Builds assertion options for a user's registered credentials. Returns the options JSON.</summary>
    public async Task<string> BeginAssertionAsync(string userId, CancellationToken ct = default)
    {
        var existing = await credentials.ListByUserAsync(userId, ct).ConfigureAwait(false);
        var allowed = existing
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToList();

        var assertionOptions = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowed,
            UserVerification = UserVerificationRequirement.Required,
        });

        return assertionOptions.ToJson();
    }

    /// <summary>
    /// Verifies an assertion response against the stored options and the user's stored credentials,
    /// then applies the synced-passkey sign-counter rule via the store. Returns true on success.
    /// Throws <see cref="WebAuthnCeremonyException"/> on a mismatched origin/RP-id or invalid assertion.
    /// </summary>
    public async Task<bool> VerifyAssertionAsync(
        string userId, string optionsJson, string responseJson, CancellationToken ct = default)
    {
        var assertionOptions = AssertionOptions.FromJson(optionsJson);
        AuthenticatorAssertionRawResponse response;
        try
        {
            response = System.Text.Json.JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(responseJson)
                ?? throw new WebAuthnCeremonyException("Empty assertion response.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new WebAuthnCeremonyException("Malformed assertion response.", ex);
        }

        var stored = await credentials.FindByCredentialIdAsync(response.RawId, ct).ConfigureAwait(false);
        if (stored is null || stored.UserId != userId)
        {
            return false;
        }

        VerifyAssertionResult result;
        try
        {
            result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = assertionOptions,
                StoredPublicKey = stored.PublicKey,
                StoredSignatureCounter = (uint)stored.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(Encoding.UTF8.GetString(args.UserHandle) == userId),
            }, ct).ConfigureAwait(false);
        }
        catch (Fido2VerificationException ex)
        {
            throw new WebAuthnCeremonyException("WebAuthn assertion verification failed.", ex);
        }

        // Apply the synced-passkey counter rule via the store (rejects a positive regression).
        return await credentials.UpdateSignCountAsync(stored.Id, (long)result.SignCount, DateTime.UtcNow, ct)
            .ConfigureAwait(false);
    }

    /// <summary>True once the configured RP id / origins are present (required outside Development).</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_options.RpId) && _options.Origins.Length > 0;
}

/// <summary>A WebAuthn ceremony failure (mismatched origin/RP-id, invalid attestation/assertion, malformed input).</summary>
public sealed class WebAuthnCeremonyException : Exception
{
    public WebAuthnCeremonyException(string message)
        : base(message)
    {
    }

    public WebAuthnCeremonyException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
