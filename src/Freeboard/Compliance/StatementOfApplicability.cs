using Freeboard.Core.GitOps;
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
/// One requirement-level deviation on a node: the requirement's resolved disposition
/// (<c>In</c> or <c>Out</c>) and whether the node's own requirement-scope set it
/// (<see cref="SoaResolution.Explicit"/>) or it was inherited from an ancestor.
/// </summary>
public sealed record SoaRequirementResolution(
    string Requirement, string Disposition, SoaResolution Resolution);

/// <summary>
/// One organisation node in a Statement of Applicability: its resolved disposition for
/// the standard (<c>In</c>, <c>Out</c>, or null when <see cref="SoaResolution.Undetermined"/>)
/// and how that value was reached. <see cref="Requirements"/> lists only the requirement-level
/// deviations (requirements with an explicit or inherited requirement-scope) and is populated
/// only where the standard resolves <c>In</c>; an unlisted requirement follows the node's
/// standard disposition.
/// </summary>
public sealed record SoaNode(
    string Id,
    string Title,
    string Kind,
    string? Parent,
    string? Disposition,
    SoaResolution Resolution,
    IReadOnlyList<SoaRequirementResolution> Requirements);

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
        IReadOnlyList<RequirementRow> requirements,
        IReadOnlyList<RequirementScopeRow> requirementScopes,
        string standardId)
    {
        var byId = organisations.ToDictionary(o => o.Id, StringComparer.Ordinal);

        var explicitByOrg = scopes
            .Where(s => string.Equals(s.Standard, standardId, StringComparison.Ordinal))
            .GroupBy(s => s.Organisation, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Disposition, StringComparer.Ordinal);

        // Requirements of the requested standard, ordered by id, and the requirement-scopes that
        // bind them. A requirement-scope for a requirement of another standard is excluded here,
        // mirroring how the standard layer filters scopes by standardId.
        var standardRequirementIds = requirements
            .Where(r => string.Equals(r.Standard, standardId, StringComparison.Ordinal))
            .Select(r => r.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        var standardRequirementIdSet = standardRequirementIds.ToHashSet(StringComparer.Ordinal);

        // Nearest-ancestor lookup for the requirement layer: (organisation, requirement) -> disposition.
        var requirementScopeByOrg = requirementScopes
            .Where(rs => standardRequirementIdSet.Contains(rs.Requirement))
            .GroupBy(rs => rs.Organisation, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(rs => rs.Requirement, StringComparer.Ordinal)
                    .ToDictionary(rg => rg.Key, rg => rg.First().Disposition, StringComparer.Ordinal),
                StringComparer.Ordinal);

        var nodes = new List<SoaNode>(organisations.Count);
        foreach (var organisation in organisations)
        {
            var (disposition, resolution) = ResolveNode(organisation, byId, explicitByOrg);

            // Requirement-scopes apply only under a standard that resolves In at this node.
            var requirementResolutions = string.Equals(disposition, nameof(ScopeDisposition.In), StringComparison.Ordinal)
                ? ResolveRequirements(organisation, byId, standardRequirementIds, requirementScopeByOrg)
                : [];

            nodes.Add(new SoaNode(
                organisation.Id,
                organisation.Title,
                organisation.Kind,
                organisation.Parent,
                disposition,
                resolution,
                requirementResolutions));
        }

        return nodes.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
    }

    private static IReadOnlyList<SoaRequirementResolution> ResolveRequirements(
        OrganisationRow node,
        IReadOnlyDictionary<string, OrganisationRow> byId,
        IReadOnlyList<string> standardRequirementIds,
        IReadOnlyDictionary<string, Dictionary<string, string>> requirementScopeByOrg)
    {
        var results = new List<SoaRequirementResolution>();
        foreach (var requirementId in standardRequirementIds)
        {
            // Own requirement-scope wins (explicit); else the nearest ancestor's (inherited); else
            // the requirement is not a deviation and follows the node's standard disposition (In).
            if (TryGetRequirementDisposition(node.Id, requirementId, requirementScopeByOrg, out var own))
            {
                results.Add(new SoaRequirementResolution(requirementId, own, SoaResolution.Explicit));
                continue;
            }

            var visited = new HashSet<string>(StringComparer.Ordinal) { node.Id };
            var current = node.Parent;
            while (current is not null && byId.TryGetValue(current, out var parent) && visited.Add(current))
            {
                if (TryGetRequirementDisposition(parent.Id, requirementId, requirementScopeByOrg, out var inherited))
                {
                    results.Add(new SoaRequirementResolution(requirementId, inherited, SoaResolution.Inherited));
                    break;
                }

                current = parent.Parent;
            }
        }

        return results;
    }

    private static bool TryGetRequirementDisposition(
        string organisationId,
        string requirementId,
        IReadOnlyDictionary<string, Dictionary<string, string>> requirementScopeByOrg,
        out string disposition)
    {
        if (requirementScopeByOrg.TryGetValue(organisationId, out var byRequirement)
            && byRequirement.TryGetValue(requirementId, out var found))
        {
            disposition = found;
            return true;
        }

        disposition = string.Empty;
        return false;
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
