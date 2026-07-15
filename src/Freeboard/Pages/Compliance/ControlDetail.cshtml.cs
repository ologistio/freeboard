using Freeboard.Compliance;
using Freeboard.Pages.Shared;
using Freeboard.Persistence;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Compliance;

/// <summary>
/// The full-page control detail: the O4 direct-link and no-JavaScript target for a Statement of
/// Applicability control, rendering the same shared <see cref="ObjectDetailView"/> anatomy as the drawer.
///
/// Authorization binds to the caller's full accessible organisation set, not the active list scope, so a
/// direct URL for any accessible org renders. A missing control, or one whose org lies outside that set,
/// returns not-found and discloses no record name or facet, so a direct URL cannot probe for hidden records.
/// </summary>
public sealed class ControlDetailModel(
    IComplianceStore store, IOrgAccess orgAccess, IEvidenceStore evidenceStore) : PageModel
{
    private const string UnknownStatus = "Unknown";

    private const string SoaHref = "/compliance/statement-of-applicability";

    /// <summary>Set when the store is unreachable; rendered as an in-page notice instead of a 500.</summary>
    public bool StoreUnreachable { get; private set; }

    /// <summary>The projected control anatomy and its full-page actions, or null when unreachable.</summary>
    public ObjectDetailPartialModel? Detail { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        string? standard, string? org, string? requirement, string? control, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(standard) || string.IsNullOrEmpty(org)
            || string.IsNullOrEmpty(requirement) || string.IsNullOrEmpty(control))
        {
            return NotFound();
        }

        try
        {
            var standards = await store.GetStandardsAsync(ct).ConfigureAwait(false);
            if (!standards.Any(s => string.Equals(s.Id, standard, StringComparison.Ordinal)))
            {
                return NotFound();
            }

            var inputs = await store.GetStatementOfApplicabilityDrilldownInputsAsync(ct).ConfigureAwait(false);

            // Authorize against every org the caller may see - not the active-scope-narrowed set or the
            // org-selection cookie - so a direct link to any accessible org renders. Out-of-set is not-found.
            var accessibleIds = await orgAccess.AccessibleOrgIdsAsync(User, inputs.Organisations, ct).ConfigureAwait(false);
            if (!accessibleIds.Contains(org))
            {
                return NotFound();
            }

            var resolved = global::Freeboard.Compliance.StatementOfApplicability.ResolveDrilldown(
                inputs.Organisations, inputs.Scopes, inputs.Requirements, inputs.RequirementScopes,
                inputs.Controls, inputs.Collectors, inputs.Templates, inputs.Vendors, standard);

            var node = resolved.FirstOrDefault(n => string.Equals(n.Id, org, StringComparison.Ordinal));
            var requirementNode = node?.Requirements
                .FirstOrDefault(r => string.Equals(r.Id, requirement, StringComparison.Ordinal));
            var controlNode = requirementNode?.Controls
                .FirstOrDefault(c => string.Equals(c.Id, control, StringComparison.Ordinal));
            if (requirementNode is null || controlNode is null)
            {
                return NotFound();
            }

            // The same per-collector evidence-status read the list page runs, for the requested org, so the
            // full page feeds the shared partial the same per-check statuses the drawer templates do.
            var collectorStatuses = new Dictionary<(string, string, string), string>();
            var statuses = await evidenceStore.GetCollectorEvidenceStatusesAsync([org], ct).ConfigureAwait(false);
            foreach (var status in statuses)
            {
                collectorStatuses[(status.OrganisationId, status.RequirementId, status.CollectorId)] = status.Status;
            }

            string Resolver(string organisationId, string requirementId, string collectorId) =>
                collectorStatuses.TryGetValue((organisationId, requirementId, collectorId), out var found)
                    ? found
                    : UnknownStatus;

            // N9: return to the list with the same standard selected, so the control tree the user came
            // from is restored rather than the empty no-standard list.
            var soaHref = $"{SoaHref}?standard={Uri.EscapeDataString(standard)}";

            var view = ControlDetailProjection.Map(org, requirementNode, controlNode, Resolver);
            Detail = new ObjectDetailPartialModel(
                view, soaHref, "Back to Statement of Applicability", TitleAsPageHeading: true);

            // N8 breadcrumb "Comply / Statement of Applicability / <control>". Title supplies the leaf, so
            // BreadcrumbDetail is deliberately unset (setting it would duplicate the control crumb). This
            // page 404s on its bare path, so the leaf declares its full self-URL to stay a working link.
            ViewData["Title"] = controlNode.Title;
            ViewData["BreadcrumbTitleHref"] = Request.Path + Request.QueryString;
            ViewData["NavGroup"] = "Comply";
            ViewData["NavItem"] = "soa";
            ViewData["BreadcrumbParent"] = "Statement of Applicability";
            ViewData["BreadcrumbParentHref"] = soaHref;

            return Page();
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            StoreUnreachable = true;
            ViewData["Title"] = "Control";
            ViewData["BreadcrumbTitleHref"] = Request.Path + Request.QueryString;
            ViewData["NavGroup"] = "Comply";
            ViewData["NavItem"] = "soa";
            ViewData["BreadcrumbParent"] = "Statement of Applicability";
            ViewData["BreadcrumbParentHref"] = $"{SoaHref}?standard={Uri.EscapeDataString(standard)}";
            return Page();
        }
    }

    private static bool IsStoreFailure(Exception ex) =>
        ex is global::System.Data.Common.DbException or InvalidOperationException or TimeoutException;
}
