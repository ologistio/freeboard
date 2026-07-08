using Dapper;
using Freeboard.Persistence.Auth;
using MySqlConnector;

namespace Freeboard.Persistence;

/// <summary>
/// MySQL-backed append-only <see cref="IEvidenceWriteStore"/>. Each append runs in one transaction: the
/// run, its checks, and (for an attestation) the extension row commit together. Values are validated
/// before any write, so an invalid append returns a failing <see cref="WriteResult"/> and writes
/// nothing. Only plain <c>INSERT</c> is used - never <c>ON DUPLICATE KEY UPDATE</c>, <c>REPLACE</c>, or
/// <c>INSERT IGNORE</c>: the first two would trip the append-only UPDATE/DELETE triggers and the last
/// would swallow a real idempotency collision. A duplicate <c>(vendor, collector_ref)</c> or duplicate
/// check name is caught and mapped to a conflict, rolling back so no partial run is left behind.
/// </summary>
public sealed class MySqlEvidenceWriteStore(IDbConnectionFactory connectionFactory, IUlidFactory ulidFactory)
    : IEvidenceWriteStore
{
    private const string KindCollector = "Collector";
    private const string KindAttestationResponse = "AttestationResponse";

    public Task<WriteResult> AppendEvidenceAsync(NewEvidenceRun run, CancellationToken cancellationToken = default)
        => AppendAsync(run, KindCollector, attestation: null, cancellationToken);

    public Task<WriteResult> AppendAttestationResponseAsync(
        NewEvidenceRun run, NewAttestationResponse attestation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        if (string.IsNullOrWhiteSpace(attestation.UserId))
        {
            return Task.FromResult(WriteResult.Fail("Attestation respondent user id is required."));
        }

        return AppendAsync(run, KindAttestationResponse, attestation, cancellationToken);
    }

    private async Task<WriteResult> AppendAsync(
        NewEvidenceRun run, string kind, NewAttestationResponse? attestation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (Validate(run) is { } invalid)
        {
            return invalid;
        }

        var checks = run.Checks ?? [];

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var evidenceId = ulidFactory.NewId();
        var now = DateTime.UtcNow;

        // Collector identity and cadence belong only to a Collector-kind run. Force them null for any
        // other kind so a non-Collector append can never persist collector fields, whatever the caller
        // passed.
        var isCollector = string.Equals(kind, KindCollector, StringComparison.Ordinal);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO evidence_runs "
                + "(id, kind, organisation_id, requirement_id, vendor, collector_ref, result, "
                + "collected_at, received_at, raw_payload, created_at, collector_id, frequency) "
                + "VALUES (@Id, @Kind, @OrganisationId, @RequirementId, @Vendor, @CollectorRef, @Result, "
                + "@CollectedAt, @ReceivedAt, @RawPayload, @Now, @CollectorId, @Frequency);",
                new
                {
                    Id = evidenceId,
                    Kind = kind,
                    run.OrganisationId,
                    run.RequirementId,
                    run.Vendor,
                    run.CollectorRef,
                    run.Result,
                    run.CollectedAt,
                    run.ReceivedAt,
                    run.RawPayload,
                    Now = now,
                    CollectorId = isCollector ? run.CollectorId : null,
                    Frequency = isCollector ? run.Frequency : null,
                },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (checks.Count > 0)
            {
                var checkRows = checks.Select((c, i) => new
                {
                    Id = ulidFactory.NewId(),
                    EvidenceId = evidenceId,
                    c.Name,
                    c.Severity,
                    c.Result,
                    Ordinal = i,
                    c.Detail,
                });
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO evidence_checks (id, evidence_id, name, severity, result, ordinal, detail) "
                    + "VALUES (@Id, @EvidenceId, @Name, @Severity, @Result, @Ordinal, @Detail);",
                    checkRows, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            if (attestation is not null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO attestation_responses (evidence_id, user_id, quiz_passed, score) "
                    + "VALUES (@EvidenceId, @UserId, @QuizPassed, @Score);",
                    new
                    {
                        EvidenceId = evidenceId,
                        attestation.UserId,
                        QuizPassed = attestation.QuizPassed ? 1 : 0,
                        attestation.Score,
                    },
                    transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return WriteResult.Success;
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            // Either the (vendor, collector_ref) idempotency key (a re-delivered observation) or a
            // repeated check name within the run. Rolling back the uncommitted inserts does not fire the
            // append-only delete trigger.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return WriteResult.Conflict(
                "This evidence already exists (duplicate vendor/collector reference or check name).");
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.InvalidJsonText)
        {
            // raw_payload is a JSON column, so a non-JSON payload is rejected by the server. Map it to a
            // failing result (like the duplicate-key path) instead of surfacing a raw MySqlException.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return WriteResult.Fail("Evidence raw payload must be valid JSON.");
        }
    }

    private WriteResult? Validate(NewEvidenceRun run)
    {
        if (string.IsNullOrWhiteSpace(run.OrganisationId))
        {
            return WriteResult.Fail("Evidence organisation id is required.");
        }

        if (string.IsNullOrWhiteSpace(run.RequirementId))
        {
            return WriteResult.Fail("Evidence requirement id is required.");
        }

        if (string.IsNullOrWhiteSpace(run.Vendor))
        {
            return WriteResult.Fail("Evidence vendor is required.");
        }

        if (string.IsNullOrWhiteSpace(run.CollectorRef))
        {
            return WriteResult.Fail("Evidence collector reference is required.");
        }

        if (!IsResult(run.Result))
        {
            return WriteResult.Fail("Evidence result must be 'Pass' or 'Fail'.");
        }

        foreach (var check in run.Checks ?? [])
        {
            if (string.IsNullOrWhiteSpace(check.Name))
            {
                return WriteResult.Fail("Each evidence check must have a name.");
            }

            if (!IsSeverity(check.Severity))
            {
                return WriteResult.Fail("Evidence check severity must be 'Hard' or 'Soft'.");
            }

            if (!IsResult(check.Result))
            {
                return WriteResult.Fail("Evidence check result must be 'Pass' or 'Fail'.");
            }
        }

        return null;
    }

    private static bool IsResult(string value)
        => string.Equals(value, "Pass", StringComparison.Ordinal) || string.Equals(value, "Fail", StringComparison.Ordinal);

    private static bool IsSeverity(string value)
        => string.Equals(value, "Hard", StringComparison.Ordinal) || string.Equals(value, "Soft", StringComparison.Ordinal);
}
