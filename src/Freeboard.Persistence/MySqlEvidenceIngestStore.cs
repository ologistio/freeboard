using System.Data.Common;
using Dapper;
using Freeboard.Persistence.Auth;
using MySqlConnector;

namespace Freeboard.Persistence;

/// <summary>
/// MySQL-backed <see cref="IEvidenceIngestStore"/>. The run and its checks are written in one
/// transaction. Evidence is immutable, so a duplicate <c>(collector_id, run_id)</c> never mutates: the
/// store reads the existing row back and reports whether its stored body hash matches (replay) or not
/// (conflict). A concurrent racing insert that loses the unique key re-reads the winner's row.
/// </summary>
public sealed class MySqlEvidenceIngestStore(IDbConnectionFactory connectionFactory, IUlidFactory ulidFactory)
    : IEvidenceIngestStore
{
    public async Task<EvidenceAppendResult> TryAppendAsync(
        EvidenceRunInput run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var existing = await ReadExistingAsync(connection, null, run.CollectorId, run.RunId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return Replay(existing, run.RequestBodySha256);
        }

        var id = ulidFactory.NewId();
        var receivedAt = DateTime.UtcNow;

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO evidence_runs "
                + "(id, collector_id, collector_title, control_id, vendor_id, collector_type, run_id, "
                + "schema_version, collector_version, started_at, finished_at, received_at, "
                + "request_body_sha256, hard_fail_count, soft_fail_count, total_count, metadata) "
                + "VALUES (@Id, @CollectorId, @CollectorTitle, @ControlId, @VendorId, @CollectorType, @RunId, "
                + "@SchemaVersion, @CollectorVersion, @StartedAt, @FinishedAt, @ReceivedAt, "
                + "@RequestBodySha256, @HardFailCount, @SoftFailCount, @TotalCount, @Metadata);",
                new
                {
                    Id = id,
                    run.CollectorId,
                    run.CollectorTitle,
                    run.ControlId,
                    run.VendorId,
                    run.CollectorType,
                    run.RunId,
                    run.SchemaVersion,
                    run.CollectorVersion,
                    run.StartedAt,
                    run.FinishedAt,
                    ReceivedAt = receivedAt,
                    run.RequestBodySha256,
                    run.HardFailCount,
                    run.SoftFailCount,
                    run.TotalCount,
                    run.Metadata,
                },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            foreach (var check in run.Checks)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO evidence_run_checks "
                    + "(id, evidence_run_id, name, severity, status, detail, data, seq) "
                    + "VALUES (@Id, @RunId, @Name, @Severity, @Status, @Detail, @Data, @Seq);",
                    new
                    {
                        Id = ulidFactory.NewId(),
                        RunId = id,
                        check.Name,
                        check.Severity,
                        check.Status,
                        check.Detail,
                        check.Data,
                        check.Seq,
                    },
                    transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            // Lost a race to an identical (collector_id, run_id): roll back this attempt and read the
            // winner's row so the caller still gets a replay/conflict decision.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            var winner = await ReadExistingAsync(connection, null, run.CollectorId, run.RunId, cancellationToken)
                .ConfigureAwait(false);
            return winner is not null
                ? Replay(winner, run.RequestBodySha256)
                : throw new InvalidOperationException("Duplicate key on insert but no existing evidence run found.");
        }

        return new EvidenceAppendResult(
            id, WasNew: true, BodyMatches: true, receivedAt,
            run.HardFailCount, run.SoftFailCount, run.TotalCount);
    }

    private static async Task<ExistingRun?> ReadExistingAsync(
        DbConnection connection, DbTransaction? transaction, string collectorId, string runId,
        CancellationToken cancellationToken)
    {
        return await connection.QuerySingleOrDefaultAsync<ExistingRun>(new CommandDefinition(
            "SELECT id AS Id, received_at AS ReceivedAt, request_body_sha256 AS RequestBodySha256, "
            + "hard_fail_count AS HardFailCount, soft_fail_count AS SoftFailCount, total_count AS TotalCount "
            + "FROM evidence_runs WHERE collector_id = @CollectorId AND run_id = @RunId;",
            new { CollectorId = collectorId, RunId = runId },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static EvidenceAppendResult Replay(ExistingRun existing, byte[] incomingHash)
    {
        var matches = existing.RequestBodySha256 is not null
            && incomingHash.AsSpan().SequenceEqual(existing.RequestBodySha256);
        return new EvidenceAppendResult(
            existing.Id, WasNew: false, BodyMatches: matches, existing.ReceivedAt,
            existing.HardFailCount, existing.SoftFailCount, existing.TotalCount);
    }

    private sealed record ExistingRun(
        string Id,
        DateTime ReceivedAt,
        byte[]? RequestBodySha256,
        int HardFailCount,
        int SoftFailCount,
        int TotalCount);
}
