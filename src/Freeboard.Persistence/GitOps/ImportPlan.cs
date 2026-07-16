using System.Globalization;
using System.Text.Json;
using Freeboard.Core.GitOps;

namespace Freeboard.Persistence.GitOps;

/// <summary>
/// A control row to upsert. Carries the optional <see cref="Evaluation"/> roll-up rule (null when
/// blank); this extra column is why controls use their own row rather than a generic domain row.
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
    string? ConfigJson,
    string? Connection,
    string? ChecksJson);

/// <summary>
/// An integration-connection row to upsert: the provider and discovery-cadence tokens, an absolute
/// base URL, and an optional vendor foreign key (null when blank). The API token is resolved out-of-band
/// and is never a column here.
/// </summary>
public sealed record IntegrationConnectionRowPlan(
    string Id,
    string ApiVersion,
    string Title,
    string Provider,
    string BaseUrl,
    string DiscoveryCadence,
    string? Vendor);

/// <summary>
/// An attestation-template row to upsert: a required control foreign key, the type token, an optional
/// <see cref="Body"/> (null when blank), an optional <see cref="PassMark"/> integer percent (null when
/// blank), and <see cref="FieldsJson"/>/<see cref="QuizJson"/> - the ordered field and quiz lists
/// serialized to a JSON array string (null when the list is empty). The serialized quiz includes each
/// item's answer for the later grading runtime; the answer is redacted at the read-model boundary.
/// </summary>
public sealed record AttestationTemplateRowPlan(
    string Id,
    string ApiVersion,
    string Title,
    string Control,
    string Type,
    string? Body,
    string? FieldsJson,
    int? PassMark,
    string? QuizJson);

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

/// <summary>
/// A declared asset row to upsert, with its <see cref="Type"/> (Company/Department/Vendor) and the two
/// nullable, mutually-exclusive edges. No parent-before-child ordering is needed: assets.parent has no
/// foreign key, so upsert and delete need no topological order.
/// </summary>
public sealed record AssetRowPlan(string Id, string ApiVersion, string Title, string Type, string? Parent, string? Owner);

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
/// Pure (no database), so the mapping is unit testable without MySQL.
/// </summary>
public sealed class ImportPlan
{
    public IReadOnlyList<StandardRowPlan> Standards { get; }

    public IReadOnlyList<RequirementRowPlan> Requirements { get; }

    public IReadOnlyList<ControlRowPlan> Controls { get; }

    public IReadOnlyList<AssetRowPlan> Assets { get; }

    public IReadOnlyList<ScopeRowPlan> Scopes { get; }

    public IReadOnlyList<RequirementScopeRowPlan> RequirementScopes { get; }

    public IReadOnlyList<ControlRequirementRow> ControlRequirements { get; }

    public IReadOnlyList<VendorScopeRowPlan> VendorScopes { get; }

    public IReadOnlyList<EvidenceCollectorRowPlan> EvidenceCollectors { get; }

    public IReadOnlyList<AttestationTemplateRowPlan> AttestationTemplates { get; }

    public IReadOnlyList<IntegrationConnectionRowPlan> IntegrationConnections { get; }

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
        // Declared assets: parent/owner normalize to null-if-blank (like Organisation.Parent did). No
        // parent-before-child ordering because assets.parent has no foreign key.
        Assets = config.Assets
            .Select(a => new AssetRowPlan(a.Id, a.ApiVersion, a.Title, a.Type, NullIfBlank(a.Parent), NullIfBlank(a.Owner)))
            .ToList();
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
        // connection is the connection foreign key (null when blank); checks serializes to a JSON array
        // string, null when the list is empty.
        EvidenceCollectors = config.EvidenceCollectors
            .Select(c => new EvidenceCollectorRowPlan(
                c.Id, c.ApiVersion, c.Title, c.Control, NullIfBlank(c.Vendor), c.Type, c.Frequency,
                ParseThreshold(c.Threshold), SerializeConfig(c.Config), NullIfBlank(c.Connection), SerializeList(c.Checks)))
            .ToList();

        // Optional vendor normalizes to null (blank means absent), like the collector's vendor.
        IntegrationConnections = config.IntegrationConnections
            .Select(c => new IntegrationConnectionRowPlan(
                c.Id, c.ApiVersion, c.Title, c.Provider, c.BaseUrl, c.DiscoveryCadence, NullIfBlank(c.Vendor)))
            .ToList();

        // pass_mark is parsed to int? only here, after Core validation has range-checked the raw text; a
        // blank stays null. fields/quiz serialize to a JSON array string, null when the list is empty. The
        // serialized quiz keeps each item's answer for the later grading runtime.
        AttestationTemplates = config.AttestationTemplates
            .Select(t => new AttestationTemplateRowPlan(
                t.Id, t.ApiVersion, t.Title, t.Control, t.Type, NullIfBlank(t.Body),
                SerializeList(t.Fields), ParseThreshold(t.PassMark), SerializeList(t.Quiz)))
            .ToList();
    }

    public static ImportPlan From(GitOpsConfig config) => new(config);

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int? ParseThreshold(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string? SerializeConfig(IReadOnlyDictionary<string, string> config) =>
        config.Count == 0 ? null : JsonSerializer.Serialize(config);

    private static string? SerializeList<T>(IReadOnlyList<T> items) =>
        items.Count == 0 ? null : JsonSerializer.Serialize(items);

    public IReadOnlyList<string> StandardIds => Standards.Select(r => r.Id).ToList();

    public IReadOnlyList<string> RequirementIds => Requirements.Select(r => r.Id).ToList();

    public IReadOnlyList<string> ControlIds => Controls.Select(r => r.Id).ToList();

    /// <summary>Every declared asset id (the keep set for the source-guarded declared-asset prune).</summary>
    public IReadOnlyList<string> AssetIds => Assets.Select(r => r.Id).ToList();

    /// <summary>Declared Company/Department asset ids (the keep set for the org-role-assignment prune).</summary>
    public IReadOnlyList<string> OrganisationIds =>
        Assets.Where(r => r.Type is "Company" or "Department").Select(r => r.Id).ToList();

    public IReadOnlyList<string> ScopeIds => Scopes.Select(r => r.Id).ToList();

    public IReadOnlyList<string> RequirementScopeIds => RequirementScopes.Select(r => r.Id).ToList();

    public IReadOnlyList<string> VendorScopeIds => VendorScopes.Select(r => r.Id).ToList();

    public IReadOnlyList<string> EvidenceCollectorIds => EvidenceCollectors.Select(r => r.Id).ToList();

    public IReadOnlyList<string> AttestationTemplateIds => AttestationTemplates.Select(r => r.Id).ToList();

    public IReadOnlyList<string> IntegrationConnectionIds => IntegrationConnections.Select(r => r.Id).ToList();
}
