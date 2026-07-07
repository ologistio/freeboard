using Freeboard.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Compliance;

/// <summary>
/// Read-only server-rendered evidence-collector register: control-centric, showing each control's
/// evaluation rule and, under it, its attached collectors (type, vendor, frequency, threshold, and any
/// config). GET-only, so the GitOps read-only middleware never blocks it. Reads controls and collectors
/// through <see cref="IComplianceStore"/> in-process (like the Vendors and Statement of Applicability
/// pages) inside one try/catch that sets <see cref="StoreUnreachable"/>, so a store outage renders an
/// in-page notice rather than a 500. Collectors are org-independent reference data, so the page does NOT
/// narrow by accessible organisation: any authenticated user sees every control and collector.
/// </summary>
public sealed class EvidenceCollectorsModel(IComplianceStore store) : PageModel
{
    /// <summary>All controls, ordered by id.</summary>
    public IReadOnlyList<ControlRow> Controls { get; private set; } = [];

    /// <summary>Set when the store is unreachable; rendered as an in-page notice.</summary>
    public bool StoreUnreachable { get; private set; }

    private IReadOnlyDictionary<string, List<EvidenceCollectorRow>> collectorsByControl =
        new Dictionary<string, List<EvidenceCollectorRow>>(StringComparer.Ordinal);

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            Controls = (await store.GetControlsAsync(ct).ConfigureAwait(false))
                .OrderBy(c => c.Id, StringComparer.Ordinal).ToList();

            collectorsByControl = (await store.GetEvidenceCollectorsAsync(ct).ConfigureAwait(false))
                .GroupBy(c => c.Control, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            StoreUnreachable = true;
        }
    }

    /// <summary>The collectors attached to one control, ordered by id; empty when it has none.</summary>
    public IReadOnlyList<EvidenceCollectorRow> CollectorsFor(string controlId) =>
        collectorsByControl.TryGetValue(controlId, out var collectors) ? collectors : [];

    /// <summary>The evaluation rule to display for a control, defaulting to a dash when unset.</summary>
    public static string EvaluationLabel(ControlRow control) =>
        string.IsNullOrWhiteSpace(control.Evaluation) ? "-" : control.Evaluation;

    private static bool IsStoreFailure(Exception ex) =>
        ex is global::System.Data.Common.DbException or InvalidOperationException or TimeoutException;
}
