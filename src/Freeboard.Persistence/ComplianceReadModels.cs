using System.Text.Json.Serialization;
using Freeboard.Core.GitOps;

namespace Freeboard.Persistence;

/// <summary>
/// A persisted standard. Identity is <see cref="Id"/>. <see cref="Version"/> and
/// <see cref="Authority"/> are non-empty once synced from a v3.3+ config; <see cref="Publisher"/>
/// and <see cref="SourceUrl"/> are null when unset.
/// </summary>
public sealed record StandardRow(
    string Id, string Title, string? Version, string? Authority, string? Publisher, string? SourceUrl);

/// <summary>
/// A persisted requirement owned by one standard. Identity is <see cref="Id"/>.
/// <see cref="Guidance"/> is null when unset.
/// </summary>
public sealed record RequirementRow(
    string Id,
    string Title,
    string Standard,
    string Theme,
    string Statement,
    string? Guidance,
    string CitationLabel,
    string CitationUrl);

/// <summary>
/// A persisted control with its resolved <see cref="MapsTo"/> requirement ids and its optional
/// <see cref="Evaluation"/> roll-up rule (null when unset).
/// </summary>
public sealed record ControlRow(string Id, string Title, IReadOnlyList<string> MapsTo, string? Evaluation);

/// <summary>
/// One persisted asset of any type, as the read side sees it. <see cref="Parent"/> and
/// <see cref="Owner"/> are the two scalar edges and are mutually exclusive: an organisation or a device
/// carries <c>parent</c>, a vendor carries <c>owner</c>. <see cref="State"/> is a discovered-only column
/// and reads null on a declared asset, which the live-subject predicate treats as live.
/// </summary>
public sealed record AssetNode(
    string Id, string Title, string Type, string Source, string? State, string? Parent, string? Owner)
{
    /// <summary>
    /// True when this asset is an organisation node. The ONE expression of that rule on
    /// <see cref="AssetNode"/>: the resolver's node set, the read surfaces that present organisations,
    /// the write-path authorization guards, and the evidence-ingest acceptance gate all decide it here,
    /// so they cannot drift apart.
    /// </summary>
    public bool IsOrganisation => Type is "Company" or "Department";
}

/// <summary>
/// A persisted scope mapping one asset <see cref="Subject"/> to exactly one target - a standard,
/// requirement, or control (the other two null) - with a disposition (<c>In</c> or <c>Out</c>) and an
/// optional <see cref="Justification"/> (null when unset; always present for an <c>Out</c>). These are
/// exactly the public/wire (<c>ApiScope</c>) fields: readability is decided by resolving
/// <see cref="Subject"/> against the asset read, not by narrowing fields carried on the row.
/// </summary>
public sealed record ScopeRow(
    string Id,
    string Title,
    string Subject,
    string? Standard,
    string? Requirement,
    string? Control,
    string Disposition,
    string? Justification);

/// <summary>
/// The inputs the Statement of Applicability projection needs, read together in one
/// repeatable-read snapshot so they cannot straddle a concurrent importer commit. <see cref="Assets"/>
/// is the whole unfiltered asset set: the resolution tree and the live-subject predicate need different
/// subsets of it, so retirement and type are applied by the consumer. The requirement layer comes from
/// the one unified <see cref="Scopes"/> list (requirement-target rows).
/// </summary>
public sealed record SoaInputs(
    IReadOnlyList<AssetNode> Assets,
    IReadOnlyList<ScopeRow> Scopes,
    IReadOnlyList<RequirementRow> Requirements);

/// <summary>
/// The inputs the Statement of Applicability drill-down projection needs, read together in one
/// repeatable-read snapshot so they cannot straddle a concurrent importer commit. Extends the flat
/// <see cref="SoaInputs"/> with controls (resolved <c>maps_to</c>) and collectors so the
/// requirement -> control -> check hierarchy resolves from one consistent read; a collector's vendor id
/// maps to a title through the <c>Vendor</c>-typed rows of <see cref="Assets"/>. The requirement layer
/// comes from the one unified <see cref="Scopes"/> list.
/// </summary>
public sealed record SoaDrilldownInputs(
    IReadOnlyList<AssetNode> Assets,
    IReadOnlyList<ScopeRow> Scopes,
    IReadOnlyList<RequirementRow> Requirements,
    IReadOnlyList<ControlRow> Controls,
    IReadOnlyList<CollectorRow> Collectors);

/// <summary>
/// A persisted collector attached to one control - a data source or an attestation form. Identity is
/// <see cref="Id"/>. <see cref="Vendor"/>, <see cref="Provider"/>, and <see cref="Threshold"/> are null
/// when unset; <see cref="Config"/> is the type-specific payload, empty when the column is NULL.
/// <see cref="Connection"/> is the integration-connection
/// id (null unless this is an integration collector); it is sourced only to drive the startup
/// token-resolvability warning, not rendered on a read surface.
/// </summary>
public sealed record CollectorRow(
    string Id,
    string Title,
    string Control,
    string? Vendor,
    string Type,
    string? Provider,
    string Frequency,
    int? Threshold,
    CollectorConfigView Config,
    string? Connection = null);

/// <summary>
/// A persisted integration-connection. Identity is <see cref="Id"/>. A deliberate subset of the stored
/// columns: <c>api_version</c> and <c>title</c> are persisted but not surfaced, and no token state is
/// carried (token resolvability is composed at read time, never stored). <see cref="Vendor"/> is null
/// when unset.
/// </summary>
public sealed record IntegrationConnectionRow(
    string Id, string Provider, string BaseUrl, string DiscoveryCadence, string? Vendor);

/// <summary>
/// A quiz item as exposed on a read surface: prompt and option labels only. It deliberately has NO
/// answer property - the correct answer is a quiz secret redacted at the read-store boundary so no read
/// surface can leak it. A future grading runtime must read the answer through a separate privileged path.
/// Member order is pinned for the same reason <see cref="CollectorConfigView"/>'s is.
/// </summary>
public sealed record QuizItemView(
    [property: JsonPropertyOrder(1)] string Id,
    [property: JsonPropertyOrder(2)] string Prompt,
    [property: JsonPropertyOrder(3)] IReadOnlyList<string> Options);

/// <summary>
/// A collector's type-specific payload as exposed on a read surface. <see cref="Body"/> and
/// <see cref="PassMark"/> are null when unset and the three lists are empty when unset.
/// <see cref="Fields"/> and <see cref="Checks"/> reuse the Core value records; <see cref="Quiz"/> uses
/// the answer-free <see cref="QuizItemView"/>, and that absent answer IS the redaction boundary every
/// read surface inherits.
///
/// Member order is pinned explicitly because the schedule fingerprint serializes this record, is
/// persisted in the scheduler-state row, and is compared across process restarts and app upgrades - and
/// the serializer's reflection member order is documented as unspecified, so an order that changed
/// between builds would change the comparison. Nothing else serializes this record: the read endpoint
/// hand-writes its own snake_case projection. Pinning these five is not enough on its own, which is why
/// <see cref="QuizItemView"/> here and <see cref="AttestationField"/> and <see cref="Check"/> in Core are
/// pinned too - on a fingerprinted (integration) collector the nested <see cref="Check"/> items are the
/// only part of the hash input that varies.
/// </summary>
public sealed record CollectorConfigView(
    [property: JsonPropertyOrder(1)] string? Body,
    [property: JsonPropertyOrder(2)] IReadOnlyList<AttestationField> Fields,
    [property: JsonPropertyOrder(3)] int? PassMark,
    [property: JsonPropertyOrder(4)] IReadOnlyList<QuizItemView> Quiz,
    [property: JsonPropertyOrder(5)] IReadOnlyList<Check> Checks)
{
    /// <summary>The view a collector with no stored config reads as.</summary>
    public static CollectorConfigView Empty { get; } = new(null, [], null, [], []);
}

/// <summary>Per-kind row counts for the status summary.</summary>
public sealed record ComplianceCounts(
    int Standards,
    int Controls,
    int Requirements,
    int Organisations,
    int Scopes,
    int Vendors,
    int Collectors);
