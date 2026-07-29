using Freeboard.Persistence;

namespace Freeboard.Compliance;

/// <summary>
/// The single shared inclusive-ancestry build. A pure function over the asset list that returns
/// <c>[start, parent(start), ..., root]</c> with a visited-set cycle guard. RBAC correctness depends on
/// the cycle guard, so the authorizer (which needs the WHOLE chain to match any granting ancestor), the
/// Statement-of-Applicability projection (which consumes the same chain but stops at the first node with
/// a disposition), and the read-access closure build the chain here and cannot diverge.
/// </summary>
public static class AssetAncestry
{
    public static IReadOnlyList<string> InclusiveAncestors(
        string startId, IReadOnlyDictionary<string, AssetNode> byId)
    {
        var chain = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = (string?)startId;
        while (current is not null && visited.Add(current))
        {
            chain.Add(current);
            current = byId.TryGetValue(current, out var node) ? node.Parent : null;
        }

        return chain;
    }

    /// <summary>
    /// The same chain, memoized for the life of ONE pass through a cache the caller creates and
    /// discards with the asset snapshot it was built from. The cache is a parameter rather than a field
    /// so it can never be shared between requests or outlive the <c>parent</c> edges it encodes.
    ///
    /// Every entry the walk passes is a chain in its own right, so one walk stores each as a slice of
    /// the one array and a later walk that meets a stored entry appends it instead of re-walking it.
    /// A chain that ends by meeting itself is stored for NOBODY, not even its own start: the cycle guard
    /// cut a node that a longer chain entering the same cycle further back would still visit, so such a
    /// chain is a suffix of nothing. Because only cycle-free chains are stored, an entry can never
    /// contain a node the walk appending it just passed, which is what keeps the appended result
    /// identical to the plain walk.
    /// </summary>
    public static IReadOnlyList<string> InclusiveAncestors(
        string startId,
        IReadOnlyDictionary<string, AssetNode> byId,
        Dictionary<string, IReadOnlyList<string>> cache)
    {
        if (cache.TryGetValue(startId, out var cached))
        {
            return cached;
        }

        var walked = new List<string>();
        var onPath = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<string> tail = [];
        var current = (string?)startId;
        while (current is not null)
        {
            if (cache.TryGetValue(current, out var suffix))
            {
                tail = suffix;
                break;
            }

            if (!onPath.Add(current))
            {
                return InclusiveAncestors(startId, byId);
            }

            walked.Add(current);
            current = byId.TryGetValue(current, out var node) ? node.Parent : null;
        }

        var chain = walked.Concat(tail).ToArray();
        for (var i = 0; i < walked.Count; i++)
        {
            cache[walked[i]] = new ArraySegment<string>(chain, i, chain.Length - i);
        }

        return cache[startId];
    }
}
