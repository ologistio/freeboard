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
    public const string KindOrganisation = "Organisation";
    public const string KindScope = "Scope";
    public const string KindRequirementScope = "RequirementScope";
    public const string KindVendor = "Vendor";
    public const string KindVendorScope = "VendorScope";
    public const string KindEvidenceCollector = "EvidenceCollector";
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
    /// evidence-collectors combine into a status. Blank means absent; required only when the control
    /// has at least one attached collector.
    /// </summary>
    public string Evaluation { get; init; } = string.Empty;
}

/// <summary>What an organisation node represents in the tree.</summary>
public enum OrganisationKind
{
    Company,
    Department,
}

/// <summary>
/// A node in the organisation tree being assessed. Identity is <see cref="Id"/>.
/// <see cref="Parent"/> is the id of another organisation, empty for a root.
/// </summary>
public sealed record Organisation
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Raw Company/Department text as authored under the YAML key <c>type</c>; validation maps it
    /// to <see cref="OrganisationKind"/>. This is the organisation's kind, distinct from the
    /// document discriminator <see cref="Kind"/>.
    /// </summary>
    public string OrgKind { get; init; } = string.Empty;

    /// <summary>Parent organisation id, empty for a root.</summary>
    public string Parent { get; init; } = string.Empty;
}

/// <summary>Whether an organisation is in or out of scope for a standard.</summary>
public enum ScopeDisposition
{
    In,
    Out,
}

/// <summary>
/// Maps one <see cref="Organisation"/> to one <see cref="Standard"/> with a
/// <see cref="Disposition"/>. Identity is <see cref="Id"/>; at most one Scope exists
/// per <c>(organisation, standard)</c> pair.
/// </summary>
public sealed record Scope
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Organisation { get; init; } = string.Empty;
    public string Standard { get; init; } = string.Empty;

    /// <summary>Raw disposition text as authored; validation maps it to <see cref="ScopeDisposition"/>.</summary>
    public string Disposition { get; init; } = string.Empty;
}

/// <summary>
/// Maps one <see cref="Organisation"/> to one <see cref="Requirement"/> with a
/// <see cref="Disposition"/>. Identity is <see cref="Id"/>; at most one RequirementScope
/// exists per <c>(organisation, requirement)</c> pair. The owning standard is derived from
/// the requirement, so there is no <c>standard</c> field. Resolved under the standard-level
/// <see cref="Scope"/>: it applies only where the requirement's standard resolves <c>In</c>.
/// </summary>
public sealed record RequirementScope
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Organisation { get; init; } = string.Empty;
    public string Requirement { get; init; } = string.Empty;

    /// <summary>Raw disposition text as authored; validation maps it to <see cref="ScopeDisposition"/>.</summary>
    public string Disposition { get; init; } = string.Empty;
}

/// <summary>
/// A piece of software or a platform in use (for example Crowdstrike, FleetDM, Google Workspace,
/// an outsourced accountant). Identity is <see cref="Id"/>; <see cref="Title"/> is display only.
/// Carries no other required fields in this increment.
/// </summary>
public sealed record Vendor
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
}

/// <summary>
/// Records whether one <see cref="Requirement"/> or one <see cref="Control"/> applies to one
/// <see cref="Vendor"/>, with an exception rationale. Identity is <see cref="Id"/>. Exactly one of
/// <see cref="Requirement"/> or <see cref="Control"/> is set (the target); the other is empty.
/// <see cref="Disposition"/> reuses the <see cref="Scope"/> disposition (<c>In</c>/<c>Out</c>):
/// <c>In</c> means the target applies to the vendor, <c>Out</c> means it is excepted. A
/// <see cref="VendorScope"/> is a flat per-<c>(vendor, target)</c> statement with no organisation
/// dimension. <see cref="Justification"/> is required when <see cref="Disposition"/> is <c>Out</c>
/// and optional otherwise. At most one exists per <c>(vendor, requirement)</c> and per
/// <c>(vendor, control)</c> pair.
/// </summary>
public sealed record VendorScope
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Vendor { get; init; } = string.Empty;

    /// <summary>Target Requirement id, empty when the target is a control.</summary>
    public string Requirement { get; init; } = string.Empty;

    /// <summary>Target Control id, empty when the target is a requirement.</summary>
    public string Control { get; init; } = string.Empty;

    /// <summary>Raw disposition text as authored; validation maps it to <see cref="ScopeDisposition"/>.</summary>
    public string Disposition { get; init; } = string.Empty;

    /// <summary>Exception rationale; required when the disposition is <c>Out</c>, else optional.</summary>
    public string Justification { get; init; } = string.Empty;
}

/// <summary>
/// Attaches a data source to one <see cref="Control"/>. Identity is <see cref="Id"/>. A collector
/// names its <see cref="Control"/> (the attach point) and, optionally, a <see cref="Vendor"/>.
/// <see cref="Type"/> is one of a fixed token set; <see cref="Frequency"/> is a collection cadence.
/// <see cref="Threshold"/> is carried as raw authored text (an integer percent 0..100) so a malformed
/// value surfaces as a clean validation diagnostic rather than a YAML binding error. <see cref="Config"/>
/// is a free-form type-specific settings map; it holds no secret material.
/// </summary>
public sealed record EvidenceCollector
{
    public string ApiVersion { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>Attach-point Control id (required).</summary>
    public string Control { get; init; } = string.Empty;

    /// <summary>Optional Vendor id; blank means absent.</summary>
    public string Vendor { get; init; } = string.Empty;

    /// <summary>Collector type token (integration/script/manual-attestation/training-attestation/agent).</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Collection cadence token (continuous/daily/weekly/monthly/quarterly/annual).</summary>
    public string Frequency { get; init; } = string.Empty;

    /// <summary>Raw authored threshold text; validation parses and range-checks it to an integer percent 0..100.</summary>
    public string Threshold { get; init; } = string.Empty;

    /// <summary>Free-form type-specific settings; empty when absent.</summary>
    public Dictionary<string, string> Config { get; init; } = [];
}

/// <summary>
/// The aggregate config model loaded from a directory.
/// </summary>
public sealed record GitOpsConfig
{
    public List<Standard> Standards { get; init; } = [];
    public List<Requirement> Requirements { get; init; } = [];
    public List<Control> Controls { get; init; } = [];
    public List<Organisation> Organisations { get; init; } = [];
    public List<Scope> Scopes { get; init; } = [];
    public List<RequirementScope> RequirementScopes { get; init; } = [];
    public List<Vendor> Vendors { get; init; } = [];
    public List<VendorScope> VendorScopes { get; init; } = [];
    public List<EvidenceCollector> EvidenceCollectors { get; init; } = [];
}
