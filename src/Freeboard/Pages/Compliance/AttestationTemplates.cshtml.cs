using Freeboard.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Compliance;

/// <summary>
/// Read-only server-rendered attestation-template register: control-centric, showing each control that
/// has templates and, under it, its templates (type, body, fields, and for a training template the pass
/// mark and quiz prompts/options). GET-only, so the GitOps read-only middleware never blocks it. Reads
/// controls and templates through <see cref="IComplianceStore"/> in-process (like the Evidence Collectors
/// page) inside one try/catch that sets <see cref="StoreUnreachable"/>, so a store outage renders an
/// in-page notice rather than a 500. Templates are org-independent reference data, so the page does NOT
/// narrow by accessible organisation: any authenticated user sees every template. The quiz renders no
/// answer - the read model carries none.
/// </summary>
public sealed class AttestationTemplatesModel(IComplianceStore store) : PageModel
{
    /// <summary>Controls that have at least one template, ordered by id.</summary>
    public IReadOnlyList<ControlRow> Controls { get; private set; } = [];

    /// <summary>Set when the store is unreachable; rendered as an in-page notice.</summary>
    public bool StoreUnreachable { get; private set; }

    private IReadOnlyDictionary<string, List<AttestationTemplateRow>> templatesByControl =
        new Dictionary<string, List<AttestationTemplateRow>>(StringComparer.Ordinal);

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            templatesByControl = (await store.GetAttestationTemplatesAsync(ct).ConfigureAwait(false))
                .GroupBy(t => t.Control, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            // Only controls with templates are shown; a template-free control would add noise.
            Controls = (await store.GetControlsAsync(ct).ConfigureAwait(false))
                .Where(c => templatesByControl.ContainsKey(c.Id))
                .OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            StoreUnreachable = true;
        }
    }

    /// <summary>The templates attached to one control, ordered by id; empty when it has none.</summary>
    public IReadOnlyList<AttestationTemplateRow> TemplatesFor(string controlId) =>
        templatesByControl.TryGetValue(controlId, out var templates) ? templates : [];

    private static bool IsStoreFailure(Exception ex) =>
        ex is global::System.Data.Common.DbException or InvalidOperationException or TimeoutException;
}
