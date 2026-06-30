using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc;
using Freeboard.Web;
using Microsoft.Extensions.Options;

namespace Freeboard.Pages.Login.Mfa;

/// <summary>
/// Login-MFA TOTP challenge. Anonymous (the user is mid-login). The GET renders the code form when a
/// pending challenge offers TOTP, else a restart message. The POST drives the shared TOTP verify with
/// the held <c>mfa_token</c>; on success it issues the full session and makes the nonce single-use.
/// </summary>
public sealed class TotpModel(
    PendingMfaStore pendingMfa,
    MfaChallengeService mfa,
    ITotpStore totp,
    IUserStore users,
    AuthRateLimiter rateLimiter,
    IOptions<WebAuthOptions> webAuthOptions)
    : MfaChallengePageModel(pendingMfa, mfa, webAuthOptions)
{
    private readonly MfaChallengeService _mfa = mfa;
    private readonly IUserStore _users = users;
    private readonly AuthRateLimiter _rateLimiter = rateLimiter;

    public async Task OnGetAsync(CancellationToken ct)
        => await LoadPendingAsync(MfaFactors.Totp, ct).ConfigureAwait(false);

    public async Task<IActionResult> OnPostAsync(string? code, CancellationToken ct)
    {
        if (!await LoadPendingAsync(MfaFactors.Totp, ct).ConfigureAwait(false))
        {
            return Page();
        }

        var result = await AuthFlows.MfaVerifyAsync(
            MfaToken, MfaFactors.Totp, HttpContext.Connection.RemoteIpAddress?.ToString(),
            challenge => totp.VerifyAsync(challenge.UserId, code ?? string.Empty, ct),
            _mfa, _users, _rateLimiter, ct).ConfigureAwait(false);

        return result is AuthFlows.MfaVerifyResult.Success success
            ? CompleteSession(success.Token)
            : await AfterFailedVerifyAsync(ct).ConfigureAwait(false);
    }
}
