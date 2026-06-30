using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Account;

/// <summary>
/// Forced-reset completion, backed by the same flow the API's <c>account/password</c> exposes
/// (new password only, no old password). A force-reset-limited session is funnelled here; on success
/// the shared flow clears the force-reset flag and upgrades THIS session to full (the bearer token
/// is unchanged), so the user continues authenticated.
///
/// Under <c>/account</c>, so the page policy requires an authenticated session (a limited session is
/// authenticated). The route carries the limited-session-allowed marker so the force-reset guard
/// permits a limited session here; the flow's own re-check still rejects a full session that has no
/// force-reset flag.
/// </summary>
public sealed class CompleteResetModel(
    IUserStore users,
    IPasswordCredentialStore credentials,
    IPasswordHasher hasher,
    IServiceProvider serviceProvider) : PageModel
{
    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(string? new_password, CancellationToken ct)
    {
        var result = await AuthFlows.AccountPasswordAsync(
            User.FindFirst(AuthClaims.UserId)?.Value,
            User.FindFirst(AuthClaims.SessionId)?.Value,
            IsForceResetLimited(),
            new_password, users, credentials, hasher, serviceProvider, ct).ConfigureAwait(false);

        switch (result)
        {
            case AuthFlows.PasswordResult.Ok:
                return Redirect("/account");
            case AuthFlows.PasswordResult.Invalid invalid:
                ModelState.AddModelError(string.Empty, invalid.Message);
                return Page();
            case AuthFlows.PasswordResult.Forbidden:
                // The session is not force-reset-limited (or the flag is already cleared): nothing to
                // complete here, so send them to the account landing.
                return Redirect("/account");
            default:
                ModelState.AddModelError(string.Empty, "Unable to set the password.");
                return Page();
        }
    }

    private bool IsForceResetLimited()
        => int.TryParse(
               User.FindFirst(AuthClaims.AuthState)?.Value,
               System.Globalization.NumberStyles.None,
               System.Globalization.CultureInfo.InvariantCulture,
               out var state)
           && state == (int)SessionAuthState.ForceResetLimited;
}
