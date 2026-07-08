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
/// A persisted organisation node. <see cref="Kind"/> is <c>Company</c> or <c>Department</c>;
/// <see cref="Parent"/> is the parent organisation id, null for a root.
/// </summary>
public sealed record OrganisationRow(string Id, string Title, string Kind, string? Parent);

/// <summary>
/// A persisted scope mapping one organisation to one standard with a disposition
/// (<c>In</c> or <c>Out</c>).
/// </summary>
public sealed record ScopeRow(string Id, string Title, string Organisation, string Standard, string Disposition);

/// <summary>
/// A persisted requirement-scope mapping one organisation to one requirement with a disposition
/// (<c>In</c> or <c>Out</c>). The owning standard is derived from the requirement.
/// </summary>
public sealed record RequirementScopeRow(string Id, string Title, string Organisation, string Requirement, string Disposition);

/// <summary>
/// The four inputs the Statement of Applicability projection needs, read together in one
/// repeatable-read snapshot so they cannot straddle a concurrent importer commit.
/// </summary>
public sealed record SoaInputs(
    IReadOnlyList<OrganisationRow> Organisations,
    IReadOnlyList<ScopeRow> Scopes,
    IReadOnlyList<RequirementRow> Requirements,
    IReadOnlyList<RequirementScopeRow> RequirementScopes);

/// <summary>
/// The inputs the Statement of Applicability drill-down projection needs, read together in one
/// repeatable-read snapshot so they cannot straddle a concurrent importer commit. Extends the flat
/// <see cref="SoaInputs"/> with controls (resolved <c>maps_to</c>), evidence-collectors,
/// attestation-templates, and vendors so the requirement -> control -> check hierarchy resolves from one
/// consistent read and a collector's vendor id maps to a vendor title.
/// </summary>
public sealed record SoaDrilldownInputs(
    IReadOnlyList<OrganisationRow> Organisations,
    IReadOnlyList<ScopeRow> Scopes,
    IReadOnlyList<RequirementRow> Requirements,
    IReadOnlyList<RequirementScopeRow> RequirementScopes,
    IReadOnlyList<ControlRow> Controls,
    IReadOnlyList<EvidenceCollectorRow> Collectors,
    IReadOnlyList<AttestationTemplateRow> Templates,
    IReadOnlyList<VendorRow> Vendors);

/// <summary>A persisted vendor (a piece of software or platform in use).</summary>
public sealed record VendorRow(string Id, string Title);

/// <summary>
/// A persisted vendor-scope binding one vendor to exactly one target (a requirement or a control,
/// the other null) with a disposition (<c>In</c> or <c>Out</c>). <see cref="Justification"/> is null
/// when unset; it is always present for an <c>Out</c> exception.
/// </summary>
public sealed record VendorScopeRow(
    string Id, string Title, string Vendor, string? Requirement, string? Control, string Disposition, string? Justification);

/// <summary>
/// A persisted evidence-collector attached to one control. Identity is <see cref="Id"/>.
/// <see cref="Vendor"/> and <see cref="Threshold"/> are null when unset; <see cref="Config"/> is the
/// type-specific settings map (empty when unset).
/// </summary>
public sealed record EvidenceCollectorRow(
    string Id,
    string Title,
    string Control,
    string? Vendor,
    string Type,
    string Frequency,
    int? Threshold,
    IReadOnlyDictionary<string, string> Config);

/// <summary>
/// A quiz item as exposed on a read surface: prompt and option labels only. It deliberately has NO
/// answer property - the correct answer is a quiz secret redacted at the read-store boundary so no read
/// surface can leak it. A future grading runtime must read the answer through a separate privileged path.
/// </summary>
public sealed record QuizItemView(string Id, string Prompt, IReadOnlyList<string> Options);

/// <summary>
/// A persisted attestation-template attached to one control. Identity is <see cref="Id"/>.
/// <see cref="Body"/> and <see cref="PassMark"/> are null when unset. <see cref="Fields"/> reuses the
/// Core <see cref="AttestationField"/> value record; <see cref="Quiz"/> uses the answer-free
/// <see cref="QuizItemView"/> so the correct answer never reaches a read surface.
/// </summary>
public sealed record AttestationTemplateRow(
    string Id,
    string Title,
    string Control,
    string Type,
    string? Body,
    IReadOnlyList<AttestationField> Fields,
    int? PassMark,
    IReadOnlyList<QuizItemView> Quiz);

/// <summary>Per-kind row counts for the status summary.</summary>
public sealed record ComplianceCounts(
    int Standards,
    int Controls,
    int Requirements,
    int Organisations,
    int Scopes,
    int RequirementScopes,
    int Vendors,
    int VendorScopes,
    int EvidenceCollectors,
    int AttestationTemplates);
