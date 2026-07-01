namespace Freeboard.Persistence;

/// <summary>A persisted standard. Identity is <see cref="Id"/>.</summary>
public sealed record StandardRow(string Id, string Title);

/// <summary>A persisted control with its resolved <see cref="MapsTo"/> standard ids.</summary>
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

/// <summary>Per-kind row counts for the status summary.</summary>
public sealed record ComplianceCounts(int Standards, int Controls, int Organisations, int Scopes);
