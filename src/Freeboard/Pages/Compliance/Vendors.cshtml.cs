using Freeboard.GitOps;
using Freeboard.Persistence;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Freeboard.Pages.Compliance;

/// <summary>
/// One vendor as the register renders it: the asset, the title of the organisation that owns it (null
/// when that owner is not itself readable), and its scopes ordered by id.
/// </summary>
public sealed record VendorRow(AssetNode Vendor, string? OwnerTitle, IReadOnlyList<ScopeRow> Scopes)
{
    /// <summary>How many of this vendor's scopes exclude their target - the number the register exists to surface.</summary>
    public int ExceptionCount => Scopes.Count(s => s.Disposition == "Out");
}

/// <summary>
/// Read-only server-rendered vendor register: each vendor the caller may see and, alongside it, its
/// vendor-subject scopes (target, disposition, and - for every Out - the justification, so an exception is
/// never silent). GET-only, so the GitOps read-only middleware never blocks it. Reads vendors and the
/// unified scopes through <see cref="IComplianceStore"/> in-process (like the Statement of
/// Applicability page) inside one try/catch that sets <see cref="StoreUnreachable"/>, so a store
/// outage renders an in-page notice rather than a 500. A vendor is shown when it is in the caller's
/// accessible asset set, which admits it exactly when its owner resolves into the caller's organisation
/// union; a vendor with a null or dangling owner is hidden (fail-closed), and its scope justifications
/// are hidden with it.
/// </summary>
public sealed class VendorsModel(
    IComplianceStore store, IAssetAccess assetAccess, IOptions<GitOpsOptions> gitOps) : PageModel
{
    /// <summary>The vendors the caller may read, ordered by id.</summary>
    public IReadOnlyList<VendorRow> Vendors { get; private set; } = [];

    /// <summary>Set when the store is unreachable; rendered as an in-page notice.</summary>
    public bool StoreUnreachable { get; private set; }

    /// <summary>True when git owns the register, so the page offers no way to add a vendor here.</summary>
    public bool GitOpsReadOnly => gitOps.Value.ReadOnly;

    /// <summary>Every readable vendor's scopes, both dispositions.</summary>
    public int ScopeCount => Vendors.Sum(v => v.Scopes.Count);

    /// <summary>Every readable vendor's Out scopes.</summary>
    public int ExceptionCount => Vendors.Sum(v => v.ExceptionCount);

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            var assets = await store.GetAssetsAsync(ct).ConfigureAwait(false);
            var accessible = await assetAccess.AccessibleAssetIdsAsync(User, assets, ct).ConfigureAwait(false);

            // Vendor exceptions are the unified scopes whose subject is a visible vendor.
            var scopesBySubject = (await store.GetScopesAsync(ct).ConfigureAwait(false))
                .GroupBy(s => s.Subject, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<ScopeRow>)g.OrderBy(s => s.Id, StringComparer.Ordinal).ToList(),
                    StringComparer.Ordinal);

            // An owner title is a readable asset's title, never a raw id: the vendor is only visible
            // because its owner resolved into the caller's union, so the owner is in the same set.
            var titles = assets
                .Where(a => accessible.Contains(a.Id))
                .ToDictionary(a => a.Id, a => a.Title, StringComparer.Ordinal);

            Vendors = assets
                .Where(a => a.Type is "Vendor" && accessible.Contains(a.Id))
                .OrderBy(v => v.Id, StringComparer.Ordinal)
                .Select(v => new VendorRow(
                    v,
                    v.Owner is not null && titles.TryGetValue(v.Owner, out var owner) ? owner : null,
                    scopesBySubject.TryGetValue(v.Id, out var scopes) ? scopes : []))
                .ToList();
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            StoreUnreachable = true;
        }
    }

    /// <summary>
    /// The display label for a data class token. The token is the authored id, which stays a lowercase
    /// slug because an admin-editable taxonomy is planned; an unrecognized token renders as authored
    /// rather than being hidden.
    /// </summary>
    public static string DataClassLabel(string dataClass) => dataClass switch
    {
        "pii" => "PII",
        "phi" => "PHI",
        "special-category" => "Special category",
        "payment-card" => "Payment card",
        "credentials" => "Credentials",
        _ => dataClass,
    };

    /// <summary>The target label for a scope: its requirement or control id (exactly one is set).</summary>
    public static string TargetLabel(ScopeRow scope) => scope.Requirement ?? scope.Control ?? "-";

    /// <summary>The target kind for a scope: "requirement" or "control".</summary>
    public static string TargetKind(ScopeRow scope) => scope.Requirement is not null ? "requirement" : "control";

    private static bool IsStoreFailure(Exception ex) =>
        ex is global::System.Data.Common.DbException or InvalidOperationException or TimeoutException;
}
