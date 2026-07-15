using Freeboard.Auth;
using Freeboard.Navigation;
using Freeboard.TagHelpers;
using Microsoft.AspNetCore.Mvc;

namespace Freeboard.Pages.Shared.Components.ShellTopbar;

/// <summary>One breadcrumb segment: its text and the link it points at.</summary>
public sealed record ShellCrumb(string Text, string Href);

/// <summary>
/// The topbar view model: the breadcrumb segments (group then page then optional detail, N8) and the
/// account identity for the avatar menu.
/// </summary>
public sealed record ShellTopbarViewModel(
    IReadOnlyList<ShellCrumb> Breadcrumb, string DisplayName, string? Email, string Role, string Initials);

/// <summary>
/// Renders the breadcrumb topbar: the linked breadcrumb built from page-declared ViewData, the
/// audit-countdown slot (empty - no backing source), the theme toggle, the notifications slot (bell,
/// no pip - no backing source), and the account menu. The breadcrumb degrades to a single title
/// segment when a page declares no group or detail.
///
/// Breadcrumb ViewData: <c>NavGroup</c> (group segment), an optional <c>BreadcrumbParent</c> /
/// <c>BreadcrumbParentHref</c> segment between group and page (Account subpages set it to Account so
/// they read "group / Account / leaf" while <c>Title</c> stays the leaf and the page's own title tag),
/// <c>Title</c> (page segment, linking to its bare path unless the page declares
/// <c>BreadcrumbTitleHref</c> for a query-keyed self-link), and an optional <c>BreadcrumbDetail</c> /
/// <c>BreadcrumbDetailHref</c>. The group link resolves against the request's visible items, so it never
/// points a non-entitled viewer at a gated destination.
/// </summary>
public sealed class ShellTopbarViewComponent(ShellNavResolver resolver) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var path = HttpContext.Request.Path.Value ?? "/";
        var currentUrl = path + HttpContext.Request.QueryString;

        var group = ViewData["NavGroup"] as string;
        var parent = ViewData["BreadcrumbParent"] as string;
        var parentHref = ViewData["BreadcrumbParentHref"] as string;
        var title = ViewData["Title"] as string;
        var titleHref = ViewData["BreadcrumbTitleHref"] as string;
        var detail = ViewData["BreadcrumbDetail"] as string;
        var detailHref = ViewData["BreadcrumbDetailHref"] as string;

        var crumbs = new List<ShellCrumb>();
        if (!string.IsNullOrWhiteSpace(group))
        {
            crumbs.Add(new ShellCrumb(group, await GroupHrefAsync(group, path).ConfigureAwait(false) ?? path));
        }

        if (!string.IsNullOrWhiteSpace(parent))
        {
            crumbs.Add(new ShellCrumb(parent, string.IsNullOrWhiteSpace(parentHref) ? path : parentHref));
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            // The page (leaf) crumb links to the page's own route. Most pages resolve on their bare path,
            // so that is the default. A page keyed only by query - the control detail - 404s on the bare
            // path, so it declares BreadcrumbTitleHref with its full self-URL (path plus query) to stay a
            // working self-link (N8); it does not change the leaf href of any other page.
            crumbs.Add(new ShellCrumb(title, string.IsNullOrWhiteSpace(titleHref) ? path : titleHref));
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            crumbs.Add(new ShellCrumb(detail, string.IsNullOrWhiteSpace(detailHref) ? currentUrl : detailHref));
        }

        if (crumbs.Count == 0)
        {
            crumbs.Add(new ShellCrumb(title ?? "Freeboard", path));
        }

        var principal = HttpContext.User;
        var name = principal.FindFirst(AuthClaims.Name)?.Value;
        var email = principal.FindFirst(AuthClaims.Email)?.Value;
        var role = principal.FindFirst(AuthClaims.Role)?.Value ?? "member";
        var displayName = string.IsNullOrWhiteSpace(name) ? "Account" : name;
        var initials = OwnerTagHelper.DeriveInitials(name ?? string.Empty);

        return View(new ShellTopbarViewModel(crumbs, displayName, email, role, initials));
    }

    // The group segment links to the group's first VISIBLE item for this request, so a gated first
    // item never becomes a breadcrumb link a non-entitled viewer cannot follow.
    private async Task<string?> GroupHrefAsync(string group, string path)
    {
        var activeKey = ViewData["NavItem"] as string;
        var nav = await resolver.ResolveAsync(HttpContext.User, path, activeKey, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return nav.Groups
            .FirstOrDefault(g => string.Equals(g.Label, group, StringComparison.Ordinal))
            ?.Items.FirstOrDefault()?.Route;
    }
}
