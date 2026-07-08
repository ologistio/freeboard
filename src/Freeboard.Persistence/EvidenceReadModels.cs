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
/// <see cref="CollectorId"/> and <see cref="Frequency"/> are the producing collector's recorded identity
/// and cadence; both are null on pre-migration rows and on attestation runs.
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
    AttestationResponseRow? Attestation,
    string? CollectorId = null,
    string? Frequency = null);

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
/// A computed evidence status for one collector on an <c>(organisation, requirement)</c> pair, derived
/// on read from that collector's latest run and its checks. <see cref="CollectorId"/> is the collector's
/// recorded identity or, for a pre-migration run, the <c>collector_ref</c> prefix. <see cref="Status"/>
/// is <c>HardFailure</c>, <c>Stale</c>, <c>SoftFailure</c>, or <c>Passing</c> for a collector that has a
/// run (precedence in that order); <c>Unknown</c> is a caller-derived status for a configured collector
/// with no run (the store never emits it). <c>Stale</c> means the latest run is past its cadence window
/// plus grace. <c>Passing</c> means only that the latest run has no failing hard check, never that the
/// requirement is satisfied. <see cref="LastCollectedAt"/> is the latest run's <c>collected_at</c>.
/// </summary>
public sealed record CollectorEvidenceStatusRow(
    string OrganisationId, string RequirementId, string CollectorId, string Status, DateTime LastCollectedAt);
