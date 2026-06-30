using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc;
using Freeboard.Web;
using Microsoft.Extensions.Options;

namespace Freeboard.Pages.Login.Mfa;

/// <summary>
/// Login-MFA recovery-code challenge. Anonymous (the user is mid-login). The GET renders the
/// recovery-code form when a pending challenge offers recovery, else a restart message. The POST
/// drives the shared recovery verify with the held <c>mfa_token</c>; on success it issues the full
/// session and makes the nonce single-use.
/// </summary>
public sealed class RecoveryModel(
    PendingMfaStore pendingMfa,
    MfaChallengeService mfa,
    IRecoveryCodeStore recovery,
    IUserStore users,
    AuthRateLimiter rateLimiter,
    IOptions<WebAuthOptions> webAuthOptions)
    : MfaChallengePageModel(pendingMfa, mfa, webAuthOptions)
{
    private readonly MfaChallengeService _mfa = mfa;
    private readonly IUserStore _users = users;
    private readonly AuthRateLimiter _rateLimiter = rateLimiter;

    public async Task OnGetAsync(CancellationToken ct)
        => await LoadPendingAsync(MfaFactors.Recovery, ct).ConfigureAwait(false);

    public async Task<IActionResult> OnPostAsync(string? recovery_code, CancellationToken ct)
    {
        if (!await LoadPendingAsync(MfaFactors.Recovery, ct).ConfigureAwait(false))
        {
            return Page();
        }

        var result = await AuthFlows.MfaVerifyAsync(
            MfaToken, MfaFactors.Recovery, HttpContext.Connection.RemoteIpAddress?.ToString(),
            challenge => recovery.ConsumeAsync(challenge.UserId, recovery_code ?? string.Empty, ct),
            _mfa, _users, _rateLimiter, ct).ConfigureAwait(false);

        return result is AuthFlows.MfaVerifyResult.Success success
            ? CompleteSession(success.Token)
            : await AfterFailedVerifyAsync(ct).ConfigureAwait(false);
    }
}
