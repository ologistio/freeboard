using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Account.Password;

/// <summary>
/// Change-password screen, backed by the same flow the API exposes. Under <c>/account</c>, so the
/// page policy already requires an authenticated session; the user and session ids come from the
/// bearer claims the cookie bridge supplies. The flow revokes the user's other sessions and keeps
/// this one. Errors are generic (a wrong current password is reported the same way as any other
/// validation failure).
/// </summary>
public sealed class ChangeModel(
    IPasswordCredentialStore credentials, IPasswordHasher hasher, IServiceProvider serviceProvider) : PageModel
{
    public bool Changed { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(string? old_password, string? new_password, CancellationToken ct)
    {
        var result = await AuthFlows.ChangePasswordAsync(
            User.FindFirst(AuthClaims.UserId)?.Value,
            User.FindFirst(AuthClaims.SessionId)?.Value,
            old_password, new_password, credentials, hasher, serviceProvider, ct).ConfigureAwait(false);

        switch (result)
        {
            case AuthFlows.PasswordResult.Ok:
                Changed = true;
                return Page();
            case AuthFlows.PasswordResult.Invalid invalid:
                ModelState.AddModelError(string.Empty, invalid.Message);
                return Page();
            default:
                ModelState.AddModelError(string.Empty, "Unable to change the password.");
                return Page();
        }
    }
}
