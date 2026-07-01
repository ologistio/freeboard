using Freeboard.Persistence;

namespace Freeboard.Compliance;

/// <summary>How a node's disposition for a standard was determined.</summary>
public enum SoaResolution
{
    /// <summary>The node has its own Scope for the standard.</summary>
    Explicit,

    /// <summary>The value came from the nearest ancestor that has a Scope.</summary>
    Inherited,

    /// <summary>No node on the path to the root has a Scope for the standard.</summary>
    Undetermined,
}

/// <summary>Wire names for <see cref="SoaResolution"/>: lowercase for a resolved value, capital-U Undetermined.</summary>
public static class SoaResolutionNames
{
    public static string ToWireValue(this SoaResolution resolution) => resolution switch
    {
        SoaResolution.Explicit => "explicit",
        SoaResolution.Inherited => "inherited",
        _ => "Undetermined",
    };
}

/// <summary>
/// One organisation node in a Statement of Applicability: its resolved disposition for
/// the standard (<c>In</c>, <c>Out</c>, or null when <see cref="SoaResolution.Undetermined"/>)
/// and how that value was reached.
/// </summary>
public sealed record SoaNode(
    string Id,
    string Title,
    string Kind,
    string? Parent,
    string? Disposition,
    SoaResolution Resolution);

/// <summary>
/// Resolves a Statement of Applicability for a standard: a projection over the
/// organisation tree that assigns each node a disposition by nearest-ancestor
/// inheritance. Pure (no I/O), so the inheritance rule is unit testable. Undetermined is
/// distinct from an explicit or inherited <c>Out</c>.
/// </summary>
public static class StatementOfApplicability
{
    public static IReadOnlyList<SoaNode> Resolve(
        IReadOnlyList<OrganisationRow> organisations,
        IReadOnlyList<ScopeRow> scopes,
        string standardId)
    {
        var byId = organisations.ToDictionary(o => o.Id, StringComparer.Ordinal);

        var explicitByOrg = scopes
            .Where(s => string.Equals(s.Standard, standardId, StringComparison.Ordinal))
            .GroupBy(s => s.Organisation, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Disposition, StringComparer.Ordinal);

        var nodes = new List<SoaNode>(organisations.Count);
        foreach (var organisation in organisations)
        {
            var (disposition, resolution) = ResolveNode(organisation, byId, explicitByOrg);
            nodes.Add(new SoaNode(
                organisation.Id,
                organisation.Title,
                organisation.Kind,
                organisation.Parent,
                disposition,
                resolution));
        }

        return nodes.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
    }

    private static (string? Disposition, SoaResolution Resolution) ResolveNode(
        OrganisationRow node,
        IReadOnlyDictionary<string, OrganisationRow> byId,
        IReadOnlyDictionary<string, string> explicitByOrg)
    {
        // Explicit disposition on the node itself wins.
        if (explicitByOrg.TryGetValue(node.Id, out var own))
        {
            return (own, SoaResolution.Explicit);
        }

        // Otherwise walk ancestors to the first that has an explicit disposition.
        var visited = new HashSet<string>(StringComparer.Ordinal) { node.Id };
        var current = node.Parent;
        while (current is not null && byId.TryGetValue(current, out var parent) && visited.Add(current))
        {
            if (explicitByOrg.TryGetValue(parent.Id, out var inherited))
            {
                return (inherited, SoaResolution.Inherited);
            }

            current = parent.Parent;
        }

        return (null, SoaResolution.Undetermined);
    }
}
