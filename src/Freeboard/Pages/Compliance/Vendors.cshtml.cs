using System.Globalization;
using Freeboard.Authz;
using Freeboard.Compliance;
using Freeboard.Core.Assets;
using Freeboard.GitOps;
using Freeboard.Persistence;
using Freeboard.TagHelpers;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Freeboard.Pages.Compliance;

/// <summary>
/// One certification as the register renders it: the standard's title (its id when that standard is not
/// resolvable), the derived status, and the expiry wording the cell shows.
/// </summary>
public sealed record VendorAssuranceView(string StandardTitle, AssuranceStatus Status, string Expiry)
{
    /// <summary>
    /// The stamp tone. Valid takes the mark's untinted neutral rather than the pass green: a certification
    /// is a fact on file rather than a Freeboard verdict, and the Status column still reads "Not
    /// evaluated". Red is earned under S3 because a lapsed expiry is an overdue fact, not a judgement.
    /// </summary>
    public StampTone Tone => Status switch
    {
        AssuranceStatus.Expiring => StampTone.Warn,
        AssuranceStatus.Expired => StampTone.Fail,
        _ => StampTone.Neutral,
    };
}

/// <summary>
/// One vendor as the register renders it: the asset, the title of the organisation that owns it (null
/// when that owner is not itself readable), its assurances ordered by standard, and its scopes ordered
/// by id.
/// </summary>
public sealed record VendorRow(
    AssetNode Vendor,
    string? OwnerTitle,
    IReadOnlyList<VendorAssuranceView> Assurances,
    IReadOnlyList<ScopeRow> Scopes)
{
    /// <summary>How many of this vendor's scopes exclude their target - the number the register exists to surface.</summary>
    public int ExceptionCount => Scopes.Count(s => s.Disposition == "Out");

    /// <summary>True when any certification this vendor holds is expiring or already expired.</summary>
    public bool HasLapsingAssurance => Assurances.Any(a => a.Status is not AssuranceStatus.Valid);
}

/// <summary>
/// Read-only server-rendered vendor register: each vendor the caller may see and, alongside it, its
/// certifications and its vendor-subject scopes (target, disposition, and - for every Out - the
/// justification, so an exception is never silent). GET-only, so the GitOps read-only middleware never
/// blocks it. Takes the assets, the assurances, and the scopes - everything its visibility decision rests
/// on - from ONE snapshot on <see cref="AuthzRequestCache"/>, and reads the standards separately, all
/// inside one try/catch that sets <see cref="StoreUnreachable"/>, so a store outage renders an in-page
/// notice rather than a 500. A vendor is shown when it is in the caller's accessible asset set, which
/// admits it exactly when its owner resolves into the caller's organisation union; a vendor with a null
/// or dangling owner is hidden (fail-closed), and its assurances and scope justifications are hidden
/// with it.
/// </summary>
public sealed class VendorsModel(
    IComplianceStore store,
    AuthzRequestCache cache,
    IAssetAccess assetAccess,
    TimeProvider clock,
    IOptions<AssuranceOptions> assurance,
    IOptions<GitOpsOptions> gitOps) : PageModel
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

    /// <summary>Readable vendors holding at least one expiring or expired certification, counted once each.</summary>
    public int LapsingVendorCount => Vendors.Count(v => v.HasLapsingAssurance);

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            var snapshot = await cache.GetSnapshotAsync(
                ComplianceReadSet.Assets | ComplianceReadSet.VendorAssurances | ComplianceReadSet.Scopes, ct)
                .ConfigureAwait(false);
            var assets = snapshot.Assets;
            var accessible = await assetAccess.AccessibleAssetIdsAsync(User, assets, ct).ConfigureAwait(false);

            // Vendor exceptions are the unified scopes whose subject is a visible vendor.
            var scopesBySubject = snapshot.Scopes
                .GroupBy(s => s.Subject, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<ScopeRow>)g.OrderBy(s => s.Id, StringComparer.Ordinal).ToList(),
                    StringComparer.Ordinal);

            // An unresolvable standard renders as its id, so this title read costs a label rather than a
            // narrowing decision and stays outside the snapshot.
            var standardTitles = (await store.GetSnapshotAsync(ComplianceReadSet.Standards, ct).ConfigureAwait(false))
                .Standards.ToDictionary(s => s.Id, s => s.Title, StringComparer.Ordinal);

            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var warnWindowDays = assurance.Value.WarnWindowDays;
            var assurancesByVendor = snapshot.VendorAssurances
                .GroupBy(a => a.VendorId, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<VendorAssuranceView>)g
                        .Select(a =>
                        {
                            var status = VendorAssurance.Evaluate(a.Expires, a.WarnDays ?? warnWindowDays, today);
                            return new VendorAssuranceView(
                                standardTitles.TryGetValue(a.StandardId, out var title) ? title : a.StandardId,
                                status,
                                DescribeExpiry(a.Expires, status, today));
                        })
                        .ToList(),
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
                    assurancesByVendor.TryGetValue(v.Id, out var rows) ? rows : [],
                    scopesBySubject.TryGetValue(v.Id, out var scopes) ? scopes : []))
                .ToList();
        }
        catch (Exception ex) when (ComplianceEndpoints.IsStoreFailure(ex))
        {
            StoreUnreachable = true;
        }
    }

    // T6, with the certificate's own verb in place of "Overdue since": the two states that ask for action
    // are relative when near and absolute when far. Valid is absolute at EVERY distance, because with a
    // warn_days of 0 an assurance expiring tomorrow is Valid, and a relative form there would read
    // word-for-word like an Expiring one, leaving colour as the only difference (S2).
    private static string DescribeExpiry(DateOnly expires, AssuranceStatus status, DateOnly today)
    {
        var absolute = expires.ToString("MMM d", CultureInfo.InvariantCulture);
        var days = expires.DayNumber - today.DayNumber;

        return status switch
        {
            AssuranceStatus.Expired when -days <= 7 =>
                -days == 1 ? "expired 1 day ago" : $"expired {-days} days ago",
            AssuranceStatus.Expired => $"expired {absolute}",
            AssuranceStatus.Expiring when days <= 7 => days switch
            {
                0 => "expires today",
                1 => "expires tomorrow",
                _ => $"expires in {days} days",
            },
            AssuranceStatus.Expiring => $"expires {absolute}",
            _ => $"expires {absolute}",
        };
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
}
