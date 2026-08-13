using Freeboard.Authz;
using Freeboard.Compliance;
using Freeboard.Persistence;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Compliance;

/// <summary>
/// Read-only server-rendered collector register: control-centric, showing each control's evaluation
/// rule and, under it, its attached collectors (type, provider, vendor, frequency, threshold, and the
/// typed config). GET-only, so the GitOps read-only middleware never blocks it. Reads controls and
/// collectors through <see cref="IComplianceStore"/> in-process (like the Vendors and Statement of
/// Applicability pages) inside one try/catch that sets <see cref="StoreUnreachable"/>, so a store outage
/// renders an in-page notice rather than a 500. Collectors are org-independent reference data, so the ROW
/// set is not narrowed: any authenticated user sees every control and collector. The vendor IS narrowed -
/// it names a vendor asset, which the owner edge governs - and a collector whose vendor is outside the
/// caller's accessible asset set renders exactly as one with no vendor.
/// </summary>
public sealed class CollectorsModel(
    IComplianceStore store, AuthzRequestCache cache, IAssetAccess assetAccess) : PageModel
{
    /// <summary>All controls, ordered by id.</summary>
    public IReadOnlyList<ControlRow> Controls { get; private set; } = [];

    /// <summary>Set when the store is unreachable; rendered as an in-page notice.</summary>
    public bool StoreUnreachable { get; private set; }

    private IReadOnlyDictionary<string, List<CollectorRow>> collectorsByControl =
        new Dictionary<string, List<CollectorRow>>(StringComparer.Ordinal);

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            Controls = (await store.GetControlsAsync(ct).ConfigureAwait(false))
                .OrderBy(c => c.Id, StringComparer.Ordinal).ToList();

            var assets = await cache.GetAssetsAsync(ct).ConfigureAwait(false);
            var accessible = await assetAccess.AccessibleAssetIdsAsync(User, assets, ct).ConfigureAwait(false);

            collectorsByControl = (await store.GetCollectorsAsync(ct).ConfigureAwait(false))
                .Select(c => c.Vendor is not null && !accessible.Contains(c.Vendor) ? c with { Vendor = null } : c)
                .GroupBy(c => c.Control, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ComplianceEndpoints.IsStoreFailure(ex))
        {
            StoreUnreachable = true;
        }
    }

    /// <summary>The collectors attached to one control, ordered by id; empty when it has none.</summary>
    public IReadOnlyList<CollectorRow> CollectorsFor(string controlId) =>
        collectorsByControl.TryGetValue(controlId, out var collectors) ? collectors : [];

    /// <summary>The evaluation rule to display for a control, defaulting to a dash when unset.</summary>
    public static string EvaluationLabel(ControlRow control) =>
        string.IsNullOrWhiteSpace(control.Evaluation) ? "-" : control.Evaluation;
}
