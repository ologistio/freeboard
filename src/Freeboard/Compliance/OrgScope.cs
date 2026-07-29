using Freeboard.Persistence;

namespace Freeboard.Compliance;

/// <summary>
/// Computes the in-scope organisation id set for a selection, always bounded by the accessible set.
/// Pure (no I/O), so the subtree rule is unit testable. A null selection is "All Organisations": the
/// accessible ORGANISATIONS. A selected id is that node plus all organisation descendants, intersected
/// with the accessible set, so an out-of-access organisation never renders even under "All".
///
/// Both branches are organisation-only. The accessible set handed in is an ASSET set, so the null
/// branch intersects with the organisation ids rather than returning it whole, and the subtree walk
/// descends organisations only so it cannot pass through a machine into whatever hangs below it.
/// </summary>
public static class OrgScope
{
    public static IReadOnlySet<string> InScopeIds(
        IReadOnlyList<AssetNode> assets,
        IReadOnlySet<string> accessibleIds,
        string? selectedId)
    {
        var organisations = assets.Where(a => a.IsOrganisation).ToList();
        var organisationIds = organisations.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);

        // All Organisations: the accessible organisations, not every persisted one.
        if (selectedId is null)
        {
            organisationIds.IntersectWith(accessibleIds);
            return organisationIds;
        }

        // A selection outside the accessible set yields nothing (fail closed).
        if (!accessibleIds.Contains(selectedId))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var childrenByParent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var organisation in organisations.Where(o => o.Parent is not null))
        {
            var parent = organisation.Parent!;
            if (!childrenByParent.TryGetValue(parent, out var children))
            {
                children = [];
                childrenByParent[parent] = children;
            }

            children.Add(organisation.Id);
        }

        // Walk the selected node and its descendants; the visited set doubles as the result and
        // terminates a cyclic parent link.
        var inScope = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(selectedId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!inScope.Add(id))
            {
                continue;
            }

            if (childrenByParent.TryGetValue(id, out var children))
            {
                foreach (var child in children)
                {
                    stack.Push(child);
                }
            }
        }

        inScope.IntersectWith(accessibleIds);
        // The walk only ever descends organisations, so this bounds the SELECTED id itself: a cookie
        // naming a readable machine must not put that machine in an organisation scope.
        inScope.IntersectWith(organisationIds);
        return inScope;
    }
}
