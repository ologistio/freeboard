using Freeboard.Compliance;
using Freeboard.Persistence;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Compliance;

/// <summary>
/// Read-only server-rendered Statement of Applicability for a chosen standard, scoped to the selected
/// organisation and its descendants. GET-only, so the GitOps read-only middleware never blocks it.
/// Derives its ENTIRE scope from its own store reads and consumes the layout selection resolver for
/// nothing: it reads standards for the selector and the Statement-of-Applicability inputs
/// (organisations, scopes, requirements, requirement-scopes) in one repeatable-read snapshot through
/// <see cref="IComplianceStore"/> inside one try/catch that sets <see cref="StoreUnreachable"/>, so a
/// store outage renders an in-page notice rather than a 500. The projection resolves inheritance over
/// the full tree first, then filters the node list to the in-scope set, so a selected department still
/// inherits a disposition from a company above it.
/// </summary>
public sealed class StatementOfApplicabilityModel(IComplianceStore store, IOrgAccess orgAccess) : PageModel
{
    /// <summary>All standards, ordered by id, for the selector.</summary>
    public IReadOnlyList<StandardRow> Standards { get; private set; } = [];

    /// <summary>The chosen standard id, or null when none is selected yet.</summary>
    public string? StandardId { get; private set; }

    /// <summary>The resolved, in-scope node list for the chosen standard, ordered by id.</summary>
    public IReadOnlyList<SoaNode> Nodes { get; private set; } = [];

    /// <summary>Set when the store is unreachable; rendered as an in-page notice.</summary>
    public bool StoreUnreachable { get; private set; }

    /// <summary>The active scope shown above the table: the selected organisation's title, or "All Organisations".</summary>
    public string ActiveScope { get; private set; } = "All Organisations";

    public async Task OnGetAsync(string? standard, CancellationToken ct)
    {
        try
        {
            Standards = (await store.GetStandardsAsync(ct).ConfigureAwait(false))
                .OrderBy(s => s.Id, StringComparer.Ordinal).ToList();

            StandardId = string.IsNullOrEmpty(standard) ? null : standard;
            if (StandardId is null)
            {
                return;
            }

            var inputs = await store.GetStatementOfApplicabilityInputsAsync(ct).ConfigureAwait(false);

            // Derive the whole scope from the page's own reads, never from the layout resolver: the
            // resolver degrades a failed org load to an empty list and "All Organisations", which would
            // silently drop this page's cookie-selected subtree or hide a real outage.
            var accessibleIds = orgAccess.AccessibleOrgIds(User, inputs.Organisations);
            var selectedId = OrgSelection.Resolve(OrgSelection.ReadCandidate(HttpContext), accessibleIds);
            var inScope = OrgScope.InScopeIds(inputs.Organisations, accessibleIds, selectedId);

            // Resolve over the full tree first, then filter: filtering before resolving would drop
            // ancestors above the selection and lose inherited dispositions.
            var resolved = global::Freeboard.Compliance.StatementOfApplicability.Resolve(
                inputs.Organisations, inputs.Scopes, inputs.Requirements, inputs.RequirementScopes, StandardId);
            Nodes = resolved.Where(n => inScope.Contains(n.Id)).ToList();

            ActiveScope = selectedId is null
                ? "All Organisations"
                : inputs.Organisations.FirstOrDefault(o => string.Equals(o.Id, selectedId, StringComparison.Ordinal))?.Title
                    ?? "All Organisations";
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            StoreUnreachable = true;
        }
    }

    /// <summary>Human label for a node's disposition: In, Out, or Undetermined.</summary>
    public static string DispositionLabel(SoaNode node) => node.Disposition ?? "Undetermined";

    private static bool IsStoreFailure(Exception ex) =>
        ex is global::System.Data.Common.DbException or InvalidOperationException or TimeoutException;
}
