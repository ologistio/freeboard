using Freeboard.Navigation;
using Microsoft.AspNetCore.Mvc;

namespace Freeboard.Pages.Shared.Components.ShellRail;

/// <summary>
/// Renders the app-shell nav rail from the resolved <see cref="ShellNavCatalog"/>. Gating, active-item
/// resolution, and counts all come from <see cref="ShellNavResolver"/>, so this view holds no authz or
/// entitlement logic. The active item is the page-declared <c>ViewData["NavItem"]</c> key when present,
/// else the longest route match against the current path.
/// </summary>
public sealed class ShellRailViewComponent(ShellNavResolver resolver) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var path = HttpContext.Request.Path.Value ?? "/";
        var activeKey = ViewData["NavItem"] as string;
        var nav = await resolver.ResolveAsync(HttpContext.User, path, activeKey, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return View(nav);
    }
}
