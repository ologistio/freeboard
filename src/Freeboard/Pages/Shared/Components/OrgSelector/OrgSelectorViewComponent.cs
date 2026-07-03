using Freeboard.Persistence;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc;

namespace Freeboard.Pages.Shared.Components.OrgSelector;

/// <summary>One organisation node in the selector tree, with its accessible children.</summary>
public sealed record OrgSelectorNode(string Id, string Title, IReadOnlyList<OrgSelectorNode> Children);

/// <summary>
/// The selector view model: the accessible root nodes, the current selection, and the return target
/// each entry carries so a selection preserves the current page and its query state.
/// </summary>
public sealed record OrgSelectorViewModel(
    IReadOnlyList<OrgSelectorNode> Roots, string? SelectedId, string ReturnTarget);

/// <summary>
/// Renders the accessible organisation tree plus an "All Organisations" entry in the layout sidebar.
/// Consumes the request-scoped <see cref="OrgSelectionResolver"/> (no direct store read) and builds
/// the tree from the accessible organisations only. Degrades to just "All Organisations" when there
/// are no accessible organisations or the resolver degraded because the store was unreachable, so a
/// store outage never throws into the layout.
/// </summary>
public sealed class OrgSelectorViewComponent(OrgSelectionResolver resolver) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var state = await resolver.GetAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        var accessible = state.Organisations
            .Where(o => state.AccessibleIds.Contains(o.Id))
            .ToList();
        var roots = BuildRoots(accessible);

        var returnTarget = HttpContext.Request.Path + HttpContext.Request.QueryString;
        return View(new OrgSelectorViewModel(roots, state.SelectedId, returnTarget));
    }

    private static IReadOnlyList<OrgSelectorNode> BuildRoots(IReadOnlyList<OrganisationRow> organisations)
    {
        var ids = organisations.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var childrenByParent = organisations
            .Where(o => o.Parent is not null && ids.Contains(o.Parent))
            .GroupBy(o => o.Parent!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // A node is a root when it has no parent or its parent is outside the accessible set.
        var roots = organisations
            .Where(o => o.Parent is null || !ids.Contains(o.Parent))
            .OrderBy(o => o.Id, StringComparer.Ordinal)
            .Select(o => Build(o, childrenByParent))
            .ToList();
        return roots;
    }

    private static OrgSelectorNode Build(
        OrganisationRow organisation, IReadOnlyDictionary<string, List<OrganisationRow>> childrenByParent)
    {
        var children = childrenByParent.TryGetValue(organisation.Id, out var kids)
            ? kids.OrderBy(o => o.Id, StringComparer.Ordinal)
                .Select(child => Build(child, childrenByParent))
                .ToList()
            : (IReadOnlyList<OrgSelectorNode>)[];
        return new OrgSelectorNode(organisation.Id, organisation.Title, children);
    }
}
