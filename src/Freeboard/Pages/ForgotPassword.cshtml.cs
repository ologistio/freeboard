using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Freeboard.Pages;

/// <summary>
/// Forgot-password screen. Anonymous (reached before authentication). The POST always renders the
/// same "if that account exists, we sent a link" confirmation regardless of whether the account
/// exists, matching the API's uniform 200 - the shared flow does the enumeration-safe work (mints and
/// sends only for a real account, swallowing any send failure into the same uniform outcome).
/// </summary>
public sealed class ForgotPasswordModel(
    IUserStore users,
    IPasswordResetStore resets,
    IOptions<WebAuthOptions> options,
    ILoggerFactory loggerFactory,
    IServiceProvider serviceProvider) : PageModel
{
    public bool Submitted { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(string? email, CancellationToken ct)
    {
        await AuthFlows.ForgotPasswordAsync(email, users, resets, options, loggerFactory, serviceProvider, ct)
            .ConfigureAwait(false);
        Submitted = true;
        return Page();
    }
}
