using System.Text.Json;
using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Freeboard.Pages.Login.Mfa;

/// <summary>
/// Login-MFA passkey challenge. Anonymous (the user is mid-login). The GET renders the assertion
/// options (cached on the challenge row at login) plus an antiforgery token for the JS shim, which
/// runs the WebAuthn ceremony and POSTs the assertion JSON back with the antiforgery token in the
/// <c>RequestVerificationToken</c> header. The POST drives the shared passkey verify with the held
/// <c>mfa_token</c>; on success it issues the full session and makes the nonce single-use, reusing the
/// same cap-exhaustion cleanup as the other factors.
/// </summary>
public sealed class PasskeyModel(
    PendingMfaStore pendingMfa,
    MfaChallengeService mfa,
    WebAuthnCeremony webAuthn,
    IUserStore users,
    AuthRateLimiter rateLimiter,
    IAntiforgery antiforgery,
    IOptions<WebAuthOptions> webAuthOptions)
    : MfaChallengePageModel(pendingMfa, mfa, webAuthOptions)
{
    private readonly MfaChallengeService _mfa = mfa;
    private readonly IUserStore _users = users;
    private readonly AuthRateLimiter _rateLimiter = rateLimiter;

    /// <summary>The cached assertion options JSON for the JS ceremony, set on a usable pending challenge.</summary>
    public string? OptionsJson { get; private set; }

    /// <summary>The antiforgery request token the JS shim echoes in the RequestVerificationToken header.</summary>
    public string? AntiforgeryToken { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (!await LoadPendingAsync(MfaFactors.Passkey, ct).ConfigureAwait(false))
        {
            return;
        }

        OptionsJson = await AuthFlows.MfaPasskeyOptionsAsync(MfaToken, _mfa, ct).ConfigureAwait(false);
        AntiforgeryToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!await LoadPendingAsync(MfaFactors.Passkey, ct).ConfigureAwait(false))
        {
            return Page();
        }

        var assertion = await ReadAssertionAsync(ct).ConfigureAwait(false);

        var result = await AuthFlows.MfaVerifyAsync(
            MfaToken, MfaFactors.Passkey, HttpContext.Connection.RemoteIpAddress?.ToString(),
            async challenge =>
            {
                if (challenge.WebAuthnOptions is null || assertion is null)
                {
                    return false;
                }

                try
                {
                    return await webAuthn
                        .VerifyAssertionAsync(challenge.UserId, challenge.WebAuthnOptions, assertion, ct)
                        .ConfigureAwait(false);
                }
                catch (WebAuthnCeremonyException)
                {
                    return false;
                }
            },
            _mfa, _users, _rateLimiter, ct).ConfigureAwait(false);

        return result is AuthFlows.MfaVerifyResult.Success success
            ? CompleteSession(success.Token)
            : await AfterFailedVerifyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads the opaque assertion JSON the JS shim POSTs as <c>{ "assertion": "..." }</c>.</summary>
    private async Task<string?> ReadAssertionAsync(CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("assertion", out var p) ? p.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
