namespace Freeboard.Persistence;

/// <summary>
/// Read abstraction over persisted evidence. Returns evidence runs with their resolved checks and, for
/// attestation runs, the 1:1 extension, plus a computed status per <c>(organisation, requirement,
/// collector)</c> that has evidence.
/// <para>
/// <c>Passing</c> means "no assessed run has a failing check, an <c>Error</c> result, or an
/// overdue cadence", NOT "the requirement is satisfied". With no expected-check catalogue a run
/// can under-report, so a pass can be overclaimed.
/// </para>
/// </summary>
public interface IEvidenceStore
{
    /// <summary>
    /// Returns every evidence run for the <c>(organisation, requirement)</c> pair, newest first, each
    /// with its checks (ordered by ordinal) and, for attestation runs, its extension row.
    /// </summary>
    Task<IReadOnlyList<EvidenceRunRow>> GetEvidenceRunsAsync(
        string organisationId, string requirementId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the latest evidence run for the <c>(organisation, requirement)</c> pair with its resolved
    /// checks and any attestation extension, or null when the pair has no evidence.
    /// </summary>
    Task<EvidenceRunRow?> GetLatestEvidenceRunAsync(
        string organisationId, string requirementId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a computed <see cref="CollectorEvidenceStatusRow"/> for each <c>(organisation,
    /// requirement, collector)</c> under any of <paramref name="organisationIds"/> that has evidence.
    /// The status is derived over an assessed set of runs: the group's latest run alone when that run
    /// carries no cycle, where latest means by <c>collected_at</c>, <c>received_at</c>,
    /// <c>created_at</c>, then <c>id</c> descending, and otherwise every run of the group sharing the latest run's cycle, so one
    /// collection cycle is assessed as one outcome and a machine absent from the newest cycle stops
    /// contributing. Status is <c>HardFailure</c>, <c>Errored</c>, <c>Stale</c>, <c>SoftFailure</c>, or
    /// <c>Passing</c> (in that precedence); the store never emits <c>Unknown</c>, which stays the
    /// caller's status for a configured collector with no run, and it does not enumerate configured
    /// collectors. A single call covers every supplied organisation so the caller issues one batched
    /// read.
    /// </summary>
    Task<IReadOnlyList<CollectorEvidenceStatusRow>> GetCollectorEvidenceStatusesAsync(
        IReadOnlyCollection<string> organisationIds, CancellationToken cancellationToken = default);
}
