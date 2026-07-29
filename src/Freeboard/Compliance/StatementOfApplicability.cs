using Freeboard.Core.GitOps;
using Freeboard.Persistence;

namespace Freeboard.Compliance;

/// <summary>How a node's disposition for a standard was determined.</summary>
public enum SoaResolution
{
    /// <summary>The asset the node stands for has its own Scope for the standard.</summary>
    Asset,

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
        SoaResolution.Asset => "asset",
        SoaResolution.Inherited => "inherited",
        _ => "default",
    };
}

/// <summary>
/// One requirement-level deviation on a node: the requirement's resolved disposition
/// (<c>In</c> or <c>Out</c>) and whether the node's own requirement-scope set it
/// (<see cref="SoaResolution.Asset"/>) or it was inherited from an ancestor.
/// </summary>
public sealed record SoaRequirementResolution(
    string Requirement, string Disposition, SoaResolution Resolution);

/// <summary>
/// One node in a Statement of Applicability: its resolved standard disposition
/// (always <c>In</c> or <c>Out</c>, never null) and how that value was reached
/// (<see cref="SoaResolution.Default"/> means in-scope with no authored Scope on the path).
/// <see cref="Requirements"/> lists only the requirement-level deviations (requirements with
/// an own or inherited requirement-scope) and is populated only where the standard
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

/// <summary>
/// The kind of configured check attached to a control, derived from the collector's type: a
/// <c>manual</c> or <c>training</c> collector is an attestation, every other type is a collector.
/// </summary>
public enum SoaCheckKind
{
    /// <summary>A data-source collector attached to the control.</summary>
    Collector,

    /// <summary>An attestation form or training quiz attached to the control.</summary>
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
/// One configured check under a control, tagged <see cref="SoaCheckKind.Collector"/> or
/// <see cref="SoaCheckKind.Attestation"/> by its collector's type. Metadata only.
/// <see cref="Frequency"/> is null on an attestation-tagged check even though every collector now has
/// one: only a collector-tagged check carries an evidence status, and a cadence beside a check with no
/// status is a collection promise this page cannot back. <see cref="Vendor"/> is uniform across both
/// tags - it plays no part in status interpretation. Quiz answers are never surfaced.
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
/// (<c>In</c> or <c>Out</c>) and provenance (asset/inherited/default), with the controls that map
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
/// Resolves a Statement of Applicability for a standard: a projection over the asset
/// tree that assigns each node a disposition by nearest-ancestor inheritance. Pure (no
/// I/O), so the inheritance rule is unit testable. A node with no Scope on its path
/// defaults to <c>In</c>, so the standard disposition is always <c>In</c> or <c>Out</c>.
/// </summary>
public static class StatementOfApplicability
{
    public static IReadOnlyList<SoaNode> Resolve(
        IReadOnlyList<AssetNode> assets,
        IReadOnlyList<ScopeRow> scopes,
        IReadOnlyList<RequirementRow> requirements,
        string standardId)
    {
        var byId = assets.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var ancestryCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        // Standard-target scopes for this standard, keyed by subject (an asset id resolves against a node).
        var scopeByAsset = scopes
            .Where(s => string.Equals(s.Standard, standardId, StringComparison.Ordinal))
            .GroupBy(s => s.Subject, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Disposition, StringComparer.Ordinal);

        // Requirements of the requested standard, ordered by id, and the requirement-target scopes that
        // bind them. A requirement-target scope for a requirement of another standard is excluded here,
        // mirroring how the standard layer filters scopes by standardId.
        var standardRequirementIds = requirements
            .Where(r => string.Equals(r.Standard, standardId, StringComparison.Ordinal))
            .Select(r => r.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        var standardRequirementIdSet = standardRequirementIds.ToHashSet(StringComparer.Ordinal);

        // Nearest-ancestor lookup for the requirement layer: (subject, requirement) -> disposition.
        var requirementScopeByOrg = scopes
            .Where(s => s.Requirement is not null && standardRequirementIdSet.Contains(s.Requirement))
            .GroupBy(s => s.Subject, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(s => s.Requirement!, StringComparer.Ordinal)
                    .ToDictionary(rg => rg.Key, rg => rg.First().Disposition, StringComparer.Ordinal),
                StringComparer.Ordinal);

        var nodes = new List<SoaNode>(assets.Count);
        foreach (var asset in assets)
        {
            // Build the node's inclusive ancestry ONCE via the shared helper, then consume it for the
            // node-set test and for both nearest-ancestor lookups.
            var ancestry = AssetAncestry.InclusiveAncestors(asset.Id, byId, ancestryCache);
            if (!IsNode(asset, ancestry, byId))
            {
                continue;
            }

            var (disposition, resolution) = ResolveNode(asset.Id, ancestry, scopeByAsset);

            // Requirement-scopes apply only under a standard that resolves In at this node.
            var requirementResolutions = string.Equals(disposition, nameof(ScopeDisposition.In), StringComparison.Ordinal)
                ? ResolveRequirements(asset.Id, ancestry, standardRequirementIds, requirementScopeByOrg)
                : [];

            nodes.Add(new SoaNode(
                asset.Id,
                asset.Title,
                asset.Type,
                asset.Parent,
                disposition,
                resolution,
                requirementResolutions));
        }

        return nodes.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The node set: every organisation UNCONDITIONALLY, plus every other asset whose inclusive
    /// <c>parent</c> chain reaches one.
    ///
    /// The first arm is a type test on purpose. A dangling <c>parent</c> and a <c>parent</c> cycle are
    /// both non-blocking sync warnings, so both are reachable in a validly synced deployment; making an
    /// organisation's membership depend on reaching a root would drop it from the Statement of
    /// Applicability and from the evidence-ingest gate, and deny it the default <c>In</c> the opt-out
    /// scoping model guarantees every organisation. A vendor falls out with no vendor-specific branch:
    /// it is not organisation-typed and carries <c>owner</c> rather than <c>parent</c>.
    /// </summary>
    private static bool IsNode(
        AssetNode asset, IReadOnlyList<string> ancestry, IReadOnlyDictionary<string, AssetNode> byId)
        => asset.IsOrganisation
            || ancestry.Any(id => byId.TryGetValue(id, out var node) && node.IsOrganisation);

    /// <summary>
    /// Projects the four-level drill-down (organisation -> requirement -> control -> check) for a
    /// standard. Reuses <see cref="Resolve"/> for each node's org-level disposition and provenance, then
    /// enumerates every requirement of the standard per node (not only deviations), each tagged with its
    /// resolved disposition (<c>In</c>/<c>Out</c>) and provenance. An <c>In</c> requirement carries its
    /// controls (by <c>maps_to</c>) and checks (collectors and templates by their <c>Control</c>, tagged
    /// by kind); an <c>Out</c> requirement is a leaf and carries no controls. The
    /// requirement -> control -> check catalogue is org-independent, so it is built once and shared. A
    /// collector's vendor is shown by title, and only when that vendor is in
    /// <paramref name="accessibleAssetIds"/>; an unreadable vendor renders exactly as an unset one.
    ///
    /// The node list is organisation-only: the page's selector scoping, active-scope label, and batched
    /// per-collector evidence status are all keyed on an organisation id, so a machine row would be a
    /// data-shape change rather than a resolution one. The flat <see cref="Resolve"/> does not filter.
    /// Pure (no I/O).
    /// </summary>
    public static IReadOnlyList<SoaDrilldownNode> ResolveDrilldown(
        IReadOnlyList<AssetNode> assets,
        IReadOnlyList<ScopeRow> scopes,
        IReadOnlyList<RequirementRow> requirements,
        IReadOnlyList<ControlRow> controls,
        IReadOnlyList<CollectorRow> collectors,
        IReadOnlySet<string> accessibleAssetIds,
        string standardId)
    {
        // Org-level disposition/provenance: reuse the flat resolver so the inheritance rule is not
        // duplicated. The full in-scope requirement enumeration below is new: Resolve yields only
        // deviations and never a requirement-level Default.
        var organisationIds = assets.Where(a => a.IsOrganisation).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var resolved = Resolve(assets, scopes, requirements, standardId)
            .Where(n => organisationIds.Contains(n.Id))
            .ToList();

        var byId = assets.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var ancestryCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        var standardRequirements = requirements
            .Where(r => string.Equals(r.Standard, standardId, StringComparison.Ordinal))
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
        var standardRequirementIds = standardRequirements.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

        var requirementScopeByOrg = scopes
            .Where(s => s.Requirement is not null && standardRequirementIds.Contains(s.Requirement))
            .GroupBy(s => s.Subject, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(s => s.Requirement!, StringComparer.Ordinal)
                    .ToDictionary(rg => rg.Key, rg => rg.First().Disposition, StringComparer.Ordinal),
                StringComparer.Ordinal);

        // Only the vendors the caller may read contribute a title. A vendor outside the set has no entry,
        // so the check's vendor resolves null and renders as an unset one.
        var vendorTitleById = assets
            .Where(a => a.Type is "Vendor" && accessibleAssetIds.Contains(a.Id))
            .ToDictionary(a => a.Id, a => a.Title, StringComparer.Ordinal);
        var controlsByRequirement = BuildControlCatalogue(standardRequirementIds, controls, collectors, vendorTitleById);

        var nodes = new List<SoaDrilldownNode>(resolved.Count);
        foreach (var node in resolved)
        {
            // Standard Out dominates: no requirement children. Otherwise enumerate every requirement of
            // the standard with its resolved disposition and provenance.
            IReadOnlyList<SoaRequirementNode> requirementNodes;
            if (string.Equals(node.Disposition, nameof(ScopeDisposition.In), StringComparison.Ordinal))
            {
                var ancestry = AssetAncestry.InclusiveAncestors(node.Id, byId, ancestryCache);
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
    /// to a control by their <c>Control</c> field, tagged Collector or Attestation by the collector's
    /// type. Ordering: controls by id, checks by <c>(Kind, Id)</c> (collectors before attestations, each
    /// by id).
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<SoaControlNode>> BuildControlCatalogue(
        IReadOnlySet<string> standardRequirementIds,
        IReadOnlyList<ControlRow> controls,
        IReadOnlyList<CollectorRow> collectors,
        IReadOnlyDictionary<string, string> vendorTitleById)
    {
        var checksByControl = new Dictionary<string, List<SoaCheckNode>>(StringComparer.Ordinal);
        foreach (var collector in collectors)
        {
            // Show the vendor's title, never its raw id: the map holds only the vendors the caller may
            // read, so a vendor it does not name is withheld rather than printed as an id.
            var vendor = collector.Vendor is not null && vendorTitleById.TryGetValue(collector.Vendor, out var title)
                ? title
                : null;
            // The tag and the cadence are one decision: an attestation-tagged check carries no evidence
            // status, so it must not advertise a collection cadence either.
            var attestation = collector.Type is "manual" or "training";
            AddCheck(checksByControl, collector.Control, new SoaCheckNode(
                collector.Id,
                collector.Title,
                attestation ? SoaCheckKind.Attestation : SoaCheckKind.Collector,
                collector.Type,
                attestation ? null : collector.Frequency,
                vendor));
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

    /// <summary>
    /// True when any scope of ANY target kind (standard, requirement, or control) names a subject that
    /// resolves to no live asset - no asset row, or a retired discovered one.
    /// Drives the generic page-level dangling-subject notice; the caller must NOT disclose the scope id or
    /// subject id (an unresolved subject has no authorization anchor to check readability against).
    /// </summary>
    public static bool HasDanglingSubject(
        IReadOnlyList<ScopeRow> scopes, IReadOnlyList<AssetNode> assets)
    {
        var live = assets
            .Where(a => !(string.Equals(a.Source, "discovered", StringComparison.Ordinal)
                && string.Equals(a.State, "Retired", StringComparison.Ordinal)))
            .Select(a => a.Id)
            .ToHashSet(StringComparer.Ordinal);
        return scopes.Any(s => !live.Contains(s.Subject));
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
    /// ancestry: the node's own requirement-scope wins (asset); else the nearest ancestor's
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
                    ? (found, SoaResolution.Asset)
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
            // wins (asset); else the nearest ancestor's (inherited); else the requirement is not a
            // deviation and follows the node's standard disposition (In).
            foreach (var orgId in ancestry)
            {
                if (TryGetRequirementDisposition(orgId, requirementId, requirementScopeByOrg, out var found))
                {
                    var resolution = string.Equals(orgId, nodeId, StringComparison.Ordinal)
                        ? SoaResolution.Asset
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
        IReadOnlyDictionary<string, string> scopeByAsset)
    {
        // Walk the inclusive ancestry to the first entry carrying a disposition: the node itself is
        // Asset, any ancestor is Inherited; none on the path defaults to In.
        foreach (var orgId in ancestry)
        {
            if (scopeByAsset.TryGetValue(orgId, out var found))
            {
                return string.Equals(orgId, nodeId, StringComparison.Ordinal)
                    ? (found, SoaResolution.Asset)
                    : (found, SoaResolution.Inherited);
            }
        }

        return (nameof(ScopeDisposition.In), SoaResolution.Default);
    }
}
