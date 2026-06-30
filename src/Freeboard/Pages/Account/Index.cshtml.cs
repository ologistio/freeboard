using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Account;

/// <summary>
/// The account landing page, backed by the same me flow the API exposes. The page is protected by
/// the named page policy (applied to the <c>/account</c> folder), so an unauthenticated request is
/// redirected to <c>/login</c> by the page challenge scheme before this handler runs.
/// </summary>
public sealed class IndexModel(IUserStore users) : PageModel
{
    public string? UserName { get; private set; }

    public string? UserEmail { get; private set; }

    public string? UserRole { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var user = await AuthFlows.MeAsync(User.FindFirst(AuthClaims.UserId)?.Value, users, ct).ConfigureAwait(false);
        if (user is not null)
        {
            UserName = user.Name;
            UserEmail = user.Email;
            UserRole = user.GlobalRole;
        }
    }
}
