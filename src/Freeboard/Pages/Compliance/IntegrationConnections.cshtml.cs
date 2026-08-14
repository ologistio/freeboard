using Freeboard.Authz;
using Freeboard.Compliance;
using Freeboard.Persistence;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Compliance;

/// <summary>
/// Read-only server-rendered integration-connection list: each connection's provider, base URL, discovery
/// cadence, optional vendor, and a token-resolvable health flag. GET-only, so the GitOps read-only
/// middleware never blocks it. Takes the connections and the assets - the two lists its narrowing rests
/// on - from ONE snapshot on <see cref="AuthzRequestCache"/>, in-process (like the Vendors and Evidence
/// Collector pages) inside one try/catch that sets <see cref="StoreUnreachable"/>, so a store outage
/// renders an in-page notice rather than a 500. The health flag is composed at read time
/// via <see cref="IIntegrationTokenResolver"/>; the token value is never returned, rendered, or logged
/// (the resolver reads it only to test presence). Connections are org-independent reference data, so the
/// ROW set is not narrowed. The vendor IS narrowed - it names a vendor asset, which the owner edge
/// governs - and a connection whose vendor is outside the caller's accessible asset set renders exactly
/// as one with no vendor.
/// </summary>
public sealed class IntegrationConnectionsModel(
    AuthzRequestCache cache, IIntegrationTokenResolver tokens, IAssetAccess assetAccess) : PageModel
{
    /// <summary>All connections with their composed token-resolvable flag, failing (unresolvable) first (L1).</summary>
    public IReadOnlyList<ConnectionView> Connections { get; private set; } = [];

    /// <summary>Set when the store is unreachable; rendered as an in-page notice.</summary>
    public bool StoreUnreachable { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            var snapshot = await cache.GetSnapshotAsync(
                ComplianceReadSet.Assets | ComplianceReadSet.IntegrationConnections, ct).ConfigureAwait(false);
            var accessible = await assetAccess
                .AccessibleAssetIdsAsync(User, snapshot.Assets, ct).ConfigureAwait(false);

            // L1: exceptions first - unresolvable-token connections sort above resolvable ones, then by id.
            Connections = snapshot.IntegrationConnections
                .Select(c => c.Vendor is not null && !accessible.Contains(c.Vendor) ? c with { Vendor = null } : c)
                .Select(c => new ConnectionView(c, tokens.IsResolvable(c.Id)))
                .OrderBy(v => v.TokenResolvable)
                .ThenBy(v => v.Connection.Id, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ComplianceEndpoints.IsStoreFailure(ex))
        {
            StoreUnreachable = true;
        }
    }

    /// <summary>A persisted connection paired with its read-time token-resolvable health flag.</summary>
    public sealed record ConnectionView(IntegrationConnectionRow Connection, bool TokenResolvable);
}
