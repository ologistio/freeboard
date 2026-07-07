namespace Freeboard.Persistence;

/// <summary>
/// A new named check to append with an evidence run. <see cref="Severity"/> must be <c>Hard</c> or
/// <c>Soft</c> and <see cref="Result"/> must be <c>Pass</c> or <c>Fail</c>. The store assigns each
/// check's ordinal from its position in the submitted list.
/// </summary>
public sealed record NewEvidenceCheck(string Name, string Severity, string Result, string? Detail);

/// <summary>
/// A new evidence run to append. <see cref="Vendor"/> and <see cref="CollectorRef"/> are both required
/// and together form the idempotency key: <see cref="CollectorRef"/> is the vendor's stable id for this
/// specific observation/submission, so a re-delivery collides and is rejected. <see cref="Result"/> must
/// be <c>Pass</c> or <c>Fail</c>. <see cref="ReceivedAt"/> and <see cref="RawPayload"/> are optional.
/// The run's kind is set by the append method, not carried here.
/// </summary>
public sealed record NewEvidenceRun(
    string OrganisationId,
    string RequirementId,
    string Vendor,
    string CollectorRef,
    string Result,
    DateTime CollectedAt,
    DateTime? ReceivedAt,
    string? RawPayload,
    IReadOnlyList<NewEvidenceCheck> Checks);

/// <summary>
/// The attestation-only fields appended alongside an attestation evidence run. <see cref="UserId"/> is
/// the respondent; <see cref="Score"/> is optional.
/// </summary>
public sealed record NewAttestationResponse(string UserId, bool QuizPassed, int? Score);

/// <summary>
/// Append-only write abstraction over persisted evidence. Exposes only append operations - there is no
/// update or delete method, so no code path can mutate a recorded run; the database backs this with
/// BEFORE UPDATE / BEFORE DELETE triggers. Each append runs in one transaction: the run, its checks, and
/// (for an attestation) the extension row commit together or not at all. A validation failure or a
/// duplicate <c>(vendor, collector_ref)</c> / duplicate check name returns a failing
/// <see cref="WriteResult"/> and writes nothing.
/// </summary>
public interface IEvidenceWriteStore
{
    /// <summary>
    /// Appends a <c>Collector</c> evidence run with its checks. Returns a failing
    /// <see cref="WriteResult"/> for a validation error or a duplicate <c>(vendor, collector_ref)</c>.
    /// </summary>
    Task<WriteResult> AppendEvidenceAsync(NewEvidenceRun run, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends an <c>AttestationResponse</c> evidence run with its per-question checks and its 1:1
    /// extension row. Returns a failing <see cref="WriteResult"/> for a validation error or a duplicate
    /// <c>(vendor, collector_ref)</c>.
    /// </summary>
    Task<WriteResult> AppendAttestationResponseAsync(
        NewEvidenceRun run, NewAttestationResponse attestation, CancellationToken cancellationToken = default);
}
