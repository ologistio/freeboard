namespace Freeboard.Persistence;

/// <summary>
/// One check row inside an evidence run, already validated and projected by the ingest endpoint.
/// <see cref="Data"/> is a raw JSON object string (or null when absent). <see cref="Seq"/> preserves
/// the caller's array order.
/// </summary>
public sealed record EvidenceCheckInput(
    string Name,
    string Severity,
    string Status,
    string? Detail,
    string? Data,
    int Seq);

/// <summary>
/// A fully-validated evidence run ready to persist. The endpoint has already hashed the exact request
/// body, snapshotted the collector identity, and derived the counts; the store only writes what it is
/// given. <see cref="Metadata"/> is a raw JSON object string (or null). The store never computes a
/// rollup verdict.
/// </summary>
public sealed record EvidenceRunInput(
    string CollectorId,
    string? CollectorTitle,
    string ControlId,
    string? VendorId,
    string? CollectorType,
    string RunId,
    string SchemaVersion,
    string? CollectorVersion,
    DateTime StartedAt,
    DateTime FinishedAt,
    byte[] RequestBodySha256,
    int HardFailCount,
    int SoftFailCount,
    int TotalCount,
    string? Metadata,
    IReadOnlyList<EvidenceCheckInput> Checks);

/// <summary>
/// The outcome of an append. On a new insert the values are the ones just written. On a replay
/// (<see cref="WasNew"/> false, <see cref="BodyMatches"/> true) the <see cref="ReceivedAt"/> and counts
/// are read back from the ORIGINAL persisted row, so the endpoint returns the same body for the new and
/// replay cases. On a conflict (<see cref="WasNew"/> false, <see cref="BodyMatches"/> false) the counts
/// and timestamp are irrelevant and the endpoint returns 409 from the flags alone.
/// </summary>
public readonly record struct EvidenceAppendResult(
    string EvidenceId,
    bool WasNew,
    bool BodyMatches,
    DateTime ReceivedAt,
    int HardFailCount,
    int SoftFailCount,
    int TotalCount);

/// <summary>
/// Append-only runtime Evidence store. A run and its checks are written in one transaction; the unique
/// <c>(collector_id, run_id)</c> key makes an identical re-POST a replay and a changed body a conflict.
/// Storage-agnostic: no SQL/Dapper types in the contract.
/// </summary>
public interface IEvidenceIngestStore
{
    /// <summary>
    /// Inserts the run and its checks in one transaction. On a duplicate <c>(collector_id, run_id)</c>
    /// it inserts nothing and reports whether the stored <c>request_body_sha256</c> matches the incoming
    /// run's hash, reading back the existing row's received-at and counts for the replay body.
    /// </summary>
    Task<EvidenceAppendResult> TryAppendAsync(EvidenceRunInput run, CancellationToken cancellationToken = default);
}
