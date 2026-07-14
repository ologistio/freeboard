using Freeboard.Navigation;
using Microsoft.AspNetCore.Mvc;

namespace Freeboard.Pages.Shared.Components.ShellPalette;

/// <summary>
/// Renders the command palette (N7) from the same resolved <see cref="ShellNavCatalog"/> that drives the
/// rail, so the two cannot disagree and a destination the viewer cannot reach appears in neither. The
/// Page options are the resolved nav items (label and route only - the palette ignores the rail's
/// active-item marking); one static Command option toggles the theme. Gating lives in
/// <see cref="ShellNavResolver"/>, so this view holds no authz or entitlement logic.
/// </summary>
public sealed class ShellPaletteViewComponent(ShellNavResolver resolver) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var path = HttpContext.Request.Path.Value ?? "/";
        var nav = await resolver.ResolveAsync(HttpContext.User, path, activeKey: null, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return View(nav);
    }
}
