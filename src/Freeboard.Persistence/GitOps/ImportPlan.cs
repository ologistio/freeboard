using System.Globalization;
using System.Text.Json;
using Freeboard.Core.GitOps;

namespace Freeboard.Persistence.GitOps;

/// <summary>A domain row to upsert. Keyed on <see cref="Id"/>; never matched on title.</summary>
public sealed record DomainRow(string Id, string ApiVersion, string Title);

/// <summary>
/// A control row to upsert. Carries the optional <see cref="Evaluation"/> roll-up rule (null when
/// blank); this extra column is why controls use their own row rather than the generic <see cref="DomainRow"/>.
/// </summary>
public sealed record ControlRowPlan(string Id, string ApiVersion, string Title, string? Evaluation);

/// <summary>
/// An evidence-collector row to upsert: a required control foreign key, an optional vendor foreign key
/// (null when blank), the type/frequency tokens, an optional <see cref="Threshold"/> integer percent
/// (null when blank), and <see cref="ConfigJson"/> - the type-specific settings map serialized to a
/// JSON string (null when the map is empty).
/// </summary>
public sealed record EvidenceCollectorRowPlan(
    string Id,
    string ApiVersion,
    string Title,
    string Control,
    string? Vendor,
    string Type,
    string Frequency,
    int? Threshold,
    string? ConfigJson);

/// <summary>
/// A standard row to upsert. Carries the metadata columns; optional <see cref="Publisher"/> and
/// <see cref="SourceUrl"/> are null when absent.
/// </summary>
public sealed record StandardRowPlan(
    string Id, string ApiVersion, string Title, string Version, string Authority, string? Publisher, string? SourceUrl);

/// <summary>
/// A requirement row to upsert: owning standard foreign key, theme, statement, optional guidance
/// (null when absent), and the citation columns.
/// </summary>
public sealed record RequirementRowPlan(
    string Id,
    string ApiVersion,
    string Title,
    string Standard,
    string Theme,
    string Statement,
    string? Guidance,
    string CitationLabel,
    string CitationUrl);

/// <summary>An organisation row to upsert, with its kind and nullable parent id.</summary>
public sealed record OrganisationRowPlan(string Id, string ApiVersion, string Title, string Kind, string? Parent);

/// <summary>A scope row to upsert: organisation and standard foreign keys plus disposition.</summary>
public sealed record ScopeRowPlan(
    string Id, string ApiVersion, string Title, string Organisation, string Standard, string Disposition);

/// <summary>
/// A requirement-scope row to upsert: organisation and requirement foreign keys plus disposition.
/// The standard is derived from the requirement, so no standard column is carried.
/// </summary>
public sealed record RequirementScopeRowPlan(
    string Id, string ApiVersion, string Title, string Organisation, string Requirement, string Disposition);

/// <summary>A control -> requirement cross-ref row.</summary>
public sealed record ControlRequirementRow(string ControlId, string RequirementId);

/// <summary>
/// A vendor-scope row to insert: vendor foreign key plus exactly one target (requirement or control,
/// the other null), disposition, and optional justification (null when blank).
/// </summary>
public sealed record VendorScopeRowPlan(
    string Id,
    string ApiVersion,
    string Title,
    string Vendor,
    string? Requirement,
    string? Control,
    string Disposition,
    string? Justification);

/// <summary>
/// The flattened, id-keyed shape derived from a validated <see cref="GitOpsConfig"/>.
/// Pure (no database), so the mapping is unit testable without MySQL. Organisations are
/// ordered parent-before-child so the self-FK holds during the import upsert.
/// </summary>
public sealed class ImportPlan
{
    public IReadOnlyList<StandardRowPlan> Standards { get; }

    public IReadOnlyList<RequirementRowPlan> Requirements { get; }

    public IReadOnlyList<ControlRowPlan> Controls { get; }

    public IReadOnlyList<OrganisationRowPlan> Organisations { get; }

    public IReadOnlyList<ScopeRowPlan> Scopes { get; }

    public IReadOnlyList<RequirementScopeRowPlan> RequirementScopes { get; }

    public IReadOnlyList<ControlRequirementRow> ControlRequirements { get; }

    public IReadOnlyList<DomainRow> Vendors { get; }

    public IReadOnlyList<VendorScopeRowPlan> VendorScopes { get; }

    public IReadOnlyList<EvidenceCollectorRowPlan> EvidenceCollectors { get; }

    private ImportPlan(GitOpsConfig config)
    {
        // Optional fields normalize to null here (blank means absent), mirroring Organisation.Parent.
        Standards = config.Standards
            .Select(s => new StandardRowPlan(
                s.Id, s.ApiVersion, s.Title, s.Version, s.Authority, NullIfBlank(s.Publisher), NullIfBlank(s.SourceUrl)))
            .ToList();
        Requirements = config.Requirements
            .Select(r => new RequirementRowPlan(
                r.Id, r.ApiVersion, r.Title, r.Standard, r.Theme, r.Statement,
                NullIfBlank(r.Guidance), r.CitationLabel, r.CitationUrl))
            .ToList();
        Controls = config.Controls
            .Select(c => new ControlRowPlan(c.Id, c.ApiVersion, c.Title, NullIfBlank(c.Evaluation)))
            .ToList();
        Organisations = OrderParentBeforeChild(config.Organisations);
        Scopes = config.Scopes
            .Select(s => new ScopeRowPlan(
                s.Id, s.ApiVersion, s.Title, s.Organisation, s.Standard, s.Disposition))
            .ToList();
        RequirementScopes = config.RequirementScopes
            .Select(s => new RequirementScopeRowPlan(
                s.Id, s.ApiVersion, s.Title, s.Organisation, s.Requirement, s.Disposition))
            .ToList();

        // Distinct guards the composite-PK join table against a duplicate maps_to id within a
        // single control even if a caller skips Core validation. Record equality is ordinal on
        // the id strings, consistent with id identity.
        ControlRequirements = config.Controls
            .SelectMany(c => c.MapsTo.Select(requirementId => new ControlRequirementRow(c.Id, requirementId)))
            .Distinct()
            .ToList();

        Vendors = config.Vendors
            .Select(v => new DomainRow(v.Id, v.ApiVersion, v.Title))
            .ToList();

        // Exactly one target is set (Core validation guarantees it); the empty side normalizes to
        // null. A blank justification (permitted on an In scope) normalizes to null like other
        // optional fields.
        VendorScopes = config.VendorScopes
            .Select(v => new VendorScopeRowPlan(
                v.Id, v.ApiVersion, v.Title, v.Vendor,
                NullIfBlank(v.Requirement), NullIfBlank(v.Control), v.Disposition, NullIfBlank(v.Justification)))
            .ToList();

        // Threshold is parsed to int? only here, after Core validation has range-checked the raw text;
        // a blank stays null. config serializes to a JSON object string, null when the map is empty.
        EvidenceCollectors = config.EvidenceCollectors
            .Select(c => new EvidenceCollectorRowPlan(
                c.Id, c.ApiVersion, c.Title, c.Control, NullIfBlank(c.Vendor), c.Type, c.Frequency,
                ParseThreshold(c.Threshold), SerializeConfig(c.Config)))
            .ToList();
    }

    public static ImportPlan From(GitOpsConfig config) => new(config);

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int? ParseThreshold(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string? SerializeConfig(IReadOnlyDictionary<string, string> config) =>
        config.Count == 0 ? null : JsonSerializer.Serialize(config);

    public IReadOnlyList<string> StandardIds => Standards.Select(r => r.Id).ToList();

    public IReadOnlyList<string> RequirementIds => Requirements.Select(r => r.Id).ToList();

    public IReadOnlyList<string> ControlIds => Controls.Select(r => r.Id).ToList();

    /// <summary>Organisation ids in parent-before-child order (upsert order).</summary>
    public IReadOnlyList<string> OrganisationIds => Organisations.Select(r => r.Id).ToList();

    public IReadOnlyList<string> ScopeIds => Scopes.Select(r => r.Id).ToList();

    public IReadOnlyList<string> RequirementScopeIds => RequirementScopes.Select(r => r.Id).ToList();

    public IReadOnlyList<string> VendorIds => Vendors.Select(r => r.Id).ToList();

    public IReadOnlyList<string> VendorScopeIds => VendorScopes.Select(r => r.Id).ToList();

    public IReadOnlyList<string> EvidenceCollectorIds => EvidenceCollectors.Select(r => r.Id).ToList();

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
