namespace Freeboard.Persistence;

/// <summary>
/// A persisted evidence run: one observation a collector delivered or one attestation submission.
/// Identity is <see cref="Id"/> (a ULID). <see cref="Kind"/> is <c>Collector</c> or
/// <c>AttestationResponse</c>; <see cref="Result"/> is the run-overall outcome (<c>Pass</c> or
/// <c>Fail</c>). <see cref="OrganisationId"/> and <see cref="RequirementId"/> are scalar references
/// (no FK), so a run can outlive a removed organisation/requirement. <see cref="ReceivedAt"/> and
/// <see cref="RawPayload"/> are null when unset; <see cref="RawPayload"/> is the vendor's opaque JSON.
/// <see cref="Checks"/> holds the run's named checks ordered by ordinal; <see cref="Attestation"/> is
/// the 1:1 extension for an <c>AttestationResponse</c> run and null for a <c>Collector</c> run.
/// </summary>
public sealed record EvidenceRunRow(
    string Id,
    string Kind,
    string OrganisationId,
    string RequirementId,
    string Vendor,
    string CollectorRef,
    string Result,
    DateTime CollectedAt,
    DateTime? ReceivedAt,
    string? RawPayload,
    DateTime CreatedAt,
    IReadOnlyList<EvidenceCheckRow> Checks,
    AttestationResponseRow? Attestation);

/// <summary>
/// A named check within an evidence run. <see cref="Severity"/> is <c>Hard</c> or <c>Soft</c>;
/// <see cref="Result"/> is <c>Pass</c> or <c>Fail</c>. A failing <c>Hard</c> check fails the
/// requirement's assessment; a failing <c>Soft</c> check only warns. <see cref="Detail"/> is null when
/// unset. <see cref="Ordinal"/> gives the check's position within its run.
/// </summary>
public sealed record EvidenceCheckRow(
    string Id, string EvidenceId, string Name, string Severity, string Result, int Ordinal, string? Detail);

/// <summary>
/// The attestation-only extension of an <c>AttestationResponse</c> evidence run, keyed 1:1 on the run's
/// <see cref="EvidenceId"/>. <see cref="UserId"/> is the respondent (a scalar reference, no FK).
/// <see cref="Score"/> is null when unset.
/// </summary>
public sealed record AttestationResponseRow(string EvidenceId, string UserId, bool QuizPassed, int? Score);

/// <summary>
/// A computed assessment status for an <c>(organisation, requirement)</c> pair, derived on read from the
/// pair's latest evidence run and its checks. <see cref="Status"/> is <c>HardFailure</c>,
/// <c>SoftFailure</c>, or <c>Passing</c> for a pair that has evidence; <c>NoEvidence</c> is a
/// caller-derived status for an in-scope pair with no run (the store never emits it). <c>Passing</c>
/// means only that the latest run has no failing hard check, never that the requirement is satisfied.
/// </summary>
public sealed record AssessmentResultRow(string OrganisationId, string RequirementId, string Status);
