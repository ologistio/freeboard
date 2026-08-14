using System.Runtime.CompilerServices;
using Freeboard.Compliance;
using Freeboard.Core.Authz;
using Freeboard.Persistence;

namespace Freeboard.Authz;

/// <summary>
/// The ONE request-scoped cache shared by the authorizer and <c>AuthzAssetAccess</c>, holding nothing
/// across requests (registered SCOPED). A principal's facts are produced at most once per request, and so
/// is each shape of compliance snapshot; a principal's accessible asset set is produced at most once per
/// asset list, because a request may hold more than one. Implements <see cref="IAuthzFactProvider"/> so it
/// is the single fact loader. It also builds the organisation-anchored <see cref="AuthzResource"/> every
/// organisation gate is constructed from, because the asset list that anchoring needs is already
/// memoized here.
///
/// The accessible set is memoized per principal AND per asset list, so two reads in one request each
/// narrow with their own owner edges rather than the first one's. That keying is what lets several
/// snapshots coexist honestly in one request.
///
/// <see cref="GetSnapshotAsync"/> keeps every snapshot the request has taken and serves a later request
/// from the first taken snapshot whose sets COVER it. Reuse is sound because a wider snapshot is one
/// transaction containing every list the narrower request asked for, so the reusing decision takes its
/// rows and its asset list from that one transaction. Two snapshots are never merged into a synthetic
/// wider one: the merged lists would come from two transactions, which is the straddle this cache exists
/// to prevent. A FAULTED read keeps nothing, so a gate arriving after it still reads and answers.
///
/// <see cref="GetAssetsAsync"/> names the assets and no payload set, so every gate that reaches its
/// assets through it - every route- or body-anchored organisation gate, every compliance write selector,
/// and both role-assignment guards - reads the <c>assets</c> table alone, and a schema missing a payload
/// table degrades the surface that reads it rather than closing every gated write. The one gate that does
/// not reach its assets this way is the scope write, whose organisation is only knowable from the stored
/// row: it brings its own snapshot of the assets and the scopes.
/// </summary>
public sealed class AuthzRequestCache(IAuthzStore store, IComplianceStore compliance) : IAuthzFactProvider
{
    private readonly Dictionary<string, AuthzPrincipalFacts> _facts = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Principal, AssetListKey Assets), IReadOnlySet<string>> _accessibleAssets = [];
    private readonly List<ComplianceSnapshot> _snapshots = [];
    private readonly Dictionary<AssetListKey, IReadOnlyDictionary<string, AssetNode>> _assetsById = [];

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
    /// The lists <paramref name="sets"/> names, in one snapshot. A snapshot the request has already taken
    /// whose sets cover <paramref name="sets"/> is served instead of a new read, so a page render and the
    /// shell around it share one read and one accessible set. Which snapshot is reused depends on the
    /// order the surfaces of a request run in, and that is a performance property only: each snapshot
    /// carries its own asset list, so every decision is internally consistent whichever order runs.
    ///
    /// Memoizing assumes the sequential access a request pipeline gives it. Two calls awaited
    /// concurrently could each read before either is kept, and would then be served different snapshots;
    /// nothing in the app does that today, and a caller that starts parallelising reads through this
    /// cache has to give it a single-flight guard first.
    /// </summary>
    public async ValueTask<ComplianceSnapshot> GetSnapshotAsync(
        ComplianceReadSet sets, CancellationToken cancellationToken = default)
    {
        foreach (var taken in _snapshots)
        {
            if ((taken.Sets & sets) == sets)
            {
                return taken;
            }
        }

        var snapshot = await compliance.GetSnapshotAsync(sets, cancellationToken).ConfigureAwait(false);
        _snapshots.Add(snapshot);
        return snapshot;
    }

    /// <summary>
    /// The request's shared asset list: the <c>assets</c> table alone, or a wider snapshot's assets when
    /// the request has already taken one. The first list served is served for the rest of the request, so
    /// two consumers of this read can never narrow with different lists.
    /// </summary>
    public async ValueTask<IReadOnlyList<AssetNode>> GetAssetsAsync(
        CancellationToken cancellationToken = default)
        => (await GetSnapshotAsync(ComplianceReadSet.Assets, cancellationToken).ConfigureAwait(false)).Assets;

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
        => OrganisationResource(
            type, id, organisationId, await GetAssetsAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// The same resource, anchored on the assets of a snapshot the caller already holds. A gate whose
    /// organisation was derived from a stored row takes this form: resolving the ancestry from a
    /// separately taken asset read would authorize the write against a chain the row's own snapshot never
    /// had.
    /// </summary>
    public AuthzResource OrganisationResource(
        string type, string? id, string organisationId, ComplianceSnapshot snapshot)
        => OrganisationResource(type, id, organisationId, snapshot.Assets);

    private AuthzResource OrganisationResource(
        string type, string? id, string organisationId, IReadOnlyList<AssetNode> assets)
    {
        var byId = AssetsById(assets);

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

    // Memoized per asset list: one request can gate several times (a reparenting write gates the org and
    // both parents), each gate walks the same map, and a request holding more than one snapshot must not
    // index one list's gate against another list's map.
    private IReadOnlyDictionary<string, AssetNode> AssetsById(IReadOnlyList<AssetNode> assets)
    {
        var key = new AssetListKey(assets);
        if (_assetsById.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var byId = assets.ToDictionary(a => a.Id, StringComparer.Ordinal);
        _assetsById[key] = byId;
        return byId;
    }
}
