using Freeboard.Core.GitOps;

namespace Freeboard.Persistence.GitOps;

/// <summary>
/// The outcome of an import. <see cref="UnresolvedScopeSubjects"/> is the DB-accurate set of scope
/// subject ids that do not resolve to a live asset (no asset row, or a retired discovered asset) after all
/// writes, computed inside the
/// import transaction before commit. Distinct from the DB-less Core scope-subject warning: only the DB sees
/// discovered and retired Machine subjects, so this is the authoritative sync-path signal.
/// </summary>
public sealed record ImportResult(IReadOnlyList<string> UnresolvedScopeSubjects)
{
    public static readonly ImportResult Empty = new([]);
}

/// <summary>
/// Imports a git-sourced <see cref="GitOpsConfig"/> into the general compliance
/// store. One writer into the store.
/// </summary>
/// <remarks>
/// Precondition: the config is already validated by the caller. The importer does
/// NOT re-run Core validation. It runs in one DML transaction and replaces the whole
/// persisted set: upsert all domain rows by id, replace all cross-ref join rows, then
/// hard-remove domain rows whose id is absent from the config.
/// </remarks>
public interface IGitOpsImporter
{
    Task<ImportResult> ImportAsync(GitOpsConfig config, CancellationToken cancellationToken = default);
}
