using Freeboard.Persistence;

namespace Freeboard.Compliance;

/// <summary>
/// The single shared inclusive-ancestry build. A pure function over the organisation list that
/// returns <c>[start, parent(start), ..., root]</c> with a visited-set cycle guard. RBAC correctness
/// depends on the cycle guard, so the authorizer (which needs the WHOLE chain to match any granting
/// ancestor) and the Statement-of-Applicability projection (which consumes the same chain but stops at
/// the first node with a disposition) build the chain here and cannot diverge.
/// </summary>
public static class OrgAncestry
{
    public static IReadOnlyList<string> InclusiveAncestors(
        string startId, IReadOnlyDictionary<string, OrganisationRow> byId)
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
}
