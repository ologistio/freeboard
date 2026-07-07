using Freeboard.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Compliance;

/// <summary>
/// Read-only server-rendered vendor register: every persisted vendor and, under each, its
/// vendor-scopes (target, disposition, and - for every Out - the justification, so an exception is
/// never silent). GET-only, so the GitOps read-only middleware never blocks it. Reads vendors and
/// vendor-scopes through <see cref="IComplianceStore"/> in-process (like the Statement of
/// Applicability page) inside one try/catch that sets <see cref="StoreUnreachable"/>, so a store
/// outage renders an in-page notice rather than a 500. Vendors are org-independent reference data, so
/// the page does NOT narrow by accessible organisation: any authenticated user sees every vendor.
/// </summary>
public sealed class VendorsModel(IComplianceStore store) : PageModel
{
    /// <summary>All vendors, ordered by id.</summary>
    public IReadOnlyList<VendorRow> Vendors { get; private set; } = [];

    /// <summary>Set when the store is unreachable; rendered as an in-page notice.</summary>
    public bool StoreUnreachable { get; private set; }

    private IReadOnlyDictionary<string, List<VendorScopeRow>> scopesByVendor =
        new Dictionary<string, List<VendorScopeRow>>(StringComparer.Ordinal);

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            Vendors = (await store.GetVendorsAsync(ct).ConfigureAwait(false))
                .OrderBy(v => v.Id, StringComparer.Ordinal).ToList();

            scopesByVendor = (await store.GetVendorScopesAsync(ct).ConfigureAwait(false))
                .GroupBy(s => s.Vendor, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            StoreUnreachable = true;
        }
    }

    /// <summary>The vendor-scopes for one vendor, ordered by id; empty when the vendor has none.</summary>
    public IReadOnlyList<VendorScopeRow> ScopesFor(string vendorId) =>
        scopesByVendor.TryGetValue(vendorId, out var scopes) ? scopes : [];

    /// <summary>The target label for a scope: its requirement or control id (exactly one is set).</summary>
    public static string TargetLabel(VendorScopeRow scope) => scope.Requirement ?? scope.Control ?? "-";

    /// <summary>The target kind for a scope: "requirement" or "control".</summary>
    public static string TargetKind(VendorScopeRow scope) => scope.Requirement is not null ? "requirement" : "control";

    private static bool IsStoreFailure(Exception ex) =>
        ex is global::System.Data.Common.DbException or InvalidOperationException or TimeoutException;
}
