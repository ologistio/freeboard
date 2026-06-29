using Freeboard.Persistence.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Freeboard.Auth;

/// <summary>The MFA factor identifiers used in the challenge factor list and verify routing.</summary>
public static class MfaFactors
{
    public const string Passkey = "passkey";
    public const string Totp = "totp";
    public const string Recovery = "recovery";
    public const string MagicLink = "magic_link";
}

/// <summary>
/// Computes the MFA factors currently usable by a user - the SAME set the login challenge
/// offers and that /auth/sudo will accept. Passkey if a credential exists; TOTP if confirmed;
/// recovery always when unused codes remain; magic-link as a FALLBACK only when MFA is enabled, the
/// user has NO passkey and NO TOTP, AND an email sender is registered (every user has an email).
/// </summary>
public sealed class MfaFactorService(
    IWebAuthnCredentialStore webAuthn,
    ITotpStore totp,
    IRecoveryCodeStore recovery,
    IServiceProvider services)
{
    public async Task<IReadOnlyList<string>> AvailableAsync(UserRow user, CancellationToken ct = default)
    {
        var factors = new List<string>();

        var hasPasskey = (await webAuthn.ListByUserAsync(user.Id, ct).ConfigureAwait(false)).Count > 0;
        var hasTotp = await totp.IsConfirmedAsync(user.Id, ct).ConfigureAwait(false);
        var hasRecovery = await recovery.CountRemainingAsync(user.Id, ct).ConfigureAwait(false) > 0;

        if (hasPasskey)
        {
            factors.Add(MfaFactors.Passkey);
        }

        if (hasTotp)
        {
            factors.Add(MfaFactors.Totp);
        }

        if (hasRecovery)
        {
            factors.Add(MfaFactors.Recovery);
        }

        // Magic-link is the fallback only with no strong factor AND a configured sender.
        if (user.MfaEnabled && !hasPasskey && !hasTotp && services.GetService<AuthEmailService>() is not null)
        {
            factors.Add(MfaFactors.MagicLink);
        }

        return factors;
    }
}
