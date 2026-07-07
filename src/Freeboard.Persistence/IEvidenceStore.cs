namespace Freeboard.Persistence;

/// <summary>
/// Read abstraction over persisted evidence. Returns evidence runs with their resolved checks and, for
/// attestation runs, the 1:1 extension, plus a computed AssessmentResult per <c>(organisation,
/// requirement)</c> pair that has evidence.
/// <para>
/// Assessment is derived on read from each pair's latest run (pinned by <c>collected_at</c>,
/// <c>received_at</c>, <c>created_at</c>, <c>id</c> descending) and that run's checks: it means "the
/// latest run has no failing hard check", NOT "the requirement is satisfied" - with no expected-check
/// catalogue a run can under-report, so a pass can be overclaimed. The store returns a status only for
/// pairs that have a run and never emits <c>NoEvidence</c>; deriving <c>NoEvidence</c> for in-scope pairs
/// with no run, and intersecting with the resolved in-scope set, is the web caller's responsibility.
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
    /// Returns a computed <see cref="AssessmentResultRow"/> for each <c>(organisation, requirement)</c>
    /// pair under <paramref name="organisationId"/> that has evidence, derived from each pair's latest
    /// run. Status is <c>HardFailure</c>, <c>SoftFailure</c>, or <c>Passing</c>; the store never emits
    /// <c>NoEvidence</c> and does not enumerate in-scope pairs.
    /// </summary>
    Task<IReadOnlyList<AssessmentResultRow>> GetAssessmentResultsAsync(
        string organisationId, CancellationToken cancellationToken = default);
}
