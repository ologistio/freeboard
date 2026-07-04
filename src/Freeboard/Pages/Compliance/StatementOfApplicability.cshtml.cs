using Freeboard.Compliance;
using Freeboard.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Compliance;

/// <summary>
/// Read-only server-rendered Statement of Applicability for a chosen standard. GET-only, so the
/// GitOps read-only middleware never blocks it. Reads through the same <see cref="IComplianceStore"/>
/// as the JSON endpoint and computes the projection with <see cref="StatementOfApplicability"/>; no
/// direct database access. An unreachable store degrades to an in-page notice rather than a 500.
/// </summary>
public sealed class StatementOfApplicabilityModel(IComplianceStore store) : PageModel
{
    /// <summary>All standards, ordered by id, for the selector.</summary>
    public IReadOnlyList<StandardRow> Standards { get; private set; } = [];

    /// <summary>The chosen standard id, or null when none is selected yet.</summary>
    public string? StandardId { get; private set; }

    /// <summary>The resolved node list for the chosen standard, ordered by id.</summary>
    public IReadOnlyList<SoaNode> Nodes { get; private set; } = [];

    /// <summary>Set when the store is unreachable; rendered as an in-page notice.</summary>
    public bool StoreUnreachable { get; private set; }

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
            Nodes = global::Freeboard.Compliance.StatementOfApplicability.Resolve(
                inputs.Organisations, inputs.Scopes, inputs.Requirements, inputs.RequirementScopes, StandardId);
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
