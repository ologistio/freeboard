namespace Freeboard.Persistence.System;

/// <summary>A recorded applied migration as read from the tracking table.</summary>
public sealed record AppliedMigration(string Version, string Checksum);

/// <summary>
/// Pure migration classification: given the embedded migrations and what the database
/// records as applied, decide applied vs pending and detect forward-only violations.
/// No database access, so it is unit testable without MySQL.
/// </summary>
public static class MigrationPlanner
{
    /// <summary>
    /// Classifies embedded migrations into applied and pending versions and, by a pure
    /// read, surfaces any forward-only integrity violation in
    /// <see cref="MigrationState.IntegrityError"/>: a recorded applied version whose
    /// embedded stem is missing (deleted or renamed), or an applied migration whose
    /// checksum no longer matches its embedded file. No DDL and no writes; this is the
    /// read-only counterpart of <see cref="Validate"/> and uses the same rules.
    /// </summary>
    public static MigrationState Classify(
        IReadOnlyList<EmbeddedMigration> embedded,
        IReadOnlyList<AppliedMigration> applied)
    {
        var appliedVersions = new HashSet<string>(applied.Select(a => a.Version), StringComparer.Ordinal);

        var appliedList = new List<string>();
        var pendingList = new List<string>();
        foreach (var migration in embedded)
        {
            if (appliedVersions.Contains(migration.Version))
            {
                appliedList.Add(migration.Version);
            }
            else
            {
                pendingList.Add(migration.Version);
            }
        }

        return new MigrationState(appliedList, pendingList, DetectIntegrityError(embedded, applied));
    }

    /// <summary>
    /// Validates forward-only integrity before applying any migration. Throws
    /// <see cref="MigrationException"/> on: (a) a recorded applied version with no
    /// matching embedded stem (deleted or renamed); (b) a present-but-edited migration
    /// (checksum mismatch).
    /// </summary>
    public static void Validate(
        IReadOnlyList<EmbeddedMigration> embedded,
        IReadOnlyList<AppliedMigration> applied)
    {
        var error = DetectIntegrityError(embedded, applied);
        if (error is not null)
        {
            throw new MigrationException(error);
        }
    }

    /// <summary>
    /// Pure integrity check shared by <see cref="Classify"/> (read path) and
    /// <see cref="Validate"/> (apply path). Returns a message describing the first
    /// violation found, or null when the recorded state is consistent. Performs no I/O.
    /// </summary>
    private static string? DetectIntegrityError(
        IReadOnlyList<EmbeddedMigration> embedded,
        IReadOnlyList<AppliedMigration> applied)
    {
        var embeddedByVersion = embedded.ToDictionary(m => m.Version, StringComparer.Ordinal);

        foreach (var record in applied)
        {
            if (!embeddedByVersion.TryGetValue(record.Version, out var migration))
            {
                return $"Applied migration '{record.Version}' is recorded in the database but its embedded "
                    + "migration is missing (deleted or renamed). Migrations are forward-only.";
            }

            if (!string.Equals(migration.Checksum, record.Checksum, StringComparison.Ordinal))
            {
                return $"Applied migration '{record.Version}' has a different checksum than its embedded "
                    + "migration (edited after being applied). Migrations are forward-only.";
            }
        }

        return null;
    }
}
