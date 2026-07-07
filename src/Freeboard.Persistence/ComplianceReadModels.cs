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

/// <summary>A persisted control with its resolved <see cref="MapsTo"/> requirement ids.</summary>
public sealed record ControlRow(string Id, string Title, IReadOnlyList<string> MapsTo);

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

/// <summary>A persisted vendor (a piece of software or platform in use).</summary>
public sealed record VendorRow(string Id, string Title);

/// <summary>
/// A persisted vendor-scope binding one vendor to exactly one target (a requirement or a control,
/// the other null) with a disposition (<c>In</c> or <c>Out</c>). <see cref="Justification"/> is null
/// when unset; it is always present for an <c>Out</c> exception.
/// </summary>
public sealed record VendorScopeRow(
    string Id, string Title, string Vendor, string? Requirement, string? Control, string Disposition, string? Justification);

/// <summary>Per-kind row counts for the status summary.</summary>
public sealed record ComplianceCounts(
    int Standards,
    int Controls,
    int Requirements,
    int Organisations,
    int Scopes,
    int RequirementScopes,
    int Vendors,
    int VendorScopes);
