namespace Freeboard.Persistence;

/// <summary>
/// A new named check to append with an evidence run. <see cref="Severity"/> must be <c>Hard</c> or
/// <c>Soft</c> and <see cref="Result"/> must be <c>Pass</c> or <c>Fail</c>. The store assigns each
/// check's ordinal from its position in the submitted list.
/// </summary>
public sealed record NewEvidenceCheck(string Name, string Severity, string Result, string? Detail);

/// <summary>
/// A new evidence run to append. A run carries exactly one of two identities, and each identity backs
/// one idempotency key.
/// <list type="bullet">
/// <item><description>The producer identity: <see cref="Vendor"/> with <see cref="CollectorRef"/>, where
/// <see cref="CollectorRef"/> is the producer's stable id for this specific observation. It dedups a
/// re-delivered observation under <c>UNIQUE (vendor, collector_ref)</c>, and its run carries no
/// <see cref="CycleId"/>.</description></item>
/// <item><description>The cycle identity: a non-blank <see cref="CollectorId"/> with a non-blank
/// <see cref="CycleId"/>, and no vendor or collector reference. It dedups a re-delivered or retried
/// collection cycle under <c>UNIQUE (cycle_id, organisation_id, requirement_id, asset_key)</c>, where
/// the generated <c>asset_key</c> is <see cref="AssetId"/> or the empty string.</description></item>
/// </list>
/// An append that carries neither identity, or both, is rejected before any SQL runs, so every run is
/// dedupped by exactly one key. <see cref="Vendor"/> and <see cref="CollectorRef"/> are both present or
/// both absent. <see cref="Result"/> must be <c>Pass</c>, <c>Fail</c>, or <c>Error</c>;
/// <see cref="ErrorDetail"/> is required and non-blank for <c>Error</c> and must be absent otherwise.
/// <see cref="AssetId"/> names the machine asset the run describes and is null when the run describes
/// the organisation as a whole. <see cref="ReceivedAt"/> and <see cref="RawPayload"/> are optional. The
/// run's kind is set by the append method, not carried here. <see cref="CollectorId"/> and
/// <see cref="Frequency"/> denormalise the producing collector's identity and collection cadence onto the
/// run so staleness is evaluated from the evidence tables alone. <see cref="CollectorId"/>,
/// <see cref="Frequency"/>, <see cref="AssetId"/>, and <see cref="CycleId"/> belong to a
/// <c>Collector</c>-kind run only, so an attestation append stores null for all four whatever it passed.
/// A blank value in any of the seven optional identity, cadence, and reason members is normalized to
/// null. This happens before validation and before the insert, so absence has one representation.
/// </summary>
public sealed record NewEvidenceRun(
    string OrganisationId,
    string RequirementId,
    string? Vendor,
    string? CollectorRef,
    string Result,
    DateTime CollectedAt,
    DateTime? ReceivedAt,
    string? RawPayload,
    IReadOnlyList<NewEvidenceCheck> Checks,
    string? CollectorId = null,
    string? Frequency = null,
    string? AssetId = null,
    string? CycleId = null,
    string? ErrorDetail = null);

/// <summary>
/// The attestation-only fields appended alongside an attestation evidence run. <see cref="UserId"/> is
/// the respondent; <see cref="Score"/> is optional.
/// </summary>
public sealed record NewAttestationResponse(string UserId, bool QuizPassed, int? Score);

/// <summary>
/// Append-only write abstraction over persisted evidence. Exposes only append operations - there is no
/// update or delete method, so no code path can mutate a recorded run; the database backs this with
/// BEFORE UPDATE / BEFORE DELETE triggers. Each append runs in one transaction: the run, its checks, and
/// (for an attestation) the extension row commit together or not at all. The run result set is
/// <c>Pass</c>, <c>Fail</c>, or <c>Error</c>. A validation failure, a duplicate check name, or a
/// duplicate under EITHER idempotency key - <c>(vendor, collector_ref)</c> or
/// <c>(cycle_id, organisation_id, requirement_id, asset_key)</c> - returns a failing
/// <see cref="WriteResult"/> and writes nothing.
/// </summary>
public interface IEvidenceWriteStore
{
    /// <summary>
    /// Appends a <c>Collector</c> evidence run with its checks. Returns a failing
    /// <see cref="WriteResult"/> for a validation error or a duplicate under either idempotency key.
    /// </summary>
    Task<WriteResult> AppendEvidenceAsync(NewEvidenceRun run, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends an <c>AttestationResponse</c> evidence run with its per-question checks and its 1:1
    /// extension row. Its machine and cycle are dropped, so it is identified by its <c>vendor</c> and
    /// <c>collector_ref</c>. Returns a failing <see cref="WriteResult"/> for a validation error or a
    /// duplicate <c>(vendor, collector_ref)</c>.
    /// </summary>
    Task<WriteResult> AppendAttestationResponseAsync(
        NewEvidenceRun run, NewAttestationResponse attestation, CancellationToken cancellationToken = default);
}
