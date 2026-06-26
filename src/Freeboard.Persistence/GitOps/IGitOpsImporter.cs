using Freeboard.Core.GitOps;

namespace Freeboard.Persistence.GitOps;

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
    Task ImportAsync(GitOpsConfig config, CancellationToken cancellationToken = default);
}
