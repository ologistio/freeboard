namespace Freeboard.Persistence;

/// <summary>
/// The outcome of an app-managed write. <see cref="WriteError"/> is null on success;
/// otherwise it names the invariant that was violated so the caller can return a problem
/// response. Writes that fail an invariant do not modify the store.
/// </summary>
public sealed record WriteResult(string? Error)
{
    public static readonly WriteResult Success = new((string?)null);

    public bool Ok => Error is null;

    public static WriteResult Fail(string error) => new(error);
}

/// <summary>
/// App-managed create/update/delete of organisations, scope dispositions, and requirement-scope
/// dispositions over the same store the read path uses. Active only when the instance is not in
/// GitOps read-only mode. The implementation enforces the same domain invariants as import:
/// organisation kind in <c>Company</c>/<c>Department</c>, acyclic resolvable parents, references
/// that resolve, disposition in <c>In</c>/<c>Out</c>, at most one scope per
/// <c>(organisation, standard)</c> pair, and at most one requirement-scope per
/// <c>(organisation, requirement)</c> pair. An invalid write returns a failing
/// <see cref="WriteResult"/> and does not modify the store.
/// </summary>
public interface IComplianceWriteStore
{
    /// <summary>Creates or updates an organisation node keyed on <paramref name="id"/>.</summary>
    Task<WriteResult> UpsertOrganisationAsync(
        string id,
        string title,
        string kind,
        string? parent,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an organisation. Fails if it still has children or scopes.</summary>
    Task<WriteResult> DeleteOrganisationAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates the scope disposition for an <c>(organisation, standard)</c> pair.
    /// <paramref name="id"/> is the scope's own identity. Fails if a different scope already
    /// maps the same pair.
    /// </summary>
    Task<WriteResult> UpsertScopeDispositionAsync(
        string id,
        string title,
        string organisation,
        string standard,
        string disposition,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a scope disposition by its id.</summary>
    Task<WriteResult> DeleteScopeAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates the requirement-scope disposition for an <c>(organisation, requirement)</c>
    /// pair. <paramref name="id"/> is the requirement-scope's own identity. Fails if a different
    /// requirement-scope already maps the same pair.
    /// </summary>
    Task<WriteResult> UpsertRequirementScopeDispositionAsync(
        string id,
        string title,
        string organisation,
        string requirement,
        string disposition,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a requirement-scope disposition by its id.</summary>
    Task<WriteResult> DeleteRequirementScopeAsync(string id, CancellationToken cancellationToken = default);
}
