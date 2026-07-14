using Freeboard.Compliance;
using Freeboard.Pages.Shared;
using Freeboard.Persistence;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Compliance;

/// <summary>
/// The full-page control detail: the O4 direct-link and no-JavaScript target for a Statement of
/// Applicability control. It re-runs the existing drill-down reads (in the same store-unreachable
/// try/catch as the list page) plus the same per-collector evidence-status sidecar read, selects the one
/// control identified by its (standard, organisation, requirement, control) tuple, and projects it into
/// the shared <see cref="ObjectDetailView"/> through <see cref="ControlDetailProjection"/> - the one
/// mapping the list page's drawer templates also use, so the drawer and this page cannot diverge. GET-only
/// (read-only-middleware safe) and authenticated by the folder-level policy on <c>/Compliance</c>.
///
/// Authorization binds to the caller's FULL accessible organisation set, not the active list scope: a
/// direct URL for any accessible org renders regardless of which org the active-scope cookie selects. A
/// missing control, or one whose org lies outside the accessible set, returns not-found and discloses no
/// record name or facet, so a direct URL cannot probe for records the caller may not see.
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

            var view = ControlDetailProjection.Map(org, requirementNode, controlNode, Resolver);
            Detail = new ObjectDetailPartialModel(view, SoaHref, "Back to Statement of Applicability");

            // N8 breadcrumb "Comply / Statement of Applicability / <control>". Title supplies the leaf, so
            // BreadcrumbDetail is deliberately unset (setting it would duplicate the control crumb).
            ViewData["Title"] = controlNode.Title;
            ViewData["NavGroup"] = "Comply";
            ViewData["NavItem"] = "soa";
            ViewData["BreadcrumbParent"] = "Statement of Applicability";
            ViewData["BreadcrumbParentHref"] = SoaHref;

            return Page();
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            StoreUnreachable = true;
            ViewData["Title"] = "Control";
            ViewData["NavGroup"] = "Comply";
            ViewData["NavItem"] = "soa";
            ViewData["BreadcrumbParent"] = "Statement of Applicability";
            ViewData["BreadcrumbParentHref"] = SoaHref;
            return Page();
        }
    }

    private static bool IsStoreFailure(Exception ex) =>
        ex is global::System.Data.Common.DbException or InvalidOperationException or TimeoutException;
}
