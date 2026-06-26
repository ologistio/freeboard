namespace Freeboard.Persistence;

/// <summary>A persisted standard. Identity is <see cref="Id"/>.</summary>
public sealed record StandardRow(string Id, string Title);

/// <summary>A persisted control with its resolved <see cref="MapsTo"/> standard ids.</summary>
public sealed record ControlRow(string Id, string Title, IReadOnlyList<string> MapsTo);

/// <summary>A persisted scope with its resolved <see cref="Controls"/> control ids.</summary>
public sealed record ScopeRow(string Id, string Title, IReadOnlyList<string> Controls);

/// <summary>Per-kind row counts for the status summary.</summary>
public sealed record ComplianceCounts(int Standards, int Controls, int Scopes);
