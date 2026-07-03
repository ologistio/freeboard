using Freeboard.Persistence;

namespace Freeboard.Compliance;

/// <summary>
/// Computes the in-scope organisation id set for a selection, always bounded by the accessible set.
/// Pure (no I/O), so the subtree rule is unit testable. A null selection is "All Organisations": the
/// accessible set. A selected id is that node plus all descendants, intersected with the accessible
/// set, so an out-of-access organisation never renders even under "All".
/// </summary>
public static class OrgScope
{
    public static IReadOnlySet<string> InScopeIds(
        IReadOnlyList<OrganisationRow> organisations,
        IReadOnlySet<string> accessibleIds,
        string? selectedId)
    {
        // All Organisations: the accessible set, not every persisted org.
        if (selectedId is null)
        {
            return new HashSet<string>(accessibleIds, StringComparer.Ordinal);
        }

        // A selection outside the accessible set yields nothing (fail closed).
        if (!accessibleIds.Contains(selectedId))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var childrenByParent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var organisation in organisations)
        {
            if (organisation.Parent is { } parent)
            {
                if (!childrenByParent.TryGetValue(parent, out var children))
                {
                    children = [];
                    childrenByParent[parent] = children;
                }

                children.Add(organisation.Id);
            }
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
        return inScope;
    }
}
