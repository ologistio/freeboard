using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Account.Mfa;

/// <summary>
/// MFA management status, backed by the same status flow the API exposes: whether TOTP is enrolled,
/// the registered passkeys, and how many recovery codes remain. Read-only (GET), under <c>/account</c>
/// so the page policy requires an authenticated session. Links out to the sudo-gated enroll/manage
/// actions. This is the "Back to security settings" target the passkey and recovery pages reference.
/// </summary>
public sealed class IndexModel(
    IWebAuthnCredentialStore webAuthn, ITotpStore totp, IRecoveryCodeStore recovery) : PageModel
{
    public bool TotpEnrolled { get; private set; }

    public IReadOnlyList<WebAuthnCredentialRow> Passkeys { get; private set; } = [];

    public int RecoveryCodesRemaining { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var status = await AuthFlows.MfaStatusAsync(
            User.FindFirst(AuthClaims.UserId)?.Value, webAuthn, totp, recovery, ct).ConfigureAwait(false);
        if (status is not null)
        {
            TotpEnrolled = status.Totp;
            Passkeys = status.Passkeys;
            RecoveryCodesRemaining = status.RecoveryCodesRemaining;
        }
    }
}
