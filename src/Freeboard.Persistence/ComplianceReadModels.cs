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
/// <see cref="Tier"/> and <see cref="DataClasses"/> are the Vendor-only risk profile and read empty on
/// every other type.
/// </summary>
public sealed record AssetNode(
    string Id, string Title, string Type, string Source, string? State, string? Parent, string? Owner)
{
    /// <summary>The vendor's tier (<c>Critical</c>/<c>High</c>/<c>Medium</c>/<c>Low</c>), null when unset.</summary>
    public string? Tier { get; init; }

    /// <summary>
    /// The regulated data the vendor holds. Empty means the vendor holds none: nothing distinguishes
    /// "not assessed" from "assessed as holding nothing", so a null column reads as empty.
    /// </summary>
    public IReadOnlyList<string> DataClasses { get; init; } = [];

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
/// One persisted certification a vendor holds. Identity is the pair (<see cref="VendorId"/>,
/// <see cref="StandardId"/>). <see cref="WarnDays"/> is the per-row warning-window override and is null
/// when the row carries none, which means "use the deployment's configured window" rather than "no
/// window". There is no status column: the state is derived from <see cref="Expires"/> and the clock.
/// </summary>
public sealed record VendorAssuranceRow(string VendorId, string StandardId, DateOnly Expires, int? WarnDays);

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

/// <summary>
/// The lists a compliance read can name. A caller names the ones its decision needs and receives them
/// in one <see cref="ComplianceSnapshot"/>.
/// </summary>
[Flags]
public enum ComplianceReadSet
{
    /// <summary>No list.</summary>
    None = 0,

    /// <summary>
    /// Every asset of every type, unfiltered. Resolution, read-access, and the live-subject predicate
    /// each need a different subset, so type and retirement are applied by the caller.
    /// </summary>
    Assets = 1 << 0,

    /// <summary>The standards.</summary>
    Standards = 1 << 1,

    /// <summary>The requirements, each with its resolved owning standard.</summary>
    Requirements = 1 << 2,

    /// <summary>The controls, each with its resolved <c>maps_to</c> requirement ids.</summary>
    Controls = 1 << 3,

    /// <summary>The unified scopes: subject, one target, disposition, justification.</summary>
    Scopes = 1 << 4,

    /// <summary>The collectors.</summary>
    Collectors = 1 << 5,

    /// <summary>The integration connections.</summary>
    IntegrationConnections = 1 << 6,

    /// <summary>The whole vendor assurance set, ordered by vendor id then standard id.</summary>
    VendorAssurances = 1 << 7,
}

/// <summary>
/// The lists one read asked for, read together. <see cref="Sets"/> is the shape the snapshot was read
/// with AND the contract it serves, so a caller and a test can both see which lists a decision drew on.
/// Reading a list the snapshot does not name throws <see cref="ComplianceReadSetNotRequestedException"/>
/// rather than reading as empty: an empty list is a plausible answer, so returning one would render a
/// programming error as an ordinary page.
/// </summary>
// A class rather than a record: nothing copies or compares a snapshot, and a record's generated
// ToString reads every property, so printing a partial snapshot - in a log line, a debugger, or an
// assertion failure - would throw the not-requested exception over whatever was being diagnosed.
public sealed class ComplianceSnapshot
{
    private readonly IReadOnlyList<AssetNode>? _assets;
    private readonly IReadOnlyList<StandardRow>? _standards;
    private readonly IReadOnlyList<RequirementRow>? _requirements;
    private readonly IReadOnlyList<ControlRow>? _controls;
    private readonly IReadOnlyList<ScopeRow>? _scopes;
    private readonly IReadOnlyList<CollectorRow>? _collectors;
    private readonly IReadOnlyList<IntegrationConnectionRow>? _integrationConnections;
    private readonly IReadOnlyList<VendorAssuranceRow>? _vendorAssurances;

    public ComplianceSnapshot(
        ComplianceReadSet sets,
        IReadOnlyList<AssetNode>? assets = null,
        IReadOnlyList<StandardRow>? standards = null,
        IReadOnlyList<RequirementRow>? requirements = null,
        IReadOnlyList<ControlRow>? controls = null,
        IReadOnlyList<ScopeRow>? scopes = null,
        IReadOnlyList<CollectorRow>? collectors = null,
        IReadOnlyList<IntegrationConnectionRow>? integrationConnections = null,
        IReadOnlyList<VendorAssuranceRow>? vendorAssurances = null)
    {
        Sets = sets;
        _assets = assets;
        _standards = standards;
        _requirements = requirements;
        _controls = controls;
        _scopes = scopes;
        _collectors = collectors;
        _integrationConnections = integrationConnections;
        _vendorAssurances = vendorAssurances;
    }

    /// <summary>The lists this snapshot was read with.</summary>
    public ComplianceReadSet Sets { get; }

    public IReadOnlyList<AssetNode> Assets => Requested(_assets, ComplianceReadSet.Assets);

    public IReadOnlyList<StandardRow> Standards => Requested(_standards, ComplianceReadSet.Standards);

    public IReadOnlyList<RequirementRow> Requirements => Requested(_requirements, ComplianceReadSet.Requirements);

    public IReadOnlyList<ControlRow> Controls => Requested(_controls, ComplianceReadSet.Controls);

    public IReadOnlyList<ScopeRow> Scopes => Requested(_scopes, ComplianceReadSet.Scopes);

    public IReadOnlyList<CollectorRow> Collectors => Requested(_collectors, ComplianceReadSet.Collectors);

    public IReadOnlyList<IntegrationConnectionRow> IntegrationConnections =>
        Requested(_integrationConnections, ComplianceReadSet.IntegrationConnections);

    public IReadOnlyList<VendorAssuranceRow> VendorAssurances =>
        Requested(_vendorAssurances, ComplianceReadSet.VendorAssurances);

    // Sets is the contract: a list the snapshot did not name is refused even when one was supplied, so a
    // snapshot cannot serve a set it never declared and a structural assertion on Sets cannot be evaded.
    // A named set with no list is the store's own bug and fails the same way rather than reading as null.
    private IReadOnlyList<T> Requested<T>(IReadOnlyList<T>? list, ComplianceReadSet set)
        => Sets.HasFlag(set) && list is not null
            ? list
            : throw new ComplianceReadSetNotRequestedException(set);
}

/// <summary>
/// Thrown when a caller reads a list its snapshot did not name. Deliberately NOT an
/// <see cref="InvalidOperationException"/>: the read paths turn that type into "compliance store
/// unreachable", and a programming error must not be reported to an operator as a database outage.
/// </summary>
public sealed class ComplianceReadSetNotRequestedException(ComplianceReadSet set)
    : Exception($"The compliance snapshot was not read with {set}.")
{
    /// <summary>The list the caller read.</summary>
    public ComplianceReadSet Set { get; } = set;
}
