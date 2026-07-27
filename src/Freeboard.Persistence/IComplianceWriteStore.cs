namespace Freeboard.Persistence;

/// <summary>
/// The outcome of an app-managed write. <see cref="Error"/> is null on success; otherwise it names the
/// invariant that was violated so the caller can return a problem response. <see cref="IsConflict"/>
/// distinguishes a concurrency conflict (map to 409) from an ordinary validation failure (422);
/// <see cref="IsNotFound"/> distinguishes a target the route cannot address (map to 404) - a PUT whose id
/// names an existing row of a different target kind, or a DELETE that affects no row. Writes that fail an
/// invariant do not modify the store.
/// </summary>
public sealed record WriteResult(string? Error, bool IsConflict = false, bool IsNotFound = false)
{
    public static readonly WriteResult Success = new((string?)null);

    public bool Ok => Error is null;

    public static WriteResult Fail(string error) => new(error);

    /// <summary>A concurrency conflict (e.g. the row's owning organisation changed under the lock).</summary>
    public static WriteResult Conflict(string error) => new(error, true);

    /// <summary>The route cannot address the id (a wrong-target-kind PUT, or a DELETE affecting no row).</summary>
    public static WriteResult NotFound() => new("Not found.", IsNotFound: true);
}

/// <summary>
/// App-managed create/update/delete of organisations and scope dispositions over the same store the read
/// path uses. Active only when the instance is not in GitOps read-only mode. The implementation enforces
/// the same domain invariants as import: organisation kind in <c>Company</c>/<c>Department</c>, acyclic
/// resolvable parents, references that resolve, disposition in <c>In</c>/<c>Out</c>, a non-blank
/// justification on an <c>Out</c>, at most one scope per <c>(subject, standard)</c> pair, and at most one
/// per <c>(subject, requirement)</c> pair. The two app-managed disposition routes each write the unified
/// <c>scopes</c> table confined to their own target column, so an id cannot cross the target boundary: a
/// PUT whose id names an existing row of a different target kind, or a DELETE that finds no row of the
/// route's target kind, returns a not-found <see cref="WriteResult"/>. An invalid write returns a failing
/// <see cref="WriteResult"/> and does not modify the store.
/// </summary>
public interface IComplianceWriteStore
{
    /// <summary>
    /// Creates or updates an organisation node keyed on <paramref name="id"/>. A reparent authorizes
    /// the current parent before the write, so the store locks the existing row and returns a conflict
    /// if its current parent no longer matches <paramref name="expectedCurrentParent"/>, closing the
    /// same cross-parent-move race the scope upsert closes. <paramref name="expectExisting"/> tells the
    /// store whether the caller authorized an update (row must still exist with the expected parent) or a
    /// create (row must still be absent); it is needed because a null parent is a legitimate root, so
    /// null alone cannot distinguish a root update from a create.
    /// </summary>
    Task<WriteResult> UpsertOrganisationAsync(
        string id,
        string title,
        string kind,
        string? parent,
        bool expectExisting = false,
        string? expectedCurrentParent = null,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an organisation. Fails if it still has children or scopes.</summary>
    Task<WriteResult> DeleteOrganisationAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates the standard-target scope disposition for a <c>(subject, standard)</c> pair,
    /// confined to the unified table's standard target column. <paramref name="id"/> is the scope's own
    /// identity. An <c>Out</c> disposition requires a non-blank <paramref name="justification"/>. Fails if a
    /// different scope already maps the same pair; returns not-found if the id names an existing row of a
    /// different target kind (a PUT must not convert a row's target kind). When
    /// <paramref name="expectedCurrentOrganisation"/> is supplied, the store locks the existing row and
    /// returns a conflict if its current owning subject differs, so a concurrent cross-org move cannot slip
    /// a row past the caller's pre-write authorization.
    /// </summary>
    Task<WriteResult> UpsertScopeDispositionAsync(
        string id,
        string title,
        string subject,
        string standard,
        string disposition,
        string? justification = null,
        string? expectedCurrentOrganisation = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a standard-target scope by its id; not-found when no standard-target row matches. The delete
    /// always also matches the row's current owning subject against <paramref name="expectedOwner"/>, so a
    /// row moved to another organisation after the caller's pre-delete authorization is left untouched
    /// (not-found) rather than deleted without authorization for the new owner. The owner is required (there
    /// is no unbound delete): the only caller is the app DELETE handler, which passes the exact owner its
    /// authz check resolved, so the store can never delete a row the caller was not authorized against.
    /// </summary>
    Task<WriteResult> DeleteScopeAsync(string id, string expectedOwner, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates the requirement-target scope disposition for a <c>(subject, requirement)</c> pair,
    /// confined to the unified table's requirement target column. <paramref name="id"/> is the scope's own
    /// identity. An <c>Out</c> disposition requires a non-blank <paramref name="justification"/>. Fails if a
    /// different scope already maps the same pair; returns not-found if the id names an existing row of a
    /// different target kind. When <paramref name="expectedCurrentOrganisation"/> is supplied, the store
    /// locks the existing row and returns a conflict if its current owning subject differs.
    /// </summary>
    Task<WriteResult> UpsertRequirementScopeDispositionAsync(
        string id,
        string title,
        string subject,
        string requirement,
        string disposition,
        string? justification = null,
        string? expectedCurrentOrganisation = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a requirement-target scope by its id; not-found when no requirement-target row matches.
    /// <paramref name="expectedOwner"/> matches the row's current owning subject the same way
    /// <see cref="DeleteScopeAsync"/> does (required, always enforced), so a concurrent cross-org move
    /// cannot slip a row past the caller's pre-delete authorization.
    /// </summary>
    Task<WriteResult> DeleteRequirementScopeAsync(string id, string expectedOwner, CancellationToken cancellationToken = default);
}
