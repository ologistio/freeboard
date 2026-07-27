using Freeboard.Persistence;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Compliance;

/// <summary>
/// Read-only server-rendered vendor register: each vendor the caller may see and, under it, its
/// vendor-subject scopes (target, disposition, and - for every Out - the justification, so an exception is
/// never silent). GET-only, so the GitOps read-only middleware never blocks it. Reads vendors and the
/// unified scopes through <see cref="IComplianceStore"/> in-process (like the Statement of
/// Applicability page) inside one try/catch that sets <see cref="StoreUnreachable"/>, so a store
/// outage renders an in-page notice rather than a 500. A vendor is shown only when its owner is in the
/// caller's accessible-org set; a vendor with a null or dangling owner is hidden (fail-closed), and its
/// scope justifications are hidden with it.
/// </summary>
public sealed class VendorsModel(IComplianceStore store, IOrgAccess orgAccess) : PageModel
{
    /// <summary>All vendors, ordered by id.</summary>
    public IReadOnlyList<VendorRow> Vendors { get; private set; } = [];

    /// <summary>Set when the store is unreachable; rendered as an in-page notice.</summary>
    public bool StoreUnreachable { get; private set; }

    private IReadOnlyDictionary<string, List<ScopeRow>> scopesByVendor =
        new Dictionary<string, List<ScopeRow>>(StringComparer.Ordinal);

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            var accessible = await orgAccess.AccessibleOrgIdsAsync(
                User, await store.GetOrganisationsAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

            Vendors = (await store.GetVendorsAsync(ct).ConfigureAwait(false))
                .Where(v => v.Owner is not null && accessible.Contains(v.Owner))
                .OrderBy(v => v.Id, StringComparer.Ordinal).ToList();

            var visibleVendorIds = Vendors.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
            // Vendor exceptions are the unified scopes whose subject is a visible vendor.
            scopesByVendor = (await store.GetScopesAsync(ct).ConfigureAwait(false))
                .Where(s => visibleVendorIds.Contains(s.Subject))
                .GroupBy(s => s.Subject, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            StoreUnreachable = true;
        }
    }

    /// <summary>The vendor-subject scopes for one vendor, ordered by id; empty when the vendor has none.</summary>
    public IReadOnlyList<ScopeRow> ScopesFor(string vendorId) =>
        scopesByVendor.TryGetValue(vendorId, out var scopes) ? scopes : [];

    /// <summary>The target label for a scope: its requirement or control id (exactly one is set).</summary>
    public static string TargetLabel(ScopeRow scope) => scope.Requirement ?? scope.Control ?? "-";

    /// <summary>The target kind for a scope: "requirement" or "control".</summary>
    public static string TargetKind(ScopeRow scope) => scope.Requirement is not null ? "requirement" : "control";

    private static bool IsStoreFailure(Exception ex) =>
        ex is global::System.Data.Common.DbException or InvalidOperationException or TimeoutException;
}
