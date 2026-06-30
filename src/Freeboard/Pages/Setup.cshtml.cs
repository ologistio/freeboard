using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Freeboard.Pages;

/// <summary>
/// First-admin setup. Anonymous (reached before any account exists), driving the same bootstrap flow
/// the API's <c>setup</c> endpoint exposes. On success it mints a full session, sets the
/// <c>__Host-</c> session cookie, and redirects to <c>/account</c>. The page never reveals whether the
/// instance is already initialized beyond the uniform "already set up" the backend allows, and a wrong
/// bootstrap secret surfaces only a generic error (no oracle that distinguishes a wrong secret from a
/// validation failure). The bootstrap secret is never echoed back into the rendered page.
/// </summary>
public sealed class SetupModel(
    IUserStore users,
    IPasswordHasher hasher,
    AuthRateLimiter rateLimiter,
    SessionIssuer sessions,
    IOptions<WebAuthOptions> options,
    IServiceProvider serviceProvider) : PageModel
{
    /// <summary>True when setup is already done: render the "already set up" message, no form.</summary>
    public bool AlreadyInitialized { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(
        string? email, string? name, string? password, string? bootstrap_secret, CancellationToken ct)
    {
        var result = await AuthFlows.BootstrapAsync(
            email, name, password, bootstrap_secret,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            users, hasher, rateLimiter, sessions, options, serviceProvider, ct).ConfigureAwait(false);

        switch (result)
        {
            case AuthFlows.BootstrapResult.Created created:
                SessionCookie.Set(
                    Response, created.Token, DateTimeOffset.UtcNow.Add(options.Value.SessionLifetime));
                return Redirect("/account");

            case AuthFlows.BootstrapResult.AlreadyInitialized:
                AlreadyInitialized = true;
                return Page();

            default:
                // Wrong/absent secret, rate-limit, and validation failure all surface one generic error,
                // so the page is not an oracle for the bootstrap secret or the initialization state.
                ModelState.AddModelError(string.Empty, "Could not complete setup. Check the details and try again.");
                return Page();
        }
    }
}
