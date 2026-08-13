using System.Security.Claims;
using Freeboard.Persistence;
using Microsoft.AspNetCore.Http;

namespace Freeboard.Web;

/// <summary>
/// The organisation-selection building blocks: the <c>freeboard-org</c> cookie name, its set/clear
/// helpers, a no-I/O cookie reader, and the pure fail-closed resolution rule. "All Organisations" is
/// the absence of a cookie (a null resolved selection). The cookie is a view preference, not a
/// security token: it is re-validated server-side on every request against the accessible set, so a
/// forged or stale value cannot widen what the user sees.
/// </summary>
public static class OrgSelection
{
    /// <summary>The selected-organisation cookie. Not <c>__Host-</c>: a view preference, not a token.</summary>
    public const string CookieName = "freeboard-org";

    /// <summary>Sets the selection cookie. HttpOnly (only the server reads it), Secure, SameSite=Lax, Path=/.</summary>
    public static void Set(HttpResponse response, string organisationId)
        => response.Cookies.Append(CookieName, organisationId, Options());

    /// <summary>Clears the selection cookie (choosing "All Organisations"). Delete attributes match Set.</summary>
    public static void Clear(HttpResponse response)
        => response.Cookies.Delete(CookieName, Options());

    /// <summary>Reads the raw <c>freeboard-org</c> cookie value, or null when absent. No store access.</summary>
    public static string? ReadCandidate(HttpContext context)
        => context.Request.Cookies.TryGetValue(CookieName, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : null;

    /// <summary>
    /// The single fail-closed rule: return <paramref name="cookieCandidate"/> when it is in
    /// <paramref name="accessibleIds"/>, otherwise null ("All Organisations"). Pure - no I/O, no
    /// request state - so both the resolver and org-scoped pages share the exact same rule.
    /// </summary>
    public static string? Resolve(string? cookieCandidate, IReadOnlySet<string> accessibleIds)
        => cookieCandidate is not null && accessibleIds.Contains(cookieCandidate) ? cookieCandidate : null;

    /// <summary>A store-load failure the selector degrades on rather than faulting the layout render.</summary>
    internal static bool IsStoreFailure(Exception ex) => Compliance.ComplianceEndpoints.IsStoreFailure(ex);

    private static CookieOptions Options() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
    };
}

/// <summary>The resolved state the layout selector renders: the accessible tree and current selection.</summary>
public sealed record OrgSelectionState(
    IReadOnlyList<AssetNode> Organisations,
    IReadOnlySet<string> AccessibleIds,
    string? SelectedId);

/// <summary>
/// Request-scoped resolver serving ONLY the layout selector. It takes the asset list from the request's
/// one snapshot on <see cref="Authz.AuthzRequestCache"/> rather than reading the store itself, derives the
/// accessible ORGANISATION ids via <see cref="IAssetAccess"/>, reads the cookie candidate, and resolves it.
/// A store-load failure degrades to "All Organisations" with an empty list rather than throwing, so a store
/// outage never faults the layout. It exposes no store-failure flag: an empty store and an unreachable one
/// render the same "All Organisations" entry, and org-scoped pages detect an outage through their own
/// direct reads.
/// </summary>
public sealed class OrgSelectionResolver(
    IHttpContextAccessor httpContextAccessor, Authz.AuthzRequestCache cache, IAssetAccess access)
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);

    private OrgSelectionState? _state;

    public async Task<OrgSelectionState> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_state is not null)
        {
            return _state;
        }

        var http = httpContextAccessor.HttpContext;
        try
        {
            var assets = await cache.GetAssetsAsync(cancellationToken).ConfigureAwait(false);
            var user = http?.User ?? new ClaimsPrincipal();
            var accessible = await access.AccessibleAssetIdsAsync(user, assets, cancellationToken).ConfigureAwait(false);

            // The selector presents organisations only, so the tree and the set it resolves the cookie
            // against are both organisation-typed: a cookie naming a readable machine or vendor falls
            // back to "All Organisations".
            var organisations = assets.Where(a => a.IsOrganisation).ToList();
            var accessibleIds = organisations.Select(o => o.Id).Where(accessible.Contains)
                .ToHashSet(StringComparer.Ordinal);
            var candidate = http is null ? null : OrgSelection.ReadCandidate(http);
            var selectedId = OrgSelection.Resolve(candidate, accessibleIds);
            return _state = new OrgSelectionState(organisations, accessibleIds, selectedId);
        }
        catch (Exception ex) when (OrgSelection.IsStoreFailure(ex))
        {
            return _state = new OrgSelectionState([], Empty, null);
        }
    }
}
