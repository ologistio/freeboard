namespace Freeboard.Persistence;

/// <summary>
/// Read abstraction over persisted evidence. Returns evidence runs with their resolved checks and, for
/// attestation runs, the 1:1 extension, plus a computed status per <c>(organisation, requirement,
/// collector)</c> that has evidence.
/// <para>
/// Status is derived on read from each collector's latest run (pinned by <c>collected_at</c>,
/// <c>received_at</c>, <c>created_at</c>, <c>id</c> descending) and that run's checks: <c>Passing</c>
/// means "the latest run has no failing hard check", NOT "the requirement is satisfied" - with no
/// expected-check catalogue a run can under-report, so a pass can be overclaimed. The store returns a
/// status only for collectors that have a run and never emits <c>Unknown</c>; deriving <c>Unknown</c>
/// for a configured collector with no run is the web caller's responsibility.
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
    /// requirement, collector)</c> under any of <paramref name="organisationIds"/> that has evidence,
    /// derived from each collector's latest run. Status is <c>HardFailure</c>, <c>Stale</c>,
    /// <c>SoftFailure</c>, or <c>Passing</c> (in that precedence); the store never emits <c>Unknown</c>
    /// and does not enumerate configured collectors. A single call covers every supplied organisation so
    /// the caller issues one batched read.
    /// </summary>
    Task<IReadOnlyList<CollectorEvidenceStatusRow>> GetCollectorEvidenceStatusesAsync(
        IReadOnlyCollection<string> organisationIds, CancellationToken cancellationToken = default);
}
