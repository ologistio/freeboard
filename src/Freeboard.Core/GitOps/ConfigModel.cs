using System.Text.Json.Serialization;

namespace Freeboard.Core.GitOps;

/// <summary>
/// The only supported apiVersion for this increment.
/// </summary>
public static class GitOpsSchema
{
    public const string ApiVersion = "freeboard.dev/v1alpha1";

    public const string KindStandard = "Standard";
    public const string KindRequirement = "Requirement";
    public const string KindControl = "Control";
    public const string KindAsset = "Asset";
    public const string KindScope = "Scope";
    public const string KindCollector = "Collector";
    public const string KindIntegrationConnection = "Integration";
}

/// <summary>
/// A compliance standard in scope. Identity is <see cref="Id"/>; <see cref="Title"/> is display only.
/// <see cref="Version"/> and <see cref="Authority"/> are required; <see cref="Publisher"/> and
/// <see cref="SourceUrl"/> are optional (blank means absent).
/// </summary>
public sealed record Standard
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>The scheme version, e.g. "3.3". Required, non-empty.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>The body that owns the scheme. Required, non-empty.</summary>
    public string Authority { get; init; } = string.Empty;

    /// <summary>The delivery/certification body, distinct from the authority. Optional.</summary>
    public string Publisher { get; init; } = string.Empty;

    /// <summary>Absolute http/https URL of the official source. Optional.</summary>
    public string SourceUrl { get; init; } = string.Empty;
}

/// <summary>
/// A published normative statement belonging to exactly one <see cref="Standard"/>.
/// Identity is <see cref="Id"/>. <see cref="Title"/> is a short display label; <see cref="Statement"/>
/// is the full normative text. <see cref="Theme"/> is a free-form label grouping the standard's
/// requirements. The citation (<see cref="CitationLabel"/> + <see cref="CitationUrl"/>) points at the
/// published source.
/// </summary>
public sealed record Requirement
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>Owning standard id (single).</summary>
    public string Standard { get; init; } = string.Empty;

    /// <summary>Free-form theme label; required, non-empty.</summary>
    public string Theme { get; init; } = string.Empty;

    /// <summary>The full normative requirement text.</summary>
    public string Statement { get; init; } = string.Empty;

    /// <summary>Optional helper text; blank means absent.</summary>
    public string Guidance { get; init; } = string.Empty;

    /// <summary>Human label for the published source.</summary>
    public string CitationLabel { get; init; } = string.Empty;

    /// <summary>Absolute http/https link to the published source.</summary>
    public string CitationUrl { get; init; } = string.Empty;
}

/// <summary>
/// An implemented control mapped to one or more requirements. <see cref="MapsTo"/> lists
/// <see cref="Requirement"/> ids; a control's standard is derivable from each requirement's owner.
/// </summary>
public sealed record Control
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public List<string> MapsTo { get; init; } = [];

    /// <summary>
    /// Optional roll-up rule (<c>all</c>/<c>any</c>/<c>manual</c>) saying how the control's attached
    /// collectors combine into a status. Blank means absent; required once the control has at least one
    /// attached collector, of any type.
    /// </summary>
    public string Evaluation { get; init; } = string.Empty;
}

/// <summary>
/// A declared asset authored in gitops config - any type (Company, Department, Vendor, or Machine)
/// with <c>source: declared</c>. Identity is <see cref="Id"/>. <see cref="Type"/> is the asset type
/// authored under the YAML key <c>type</c> (distinct from the document discriminator
/// <see cref="Kind"/>). <see cref="Parent"/> and <see cref="Owner"/> are the two mutually-exclusive
/// edges: <see cref="Parent"/> is containment (a Company/Department id) and <see cref="Owner"/> is
/// accountability (a Company/Department id on a Vendor); both are empty when absent. Machine assets
/// are normally discovered (written by ingest), but a Machine may be declared; either way, no
/// declared document carries the discovered-only fields (identity, state, first/last seen) - those
/// live on the persistence asset row, and the loader rejects them if authored here.
/// </summary>
public sealed record Asset
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>Raw asset-type text authored under <c>type</c>; validation maps it to an asset kind.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Raw source text authored under <c>source</c>; only <c>declared</c> is valid in config.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Containment edge (a Company/Department id); empty when absent.</summary>
    public string Parent { get; init; } = string.Empty;

    /// <summary>Accountability edge (a Company/Department id); empty when absent.</summary>
    public string Owner { get; init; } = string.Empty;

    /// <summary>
    /// Raw tier text authored under <c>tier</c>, Vendor-only; validation maps it to a
    /// <see cref="Assets.VendorTier"/>. Empty when absent.
    /// </summary>
    public string Tier { get; init; } = string.Empty;

    /// <summary>
    /// Data class tokens authored under <c>data_classes</c>, Vendor-only, validated against
    /// <see cref="Assets.VendorDataClass.Tokens"/>. Order is not meaningful. An empty list and an absent
    /// key mean the same thing: nothing distinguishes "not assessed" from "assessed as holding nothing".
    /// </summary>
    public List<string> DataClasses { get; init; } = [];

    /// <summary>
    /// Certifications authored under <c>assurances</c>, Vendor-only. Empty when absent.
    /// </summary>
    public List<Assurance> Assurances { get; init; } = [];
}

/// <summary>
/// One certification a vendor holds, authored as an entry of <see cref="Asset.Assurances"/>. The pair
/// (asset, <see cref="Standard"/>) identifies it, so it carries no id. <see cref="Expires"/> and
/// <see cref="WarnDays"/> stay raw authored text, like <see cref="Asset.Tier"/> and
/// <see cref="Collector.Threshold"/>, so a malformed value surfaces as a validation diagnostic rather
/// than a YAML binding error. There is no status field: the state is derived from the expiry and the
/// clock.
/// </summary>
public sealed record Assurance
{
    /// <summary>Id of the <see cref="GitOps.Standard"/> the certificate is against (required).</summary>
    public string Standard { get; init; } = string.Empty;

    /// <summary>Raw expiry text authored under <c>expires</c>; validation parses it as <c>YYYY-MM-DD</c>.</summary>
    public string Expires { get; init; } = string.Empty;

    /// <summary>
    /// Raw warning-window override text authored under <c>warn_days</c>; blank means absent, in which case
    /// the deployment's configured window applies. Zero means no advance notice at all.
    /// </summary>
    public string WarnDays { get; init; } = string.Empty;
}

/// <summary>Whether a subject is in or out of scope for its target.</summary>
public enum ScopeDisposition
{
    In,
    Out,
}

/// <summary>
/// Maps one asset <see cref="Subject"/> to exactly one target - a <see cref="Standard"/>, a
/// <see cref="Requirement"/>, or a <see cref="Control"/> - with a <see cref="Disposition"/>. Identity is
/// <see cref="Id"/>. Exactly one of <see cref="Standard"/>/<see cref="Requirement"/>/<see cref="Control"/>
/// is set; the others are empty. <see cref="Justification"/> is required when <see cref="Disposition"/> is
/// <c>Out</c> and optional otherwise. At most one Scope exists per <c>(subject, standard)</c>,
/// <c>(subject, requirement)</c>, and <c>(subject, control)</c> pair. A Vendor subject cannot target a
/// standard (a vendor has no standard-level disposition). The subject reference is scalar: it may name any
/// asset id and a subject that resolves to no asset is a non-blocking warning, not an error.
/// </summary>
public sealed record Scope
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>Subject asset id.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Target Standard id, empty when the target is a requirement or control.</summary>
    public string Standard { get; init; } = string.Empty;

    /// <summary>Target Requirement id, empty when the target is a standard or control.</summary>
    public string Requirement { get; init; } = string.Empty;

    /// <summary>Target Control id, empty when the target is a standard or requirement.</summary>
    public string Control { get; init; } = string.Empty;

    /// <summary>Raw disposition text as authored; validation maps it to <see cref="ScopeDisposition"/>.</summary>
    public string Disposition { get; init; } = string.Empty;

    /// <summary>Exception rationale; required when the disposition is <c>Out</c>, else optional.</summary>
    public string Justification { get; init; } = string.Empty;
}

/// <summary>
/// A provider connection (for example a FleetDM instance): one base URL and a discovery cadence that
/// drive machine discovery and back many per-control collectors. Identity is <see cref="Id"/>.
/// <see cref="Provider"/> is a closed token (see <see cref="IntegrationProvider.Tokens"/>) selecting the
/// runner/adapter; it is not unique - one provider backs many connections. The API token is resolved
/// out-of-band by connection id: it is never a field here, never authored in git, and never persisted.
/// </summary>
public sealed record IntegrationConnection
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>Closed provider token selecting the runner/adapter; equals a machine's asset_source.source token.</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>Absolute http/https base URL of the provider instance.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Discovery cadence token (reuses the collector-frequency vocabulary).</summary>
    public string DiscoveryCadence { get; init; } = string.Empty;

    /// <summary>Optional Vendor id; blank means absent.</summary>
    public string Vendor { get; init; } = string.Empty;
}

/// <summary>
/// Attaches a proving mechanism - a data source or an attestation form - to one <see cref="Control"/>.
/// Identity is <see cref="Id"/>. A collector names its <see cref="Control"/> (the attach point) and,
/// optionally, a Vendor asset id. <see cref="Type"/> is one of a fixed token set;
/// <see cref="Frequency"/> is a collection cadence, required on every type because evidence ingest
/// stamps it onto each run it appends and staleness is judged from that stamp.
/// <see cref="Threshold"/> is carried as raw authored text (an integer percent 0..100) so a malformed
/// value surfaces as a clean validation diagnostic rather than a YAML binding error.
/// <see cref="Provider"/> and <see cref="Connection"/> are required for <c>type: integration</c> and
/// absent otherwise.
///
/// A top-level field is identity, the attach point, a cross-document reference, or the collection
/// cadence; every type-specific payload lives in <see cref="Config"/>, whose key set is closed by the
/// schema <see cref="CollectorConfigSchema"/> registers for the collector's
/// <c>(type, provider)</c> pair. It holds no secret material.
/// </summary>
public sealed record Collector
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>Attach-point Control id (required).</summary>
    public string Control { get; init; } = string.Empty;

    /// <summary>Optional Vendor id; blank means absent.</summary>
    public string Vendor { get; init; } = string.Empty;

    /// <summary>Collector type token (integration/script/agent/manual/training).</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Provider token selecting the adapter; required for <c>type: integration</c>, empty otherwise.</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>Collection cadence token (continuous/daily/weekly/monthly/quarterly/annual).</summary>
    public string Frequency { get; init; } = string.Empty;

    /// <summary>Raw authored threshold text; validation parses and range-checks it to an integer percent 0..100.</summary>
    public string Threshold { get; init; } = string.Empty;

    /// <summary>Integration connection id; required for <c>type: integration</c>, empty otherwise.</summary>
    public string Connection { get; init; } = string.Empty;

    /// <summary>Type-specific payload; empty when absent.</summary>
    public CollectorConfig Config { get; init; } = new();
}

/// <summary>
/// A <see cref="Collector"/>'s type-specific payload. The record is a union across every type: it can
/// HOLD whatever any pair may author, while <see cref="CollectorConfigSchema"/> is the authority on
/// what each <c>(type, provider)</c> pair may legally author. <see cref="Body"/> is optional markdown
/// carried verbatim. <see cref="PassMark"/> is raw authored text (an integer percent 0..100), parsed at
/// import like <see cref="Collector.Threshold"/>, so a malformed value is a clean validation diagnostic
/// rather than a YAML binding error.
/// </summary>
public sealed record CollectorConfig
{
    /// <summary>Optional markdown body; blank means absent.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Optional ordered form fields; empty when absent.</summary>
    public List<AttestationField> Fields { get; init; } = [];

    /// <summary>Raw authored pass-mark text; blank means absent.</summary>
    public string PassMark { get; init; } = string.Empty;

    /// <summary>Optional ordered quiz items; empty when absent.</summary>
    public List<QuizItem> Quiz { get; init; } = [];

    /// <summary>Ordered tracked checks; empty when absent.</summary>
    public List<Check> Checks { get; init; } = [];
}

/// <summary>
/// One tracked check in an integration collector's <see cref="CollectorConfig.Checks"/>.
/// <see cref="SourceKey"/> is the provider-native id (a Fleet policy id) that joins a provider result to
/// this check; a provider result whose id is not authored here is not a tracked check.
/// <see cref="Name"/> is the Freeboard check name (as <c>evidence_checks.name</c> carries).
/// <see cref="Severity"/> is <c>Hard</c> or <c>Soft</c> (as <c>evidence_checks.severity</c> stores):
/// <c>Hard</c> fails the requirement, <c>Soft</c> warns.
///
/// These member NAMES are the persisted <c>collectors.config</c> column's <c>Checks</c> item key
/// literals, so renaming one is a database migration. Their ORDER is pinned because the same records are
/// serialized into the schedule fingerprint, which is persisted and compared across app upgrades, and
/// the serializer's reflection member order is documented as unspecified.
/// </summary>
public sealed record Check
{
    [JsonPropertyOrder(1)]
    public string SourceKey { get; init; } = string.Empty;

    [JsonPropertyOrder(2)]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyOrder(3)]
    public string Severity { get; init; } = string.Empty;
}

/// <summary>
/// A single form field in a collector's <see cref="CollectorConfig.Fields"/>. <see cref="Type"/> is one
/// of a fixed token set (boolean/single-choice/short-text); <see cref="Options"/> carries the choice
/// labels and is meaningful only for a single-choice field.
///
/// Member names and order carry the same weight as <see cref="Check"/>'s, for the same two reasons.
/// </summary>
public sealed record AttestationField
{
    [JsonPropertyOrder(1)]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyOrder(2)]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyOrder(3)]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyOrder(4)]
    public List<string> Options { get; init; } = [];
}

/// <summary>
/// A single quiz item in a training collector's <see cref="CollectorConfig.Quiz"/>.
/// <see cref="Answer"/> is the correct option label; it is persisted for grading but redacted from every
/// read surface.
/// </summary>
public sealed record QuizItem
{
    public string Id { get; init; } = string.Empty;
    public string Prompt { get; init; } = string.Empty;
    public List<string> Options { get; init; } = [];
    public string Answer { get; init; } = string.Empty;
}

/// <summary>
/// The aggregate config model loaded from a directory.
/// </summary>
public sealed record GitOpsConfig
{
    public List<Standard> Standards { get; init; } = [];
    public List<Requirement> Requirements { get; init; } = [];
    public List<Control> Controls { get; init; } = [];
    public List<Asset> Assets { get; init; } = [];
    public List<Scope> Scopes { get; init; } = [];
    public List<Collector> Collectors { get; init; } = [];
    public List<IntegrationConnection> IntegrationConnections { get; init; } = [];
}
