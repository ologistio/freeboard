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

    /// <summary>No Scope on the path to the root, so the node takes the default disposition In.</summary>
    Default,
}

/// <summary>Wire names for <see cref="SoaResolution"/>: lowercase.</summary>
public static class SoaResolutionNames
{
    public static string ToWireValue(this SoaResolution resolution) => resolution switch
    {
        SoaResolution.Explicit => "explicit",
        SoaResolution.Inherited => "inherited",
        _ => "default",
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
/// One organisation node in a Statement of Applicability: its resolved standard disposition
/// (always <c>In</c> or <c>Out</c>, never null) and how that value was reached
/// (<see cref="SoaResolution.Default"/> means in-scope with no authored Scope on the path).
/// <see cref="Requirements"/> lists only the requirement-level deviations (requirements with
/// an explicit or inherited requirement-scope) and is populated only where the standard
/// resolves <c>In</c>; an unlisted requirement follows the node's standard disposition.
/// </summary>
public sealed record SoaNode(
    string Id,
    string Title,
    string Kind,
    string? Parent,
    string Disposition,
    SoaResolution Resolution,
    IReadOnlyList<SoaRequirementResolution> Requirements);

/// <summary>The kind of configured check attached to a control.</summary>
public enum SoaCheckKind
{
    /// <summary>An evidence-collector attached to the control.</summary>
    Collector,

    /// <summary>An attestation-template attached to the control.</summary>
    Attestation,
}

/// <summary>Wire names for <see cref="SoaCheckKind"/>: lowercase.</summary>
public static class SoaCheckKindNames
{
    public static string ToWireValue(this SoaCheckKind kind) => kind switch
    {
        SoaCheckKind.Collector => "collector",
        _ => "attestation",
    };
}

/// <summary>
/// One configured check under a control: an evidence-collector (<see cref="SoaCheckKind.Collector"/>)
/// or an attestation-template (<see cref="SoaCheckKind.Attestation"/>). Metadata only. Collector rows
/// carry <see cref="Type"/>, <see cref="Frequency"/>, and an optional <see cref="Vendor"/> display (the
/// vendor's title, or its id when unknown); attestation rows carry <see cref="Type"/> with the other two
/// null. Quiz answers are never surfaced.
/// </summary>
public sealed record SoaCheckNode(
    string Id, string Title, SoaCheckKind Kind, string Type, string? Frequency, string? Vendor);

/// <summary>
/// One control under a requirement, attached by <c>maps_to</c>. <see cref="Evaluation"/> is the
/// control roll-up rule shown as metadata (null when unset). Checks are ordered by <c>(Kind, Id)</c>.
/// </summary>
public sealed record SoaControlNode(
    string Id, string Title, string? Evaluation, IReadOnlyList<SoaCheckNode> Checks);

/// <summary>
/// One requirement under an in-scope organisation node: its resolved <see cref="Disposition"/>
/// (<c>In</c> or <c>Out</c>) and provenance (explicit/inherited/default), with the controls that map
/// to it. Unlike the flat <see cref="SoaNode"/>, this is the full requirement set of the standard, not
/// only deviations. An excluded (<c>Out</c>) requirement is a leaf: <see cref="Controls"/> is empty, so
/// only an <c>In</c> requirement carries controls (and their checks).
/// </summary>
public sealed record SoaRequirementNode(
    string Id, string Title, string Disposition, SoaResolution Resolution, IReadOnlyList<SoaControlNode> Controls);

/// <summary>
/// One organisation node in the Statement of Applicability drill-down: the org scalar fields projected
/// from the resolved <see cref="SoaNode"/> plus its <see cref="Requirements"/>. A node whose standard
/// resolves <c>Out</c> carries no requirement children.
/// </summary>
public sealed record SoaDrilldownNode(
    string Id,
    string Title,
    string Kind,
    string? Parent,
    string Disposition,
    SoaResolution Resolution,
    IReadOnlyList<SoaRequirementNode> Requirements);

/// <summary>
/// Resolves a Statement of Applicability for a standard: a projection over the
/// organisation tree that assigns each node a disposition by nearest-ancestor
/// inheritance. Pure (no I/O), so the inheritance rule is unit testable. A node with no
/// Scope on its path defaults to <c>In</c>, so the standard disposition is always
/// <c>In</c> or <c>Out</c>.
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
            // Build the node's inclusive ancestry ONCE via the shared helper, then consume it for both
            // the standard-level and requirement-level nearest-ancestor lookups.
            var ancestry = OrgAncestry.InclusiveAncestors(organisation.Id, byId);
            var (disposition, resolution) = ResolveNode(organisation.Id, ancestry, explicitByOrg);

            // Requirement-scopes apply only under a standard that resolves In at this node.
            var requirementResolutions = string.Equals(disposition, nameof(ScopeDisposition.In), StringComparison.Ordinal)
                ? ResolveRequirements(organisation.Id, ancestry, standardRequirementIds, requirementScopeByOrg)
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

    /// <summary>
    /// Projects the four-level drill-down (organisation -> requirement -> control -> check) for a
    /// standard. Reuses <see cref="Resolve"/> for each node's org-level disposition and provenance, then
    /// enumerates every requirement of the standard per node (not only deviations), each tagged with its
    /// resolved disposition (<c>In</c>/<c>Out</c>) and provenance. An <c>In</c> requirement carries its
    /// controls (by <c>maps_to</c>) and checks (collectors and templates by their <c>Control</c>, tagged
    /// by kind); an <c>Out</c> requirement is a leaf and carries no controls. The
    /// requirement -> control -> check catalogue is org-independent, so it is built once and shared. A
    /// collector's vendor is shown by title (falling back to its id when unknown). Pure (no I/O).
    /// </summary>
    public static IReadOnlyList<SoaDrilldownNode> ResolveDrilldown(
        IReadOnlyList<OrganisationRow> organisations,
        IReadOnlyList<ScopeRow> scopes,
        IReadOnlyList<RequirementRow> requirements,
        IReadOnlyList<RequirementScopeRow> requirementScopes,
        IReadOnlyList<ControlRow> controls,
        IReadOnlyList<EvidenceCollectorRow> collectors,
        IReadOnlyList<AttestationTemplateRow> templates,
        IReadOnlyList<VendorRow> vendors,
        string standardId)
    {
        // Org-level disposition/provenance: reuse the flat resolver so the inheritance rule is not
        // duplicated. The full in-scope requirement enumeration below is new: Resolve yields only
        // deviations and never a requirement-level Default.
        var resolved = Resolve(organisations, scopes, requirements, requirementScopes, standardId);

        var byId = organisations.ToDictionary(o => o.Id, StringComparer.Ordinal);

        var standardRequirements = requirements
            .Where(r => string.Equals(r.Standard, standardId, StringComparison.Ordinal))
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
        var standardRequirementIds = standardRequirements.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

        var requirementScopeByOrg = requirementScopes
            .Where(rs => standardRequirementIds.Contains(rs.Requirement))
            .GroupBy(rs => rs.Organisation, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(rs => rs.Requirement, StringComparer.Ordinal)
                    .ToDictionary(rg => rg.Key, rg => rg.First().Disposition, StringComparer.Ordinal),
                StringComparer.Ordinal);

        var vendorTitleById = vendors.ToDictionary(v => v.Id, v => v.Title, StringComparer.Ordinal);
        var controlsByRequirement = BuildControlCatalogue(standardRequirementIds, controls, collectors, templates, vendorTitleById);

        var nodes = new List<SoaDrilldownNode>(resolved.Count);
        foreach (var node in resolved)
        {
            // Standard Out dominates: no requirement children. Otherwise enumerate every requirement of
            // the standard with its resolved disposition and provenance.
            IReadOnlyList<SoaRequirementNode> requirementNodes;
            if (string.Equals(node.Disposition, nameof(ScopeDisposition.In), StringComparison.Ordinal))
            {
                var ancestry = OrgAncestry.InclusiveAncestors(node.Id, byId);
                var list = new List<SoaRequirementNode>(standardRequirements.Count);
                foreach (var requirement in standardRequirements)
                {
                    var (disposition, resolution) = ResolveRequirement(node.Id, ancestry, requirement.Id, requirementScopeByOrg);

                    // An excluded (Out) requirement is a leaf: it carries no controls, so it renders
                    // without an expand toggle. Only an In requirement carries its mapped controls.
                    var reqControls =
                        string.Equals(disposition, nameof(ScopeDisposition.In), StringComparison.Ordinal)
                        && controlsByRequirement.TryGetValue(requirement.Id, out var c)
                            ? c
                            : [];
                    list.Add(new SoaRequirementNode(requirement.Id, requirement.Title, disposition, resolution, reqControls));
                }

                requirementNodes = list;
            }
            else
            {
                requirementNodes = [];
            }

            nodes.Add(new SoaDrilldownNode(
                node.Id, node.Title, node.Kind, node.Parent, node.Disposition, node.Resolution, requirementNodes));
        }

        return nodes;
    }

    /// <summary>
    /// Builds the org-independent requirement -> controls (each with its checks) catalogue. Controls
    /// attach to a requirement by <c>maps_to</c> (bounded to the standard's requirements); checks attach
    /// to a control by their <c>Control</c> field, tagged Collector or Attestation. Ordering: controls by
    /// id, checks by <c>(Kind, Id)</c> (collectors before attestations, each by id).
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<SoaControlNode>> BuildControlCatalogue(
        IReadOnlySet<string> standardRequirementIds,
        IReadOnlyList<ControlRow> controls,
        IReadOnlyList<EvidenceCollectorRow> collectors,
        IReadOnlyList<AttestationTemplateRow> templates,
        IReadOnlyDictionary<string, string> vendorTitleById)
    {
        var checksByControl = new Dictionary<string, List<SoaCheckNode>>(StringComparer.Ordinal);
        foreach (var collector in collectors)
        {
            // Show the vendor's title, not its raw id; fall back to the id when no vendor matches.
            var vendor = collector.Vendor is null
                ? null
                : vendorTitleById.TryGetValue(collector.Vendor, out var title) ? title : collector.Vendor;
            AddCheck(checksByControl, collector.Control, new SoaCheckNode(
                collector.Id, collector.Title, SoaCheckKind.Collector, collector.Type, collector.Frequency, vendor));
        }

        foreach (var template in templates)
        {
            AddCheck(checksByControl, template.Control, new SoaCheckNode(
                template.Id, template.Title, SoaCheckKind.Attestation, template.Type, null, null));
        }

        var controlNodeById = controls.ToDictionary(
            c => c.Id,
            c => new SoaControlNode(
                c.Id,
                c.Title,
                string.IsNullOrEmpty(c.Evaluation) ? null : c.Evaluation,
                checksByControl.TryGetValue(c.Id, out var checks)
                    ? checks.OrderBy(ch => ch.Kind).ThenBy(ch => ch.Id, StringComparer.Ordinal).ToList()
                    : []),
            StringComparer.Ordinal);

        var byRequirement = new Dictionary<string, List<SoaControlNode>>(StringComparer.Ordinal);
        foreach (var control in controls.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            foreach (var requirementId in control.MapsTo)
            {
                if (!standardRequirementIds.Contains(requirementId))
                {
                    continue;
                }

                if (!byRequirement.TryGetValue(requirementId, out var list))
                {
                    list = [];
                    byRequirement[requirementId] = list;
                }

                list.Add(controlNodeById[control.Id]);
            }
        }

        return byRequirement.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<SoaControlNode>)kv.Value, StringComparer.Ordinal);
    }

    private static void AddCheck(Dictionary<string, List<SoaCheckNode>> checksByControl, string controlId, SoaCheckNode check)
    {
        if (!checksByControl.TryGetValue(controlId, out var list))
        {
            list = [];
            checksByControl[controlId] = list;
        }

        list.Add(check);
    }

    /// <summary>
    /// Resolves one requirement's disposition and provenance for a node by walking its inclusive
    /// ancestry: the node's own requirement-scope wins (explicit); else the nearest ancestor's
    /// (inherited); else the requirement follows the node's standard disposition In (default).
    /// </summary>
    private static (string Disposition, SoaResolution Resolution) ResolveRequirement(
        string nodeId,
        IReadOnlyList<string> ancestry,
        string requirementId,
        IReadOnlyDictionary<string, Dictionary<string, string>> requirementScopeByOrg)
    {
        foreach (var orgId in ancestry)
        {
            if (TryGetRequirementDisposition(orgId, requirementId, requirementScopeByOrg, out var found))
            {
                return string.Equals(orgId, nodeId, StringComparison.Ordinal)
                    ? (found, SoaResolution.Explicit)
                    : (found, SoaResolution.Inherited);
            }
        }

        return (nameof(ScopeDisposition.In), SoaResolution.Default);
    }

    private static IReadOnlyList<SoaRequirementResolution> ResolveRequirements(
        string nodeId,
        IReadOnlyList<string> ancestry,
        IReadOnlyList<string> standardRequirementIds,
        IReadOnlyDictionary<string, Dictionary<string, string>> requirementScopeByOrg)
    {
        var results = new List<SoaRequirementResolution>();
        foreach (var requirementId in standardRequirementIds)
        {
            // Walk the inclusive ancestry [node, parent, ..., root]: the node's own requirement-scope
            // wins (explicit); else the nearest ancestor's (inherited); else the requirement is not a
            // deviation and follows the node's standard disposition (In).
            foreach (var orgId in ancestry)
            {
                if (TryGetRequirementDisposition(orgId, requirementId, requirementScopeByOrg, out var found))
                {
                    var resolution = string.Equals(orgId, nodeId, StringComparison.Ordinal)
                        ? SoaResolution.Explicit
                        : SoaResolution.Inherited;
                    results.Add(new SoaRequirementResolution(requirementId, found, resolution));
                    break;
                }
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

    private static (string Disposition, SoaResolution Resolution) ResolveNode(
        string nodeId,
        IReadOnlyList<string> ancestry,
        IReadOnlyDictionary<string, string> explicitByOrg)
    {
        // Walk the inclusive ancestry to the first node with an explicit disposition: the node itself
        // is Explicit, any ancestor is Inherited; none on the path defaults to In.
        foreach (var orgId in ancestry)
        {
            if (explicitByOrg.TryGetValue(orgId, out var found))
            {
                return string.Equals(orgId, nodeId, StringComparison.Ordinal)
                    ? (found, SoaResolution.Explicit)
                    : (found, SoaResolution.Inherited);
            }
        }

        return (nameof(ScopeDisposition.In), SoaResolution.Default);
    }
}
