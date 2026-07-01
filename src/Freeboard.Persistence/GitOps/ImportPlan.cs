using Freeboard.Core.GitOps;

namespace Freeboard.Persistence.GitOps;

/// <summary>A domain row to upsert. Keyed on <see cref="Id"/>; never matched on title.</summary>
public sealed record DomainRow(string Id, string ApiVersion, string Title);

/// <summary>An organisation row to upsert, with its kind and nullable parent id.</summary>
public sealed record OrganisationRowPlan(string Id, string ApiVersion, string Title, string Kind, string? Parent);

/// <summary>A scope row to upsert: organisation and standard foreign keys plus disposition.</summary>
public sealed record ScopeRowPlan(
    string Id, string ApiVersion, string Title, string Organisation, string Standard, string Disposition);

/// <summary>A control -> standard cross-ref row.</summary>
public sealed record ControlStandardRow(string ControlId, string StandardId);

/// <summary>
/// The flattened, id-keyed shape derived from a validated <see cref="GitOpsConfig"/>.
/// Pure (no database), so the mapping is unit testable without MySQL. Organisations are
/// ordered parent-before-child so the self-FK holds during the import upsert.
/// </summary>
public sealed class ImportPlan
{
    public IReadOnlyList<DomainRow> Standards { get; }

    public IReadOnlyList<DomainRow> Controls { get; }

    public IReadOnlyList<OrganisationRowPlan> Organisations { get; }

    public IReadOnlyList<ScopeRowPlan> Scopes { get; }

    public IReadOnlyList<ControlStandardRow> ControlStandards { get; }

    private ImportPlan(GitOpsConfig config)
    {
        Standards = config.Standards
            .Select(s => new DomainRow(s.Id, s.ApiVersion, s.Title))
            .ToList();
        Controls = config.Controls
            .Select(c => new DomainRow(c.Id, c.ApiVersion, c.Title))
            .ToList();
        Organisations = OrderParentBeforeChild(config.Organisations);
        Scopes = config.Scopes
            .Select(s => new ScopeRowPlan(
                s.Id, s.ApiVersion, s.Title, s.Organisation, s.Standard, s.Disposition))
            .ToList();

        // Distinct guards the composite-PK join table against a duplicate maps_to id within a
        // single control even if a caller skips Core validation. Record equality is ordinal on
        // the id strings, consistent with id identity.
        ControlStandards = config.Controls
            .SelectMany(c => c.MapsTo.Select(standardId => new ControlStandardRow(c.Id, standardId)))
            .Distinct()
            .ToList();
    }

    public static ImportPlan From(GitOpsConfig config) => new(config);

    public IReadOnlyList<string> StandardIds => Standards.Select(r => r.Id).ToList();

    public IReadOnlyList<string> ControlIds => Controls.Select(r => r.Id).ToList();

    /// <summary>Organisation ids in parent-before-child order (upsert order).</summary>
    public IReadOnlyList<string> OrganisationIds => Organisations.Select(r => r.Id).ToList();

    public IReadOnlyList<string> ScopeIds => Scopes.Select(r => r.Id).ToList();

    /// <summary>
    /// Orders organisations so every parent precedes its children (topological by depth).
    /// A validated config is acyclic with resolvable parents, so a stable order exists;
    /// any node whose parent is not present (e.g. unvalidated input) is treated as a root
    /// so the method still terminates.
    /// </summary>
    private static IReadOnlyList<OrganisationRowPlan> OrderParentBeforeChild(IReadOnlyList<Organisation> organisations)
    {
        var byId = new Dictionary<string, Organisation>(StringComparer.Ordinal);
        foreach (var organisation in organisations)
        {
            byId[organisation.Id] = organisation;
        }

        var depth = new Dictionary<string, int>(StringComparer.Ordinal);

        int DepthOf(string id)
        {
            if (depth.TryGetValue(id, out var cached))
            {
                return cached;
            }

            // Guard against an unvalidated cycle: mark in-progress as depth 0 so a back edge
            // does not recurse forever.
            depth[id] = 0;
            var node = byId[id];
            var computed = string.IsNullOrEmpty(node.Parent) || !byId.ContainsKey(node.Parent)
                ? 0
                : DepthOf(node.Parent) + 1;
            depth[id] = computed;
            return computed;
        }

        return organisations
            .OrderBy(o => DepthOf(o.Id))
            .ThenBy(o => o.Id, StringComparer.Ordinal)
            .Select(o => new OrganisationRowPlan(
                o.Id,
                o.ApiVersion,
                o.Title,
                o.OrgKind,
                string.IsNullOrEmpty(o.Parent) ? null : o.Parent))
            .ToList();
    }
}
