using System.Globalization;
using Freeboard.Core.GitOps;

namespace Freeboard.Persistence.GitOps;

/// <summary>
/// A control row to upsert. Carries the optional <see cref="Evaluation"/> roll-up rule (null when
/// blank); this extra column is why controls use their own row rather than a generic domain row.
/// </summary>
public sealed record ControlRowPlan(string Id, string ApiVersion, string Title, string? Evaluation);

/// <summary>
/// A collector row to upsert: a required control foreign key, optional vendor and connection foreign
/// keys (null when blank), the type/provider/frequency tokens (provider null off the integration path),
/// an optional <see cref="Threshold"/> integer percent (null when blank), and <see cref="ConfigJson"/> -
/// the type-specific payload serialized to the storage shape (null when no member is present). There is
/// no separate checks column: tracked checks are a <c>config</c> key.
/// </summary>
public sealed record CollectorRowPlan(
    string Id,
    string ApiVersion,
    string Title,
    string Control,
    string? Vendor,
    string? Connection,
    string Type,
    string? Provider,
    string Frequency,
    int? Threshold,
    string? ConfigJson);

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

/// <summary>
/// A scope row to insert: a scalar subject plus exactly one target (standard, requirement, or control,
/// the other two null), disposition, and optional justification (null when blank).
/// </summary>
public sealed record ScopeRowPlan(
    string Id,
    string ApiVersion,
    string Title,
    string Subject,
    string? Standard,
    string? Requirement,
    string? Control,
    string Disposition,
    string? Justification);

/// <summary>A control -> requirement cross-ref row.</summary>
public sealed record ControlRequirementRow(string ControlId, string RequirementId);

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

    public IReadOnlyList<ControlRequirementRow> ControlRequirements { get; }

    public IReadOnlyList<CollectorRowPlan> Collectors { get; }

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
        // Exactly one target is set (Core validation guarantees it); the empty target sides normalize to
        // null. A blank justification (permitted on an In scope) normalizes to null like other optional
        // fields.
        Scopes = config.Scopes
            .Select(s => new ScopeRowPlan(
                s.Id, s.ApiVersion, s.Title, s.Subject,
                NullIfBlank(s.Standard), NullIfBlank(s.Requirement), NullIfBlank(s.Control),
                s.Disposition, NullIfBlank(s.Justification)))
            .ToList();

        // Distinct guards the composite-PK join table against a duplicate maps_to id within a
        // single control even if a caller skips Core validation. Record equality is ordinal on
        // the id strings, consistent with id identity.
        ControlRequirements = config.Controls
            .SelectMany(c => c.MapsTo.Select(requirementId => new ControlRequirementRow(c.Id, requirementId)))
            .Distinct()
            .ToList();

        // threshold and the config pass_mark are parsed to int? only here, after Core validation has
        // range-checked the raw text; a blank stays null. The optional vendor, connection, and provider
        // normalize to null (blank means absent). config serializes to the storage shape, which is a
        // projection of the authored record rather than the record itself - see StoredCollectorConfig.
        Collectors = config.Collectors
            .Select(c => new CollectorRowPlan(
                c.Id, c.ApiVersion, c.Title, c.Control, NullIfBlank(c.Vendor), NullIfBlank(c.Connection),
                c.Type, NullIfBlank(c.Provider), c.Frequency, ParseThreshold(c.Threshold),
                StoredCollectorConfig.Write(c.Config, ParseThreshold(c.Config.PassMark))))
            .ToList();

        // Optional vendor normalizes to null (blank means absent), like the collector's vendor.
        IntegrationConnections = config.IntegrationConnections
            .Select(c => new IntegrationConnectionRowPlan(
                c.Id, c.ApiVersion, c.Title, c.Provider, c.BaseUrl, c.DiscoveryCadence, NullIfBlank(c.Vendor)))
            .ToList();
    }

    public static ImportPlan From(GitOpsConfig config) => new(config);

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int? ParseThreshold(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    public IReadOnlyList<string> StandardIds => Standards.Select(r => r.Id).ToList();

    public IReadOnlyList<string> RequirementIds => Requirements.Select(r => r.Id).ToList();

    public IReadOnlyList<string> ControlIds => Controls.Select(r => r.Id).ToList();

    /// <summary>Every declared asset id (the keep set for the source-guarded declared-asset prune).</summary>
    public IReadOnlyList<string> AssetIds => Assets.Select(r => r.Id).ToList();

    /// <summary>Declared Company/Department asset ids (the keep set for the org-role-assignment prune).</summary>
    public IReadOnlyList<string> OrganisationIds =>
        Assets.Where(r => r.Type is "Company" or "Department").Select(r => r.Id).ToList();

    public IReadOnlyList<string> CollectorIds => Collectors.Select(r => r.Id).ToList();

    public IReadOnlyList<string> IntegrationConnectionIds => IntegrationConnections.Select(r => r.Id).ToList();
}
