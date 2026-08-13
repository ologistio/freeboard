using System.Runtime.CompilerServices;
using Freeboard.Compliance;
using Freeboard.Core.Authz;
using Freeboard.Persistence;

namespace Freeboard.Authz;

/// <summary>
/// The ONE request-scoped cache shared by the authorizer and <c>AuthzAssetAccess</c>, holding nothing
/// across requests (registered SCOPED). A principal's facts and the shared asset list are each produced
/// at most once per request; a principal's accessible asset set is produced at most once per asset list,
/// because a request may hold more than one - the shared read, and an assurance snapshot beside it.
/// Implements <see cref="IAuthzFactProvider"/> so it is the single fact loader. It also builds
/// the organisation-anchored <see cref="AuthzResource"/> every organisation gate is constructed from,
/// because the asset list that anchoring needs is already memoized here.
///
/// The accessible set is memoized per principal AND per asset list, so two reads in one request each
/// narrow with their own owner edges rather than the first one's. That keying is what lets the shared
/// asset read carry nothing but the <c>assets</c> table: every organisation gate, every compliance write
/// selector and both role-assignment guards reach their assets through <see cref="GetAssetsAsync"/>, and
/// none of them should depend on a table only the vendor register needs.
///
/// <see cref="GetAssetsAsync"/> may be SERVED from an assurance snapshot the request has already taken,
/// but never takes one. So a request that renders the register pays for one read, and a request that only
/// gates pays for a read of <c>assets</c> alone - and a schema missing the assurance table degrades the
/// register rather than closing every gated write.
/// </summary>
public sealed class AuthzRequestCache(IAuthzStore store, IComplianceStore compliance) : IAuthzFactProvider
{
    private readonly Dictionary<string, AuthzPrincipalFacts> _facts = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Principal, AssetListKey Assets), IReadOnlySet<string>> _accessibleAssets = [];
    private VendorAssuranceInputs? _assuranceInputs;
    private IReadOnlyList<AssetNode>? _assets;
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
    /// The assets and assurances a vendor-assurance surface narrows one by the other, in one snapshot,
    /// read at most once per request. Its assets also stand in for the shared read when a later caller
    /// asks for one, which is why a request that renders the register pays for a single read.
    /// </summary>
    public async ValueTask<VendorAssuranceInputs> GetVendorAssuranceInputsAsync(
        CancellationToken cancellationToken = default)
        => _assuranceInputs ??= await compliance.GetVendorAssuranceInputsAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The request's shared asset list, pinned on first use: the first list served is served for the rest
    /// of the request, so two consumers of this read can never narrow with different lists and the ancestry
    /// map cannot disagree with what a later caller gets.
    ///
    /// An assurance snapshot the request has ALREADY taken supplies the list; this never takes one. A
    /// faulted assurance read memoizes nothing, so a gate arriving after it still reads the assets alone.
    ///
    /// Pinning assumes the sequential access a request pipeline gives it. Two calls awaited concurrently
    /// could each read before either memoizes, and would then be served different lists; nothing in the app
    /// does that today, and a caller that starts parallelising reads through this cache has to give it a
    /// single-flight guard first.
    /// </summary>
    public async ValueTask<IReadOnlyList<AssetNode>> GetAssetsAsync(
        CancellationToken cancellationToken = default)
        => _assets ??= _assuranceInputs?.Assets
            ?? await compliance.GetAssetsAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The principal's accessible ASSET set over <paramref name="assets"/>, resolved at most once per
    /// principal per list by <paramref name="resolve"/>. It is memoized here, beside the facts it is
    /// derived from, because resolving it is a full pass over the asset tree with a per-node ancestry
    /// walk and every page render asks for it at least twice - once for the layout's organisation
    /// selector, once for the page itself. The rollout rules stay in the caller so this type holds no
    /// policy.
    ///
    /// Keyed by the LIST as well as the principal, because a set resolved over one list is not an answer
    /// about another: two reads see different owner edges when a sync commits between them. Lists compare
    /// by reference, so a caller must not mutate one it has handed over.
    /// </summary>
    public async ValueTask<IReadOnlySet<string>> AccessibleAssetIdsAsync(
        string principalKey, IReadOnlyList<AssetNode> assets, Func<ValueTask<IReadOnlySet<string>>> resolve)
    {
        var key = (principalKey, new AssetListKey(assets));
        if (_accessibleAssets.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var accessible = await resolve().ConfigureAwait(false);
        _accessibleAssets[key] = accessible;
        return accessible;
    }

    /// <summary>
    /// Identity of an asset list, by reference. Wrapped rather than keyed on the list itself: the default
    /// comparer is reference equality only for as long as no list implementation overrides
    /// <see cref="object.Equals(object)"/>, which is not this seam's to guarantee.
    /// </summary>
    private readonly struct AssetListKey(IReadOnlyList<AssetNode> assets) : IEquatable<AssetListKey>
    {
        private readonly IReadOnlyList<AssetNode> _assets = assets;

        public bool Equals(AssetListKey other) => ReferenceEquals(_assets, other._assets);

        public override bool Equals(object? obj) => obj is AssetListKey other && Equals(other);

        public override int GetHashCode() => RuntimeHelpers.GetHashCode(_assets);
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
