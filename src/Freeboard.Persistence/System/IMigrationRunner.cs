namespace Freeboard.Persistence.System;

/// <summary>
/// The current/pending split of the embedded migrations against what the database
/// records as applied, plus any forward-only integrity violation detected by a pure
/// read (a checksum mismatch of an applied migration, or a recorded applied version
/// whose embedded stem is missing). <see cref="IntegrityError"/> is null when the
/// recorded state is consistent with the embedded set.
/// </summary>
public sealed record MigrationState(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Pending,
    string? IntegrityError = null)
{
    /// <summary>True when no embedded migration is pending.</summary>
    public bool IsCurrent => Pending.Count == 0;

    /// <summary>
    /// True when a forward-only integrity violation was detected on the read path: a
    /// recorded applied version whose embedded stem is missing (deleted or renamed),
    /// or an applied migration whose checksum no longer matches its embedded file.
    /// Reported without any DDL or writes; an integrity-violated schema must never be
    /// imported into.
    /// </summary>
    public bool IsCorrupt => IntegrityError is not null;
}

/// <summary>
/// Applies forward-only schema migrations and reports current/pending state.
/// Migrations are a system/platform concern. The schema-tracking table is
/// bootstrapped only in <see cref="ApplyPendingAsync"/>; <see cref="GetStateAsync"/>
/// is strictly read-only and performs no DDL or writes.
/// </summary>
public interface IMigrationRunner
{
    /// <summary>
    /// Reports applied vs pending migration versions, and surfaces any forward-only
    /// integrity violation in <see cref="MigrationState.IntegrityError"/> (a checksum
    /// mismatch of an applied migration, or a recorded applied version whose embedded
    /// stem is missing). Strictly read-only: performs no DDL and no writes. On a fresh
    /// database without the tracking table it reports every embedded migration pending,
    /// with no integrity violation, without creating the table.
    /// </summary>
    Task<MigrationState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the pending migrations and returns the versions applied. Bootstraps the
    /// tracking table as its first step. Forward-only; no down migrations. Fails loudly
    /// on a changed checksum of an applied migration, or on a recorded applied version
    /// whose embedded migration is missing.
    /// </summary>
    Task<IReadOnlyList<string>> ApplyPendingAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised when the migration runner detects a forward-only integrity violation or a
/// failed migration. Surfaced by the CLI as exit code 3.
/// </summary>
public sealed class MigrationException(string message) : Exception(message);
