using Freeboard.Compliance;
using Freeboard.Core.Authz;
using Freeboard.Persistence;

namespace Freeboard.Authz;

/// <summary>
/// The ONE request-scoped cache shared by the authorizer and <c>AuthzAssetAccess</c>: a principal's
/// facts, the asset tree, and a principal's accessible asset set are each produced at most once per
/// request and never leak across requests (registered SCOPED).
/// Implements <see cref="IAuthzFactProvider"/> so it is the single fact loader. It also builds
/// the organisation-anchored <see cref="AuthzResource"/> every organisation gate is constructed from,
/// because the asset list that anchoring needs is already memoized here.
///
/// The request's asset read IS the vendor assurance snapshot: <see cref="GetAssetsAsync"/> serves its
/// assets from <see cref="GetVendorAssuranceInputsAsync"/>. That is what makes the narrowing honest. The
/// accessible set is memoized per principal per request and resolved from the FIRST asset list that
/// reaches <c>IAssetAccess</c>, so unless every consumer reads ONE snapshot, a surface ends up narrowing
/// its assurance rows with another surface's owner edges. The two Statement of Applicability pages are the
/// exception: they keep their own snapshot read
/// (<c>GetStatementOfApplicabilityDrilldownInputsAsync</c>), so on their own pages they still seed the
/// accessible set from their own asset list.
/// </summary>
public sealed class AuthzRequestCache(IAuthzStore store, IComplianceStore compliance) : IAuthzFactProvider
{
    private readonly Dictionary<string, AuthzPrincipalFacts> _facts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlySet<string>> _accessibleAssets = new(StringComparer.Ordinal);
    private VendorAssuranceInputs? _assuranceInputs;
    private IReadOnlyDictionary<string, AssetNode>? _assetsById;

    public async ValueTask<AuthzPrincipalFacts> LoadFactsAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        if (_facts.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        var facts = await store.LoadPrincipalFactsAsync(userId, cancellationToken).ConfigureAwait(false);
        _facts[userId] = facts;
        return facts;
    }

    /// <summary>
    /// The request's one asset-and-assurance snapshot, read at most once. Taken once per request rather
    /// than once per component: two components each taking their own would narrow with each other's owner
    /// edges, since the accessible set below serves every later caller the first one's answer.
    /// </summary>
    public async ValueTask<VendorAssuranceInputs> GetVendorAssuranceInputsAsync(
        CancellationToken cancellationToken = default)
        => _assuranceInputs ??= await compliance.GetVendorAssuranceInputsAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<AssetNode>> GetAssetsAsync(
        CancellationToken cancellationToken = default)
        => (await GetVendorAssuranceInputsAsync(cancellationToken).ConfigureAwait(false)).Assets;

    /// <summary>
    /// The principal's accessible ASSET set for this request, resolved at most once per principal by
    /// <paramref name="resolve"/>. It is memoized here, beside the facts it is derived from, because
    /// resolving it is a full pass over the asset tree with a per-node ancestry walk and every page
    /// render asks for it at least twice - once for the layout's organisation selector, once for the
    /// page itself. The rollout rules stay in the caller so this type holds no policy.
    /// </summary>
    public async ValueTask<IReadOnlySet<string>> AccessibleAssetIdsAsync(
        string principalKey, Func<ValueTask<IReadOnlySet<string>>> resolve)
    {
        if (_accessibleAssets.TryGetValue(principalKey, out var cached))
        {
            return cached;
        }

        var accessible = await resolve().ConfigureAwait(false);
        _accessibleAssets[principalKey] = accessible;
        return accessible;
    }

    /// <summary>
    /// Builds a resource for an organisation gate, with its ancestry pinned to the ORGANISATION-BOUNDED
    /// chain: the inclusive <c>parent</c> chain cut immediately after its first entry naming a
    /// non-organisation asset (that entry is kept). Org RBAC permits on any grant in the chain, so
    /// without the cut a grant beyond a machine link would authorize a write on the far side of it.
    ///
    /// Pinned unconditionally rather than per id class, because the cut chain and the plain walk agree
    /// wherever no cut applies: an ordinary all-organisation chain, a dangling <c>parent</c>, a
    /// <c>parent</c> cycle, and an id naming no asset all resolve identically either way.
    /// </summary>
    public async ValueTask<AuthzResource> OrganisationResourceAsync(
        string type, string? id, string organisationId, CancellationToken cancellationToken = default)
    {
        var byId = await AssetsByIdAsync(cancellationToken).ConfigureAwait(false);

        var chain = new List<string>();
        foreach (var entry in AssetAncestry.InclusiveAncestors(organisationId, byId))
        {
            chain.Add(entry);
            // An entry naming no asset is not a cut point: the walk already stops there for want of a
            // parent, so keeping it matches what an organisation-only map would have produced.
            if (byId.TryGetValue(entry, out var node) && !node.IsOrganisation)
            {
                break;
            }
        }

        return new AuthzResource(type, id, organisationId, chain);
    }

    // Memoized with the list it indexes: one request can gate several times (a reparenting write gates
    // the org and both parents), and each gate walks the same map.
    private async ValueTask<IReadOnlyDictionary<string, AssetNode>> AssetsByIdAsync(CancellationToken ct)
        => _assetsById ??= (await GetAssetsAsync(ct).ConfigureAwait(false))
            .ToDictionary(a => a.Id, StringComparer.Ordinal);
}
