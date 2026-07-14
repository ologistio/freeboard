using Freeboard.Compliance;
using Freeboard.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Compliance;

/// <summary>
/// Read-only server-rendered integration-connection list: each connection's provider, base URL, discovery
/// cadence, optional vendor, and a token-resolvable health flag. GET-only, so the GitOps read-only
/// middleware never blocks it. Reads connections through <see cref="IComplianceStore"/> in-process (like
/// the Vendors and Evidence Collector pages) inside one try/catch that sets <see cref="StoreUnreachable"/>,
/// so a store outage renders an in-page notice rather than a 500. The health flag is composed at read time
/// via <see cref="IIntegrationTokenResolver"/>; the token value is never read, rendered, or logged.
/// Connections are org-independent reference data, so the page does NOT narrow by accessible organisation.
/// </summary>
public sealed class IntegrationConnectionsModel(IComplianceStore store, IIntegrationTokenResolver tokens) : PageModel
{
    /// <summary>All connections with their composed token-resolvable flag, ordered by id.</summary>
    public IReadOnlyList<ConnectionView> Connections { get; private set; } = [];

    /// <summary>Set when the store is unreachable; rendered as an in-page notice.</summary>
    public bool StoreUnreachable { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            Connections = (await store.GetIntegrationConnectionsAsync(ct).ConfigureAwait(false))
                .Select(c => new ConnectionView(c, tokens.IsResolvable(c.Id)))
                .ToList();
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            StoreUnreachable = true;
        }
    }

    /// <summary>A persisted connection paired with its read-time token-resolvable health flag.</summary>
    public sealed record ConnectionView(IntegrationConnectionRow Connection, bool TokenResolvable);

    private static bool IsStoreFailure(Exception ex) =>
        ex is global::System.Data.Common.DbException or InvalidOperationException or TimeoutException;
}
