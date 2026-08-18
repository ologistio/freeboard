using Dapper;
using Freeboard.Core.GitOps;
using Freeboard.Persistence.Auth;
using MySqlConnector;

namespace Freeboard.Persistence;

/// <summary>
/// MySQL-backed append-only <see cref="IEvidenceWriteStore"/>. Each append runs in one transaction: the
/// run, its checks, and (for an attestation) the extension row commit together. Values are validated
/// before any write, so an invalid append returns a failing <see cref="WriteResult"/> and writes
/// nothing. Only plain <c>INSERT</c> is used - never <c>ON DUPLICATE KEY UPDATE</c>, <c>REPLACE</c>, or
/// <c>INSERT IGNORE</c>: the first two would trip the append-only UPDATE/DELETE triggers and the last
/// would swallow a real idempotency collision. A duplicate under either idempotency key -
/// <c>(vendor, collector_ref)</c> or <c>(cycle_id, organisation_id, requirement_id, asset_key)</c> - and
/// a duplicate check name are caught and mapped to a conflict, rolling back so no partial run is left
/// behind. A broken check constraint is mapped to a failure the same way, so a permanently invalid write
/// never escapes as a raw <see cref="MySqlException"/>.
/// </summary>
public sealed class MySqlEvidenceWriteStore(IDbConnectionFactory connectionFactory, IUlidFactory ulidFactory)
    : IEvidenceWriteStore
{
    private const string KindCollector = "Collector";
    private const string KindAttestationResponse = "AttestationResponse";

    // MySQL raises this for a violated CHECK. MySqlErrorCode has no member for it, so it is matched
    // numerically.
    private const int CheckConstraintViolated = 3819;

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

        var effective = Effective(run, kind);

        if (Validate(effective) is { } invalid)
        {
            return invalid;
        }

        var checks = effective.Checks ?? [];

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var evidenceId = ulidFactory.NewId();
        var now = DateTime.UtcNow;

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO evidence_runs "
                + "(id, kind, organisation_id, requirement_id, asset_id, vendor, collector_ref, result, "
                + "collected_at, received_at, raw_payload, created_at, collector_id, frequency, cycle_id, "
                + "error_detail) "
                + "VALUES (@Id, @Kind, @OrganisationId, @RequirementId, @AssetId, @Vendor, @CollectorRef, "
                + "@Result, @CollectedAt, @ReceivedAt, @RawPayload, @Now, @CollectorId, @Frequency, "
                + "@CycleId, @ErrorDetail);",
                new
                {
                    Id = evidenceId,
                    Kind = kind,
                    effective.OrganisationId,
                    effective.RequirementId,
                    effective.AssetId,
                    effective.Vendor,
                    effective.CollectorRef,
                    effective.Result,
                    effective.CollectedAt,
                    effective.ReceivedAt,
                    effective.RawPayload,
                    Now = now,
                    effective.CollectorId,
                    effective.Frequency,
                    effective.CycleId,
                    effective.ErrorDetail,
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
            // Either idempotency key (a re-delivered observation or a re-run collection cycle) or a
            // repeated check name within the run. Rolling back the uncommitted inserts does not fire the
            // append-only delete trigger.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return WriteResult.Conflict(
                "This evidence already exists (duplicate vendor/collector reference, duplicate collection "
                + "cycle for this organisation, requirement and machine, or duplicate check name).");
        }
        catch (MySqlException ex) when ((int)ex.ErrorCode == CheckConstraintViolated)
        {
            // The database's backstop for a rule Validate already applies. Mapping it to a failure keeps a
            // permanently invalid write from surfacing as an unreachable-store error that a producer would
            // retry forever.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return WriteResult.Fail(
                $"Evidence run violates the database constraint {ConstraintName(ex.Message)}.");
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.InvalidJsonText)
        {
            // raw_payload is a JSON column, so a non-JSON payload is rejected by the server. Map it to a
            // failing result (like the duplicate-key path) instead of surfacing a raw MySqlException.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return WriteResult.Fail("Evidence raw payload must be valid JSON.");
        }
    }

    /// <summary>
    /// The run as it will be stored. A member the run's kind cannot persist is dropped here rather than at
    /// the <c>INSERT</c>, so validation judges the values that reach the row. A blank value is normalized
    /// to null so absence has one representation: the application reads a whitespace-only value as absent
    /// while the database reads it as present, and a blank <c>asset_id</c> would also collide with the
    /// cycle's organisation-level run through the PAD SPACE collation of the generated key.
    /// <c>ErrorDetail</c> is NOT dropped by kind: it belongs to <c>result</c>, which every kind carries.
    /// </summary>
    private static NewEvidenceRun Effective(NewEvidenceRun run, string kind)
    {
        var isCollector = string.Equals(kind, KindCollector, StringComparison.Ordinal);
        return run with
        {
            Vendor = NullIfBlank(run.Vendor),
            CollectorRef = NullIfBlank(run.CollectorRef),
            CollectorId = isCollector ? NullIfBlank(run.CollectorId) : null,
            Frequency = isCollector ? NullIfBlank(run.Frequency) : null,
            AssetId = isCollector ? NullIfBlank(run.AssetId) : null,
            CycleId = isCollector ? NullIfBlank(run.CycleId) : null,
            ErrorDetail = NullIfBlank(run.ErrorDetail),
        };
    }

    private static WriteResult? Validate(NewEvidenceRun run)
    {
        if (string.IsNullOrWhiteSpace(run.OrganisationId))
        {
            return WriteResult.Fail("Evidence organisation id is required.");
        }

        if (string.IsNullOrWhiteSpace(run.RequirementId))
        {
            return WriteResult.Fail("Evidence requirement id is required.");
        }

        if (!IsRunResult(run.Result))
        {
            return WriteResult.Fail("Evidence result must be 'Pass', 'Fail' or 'Error'.");
        }

        var errored = string.Equals(run.Result, ResultError, StringComparison.Ordinal);
        if (errored && run.ErrorDetail is null)
        {
            return WriteResult.Fail("An errored evidence run must state why collection failed.");
        }

        if (!errored && run.ErrorDetail is not null)
        {
            return WriteResult.Fail("An evidence error detail belongs only to a run whose result is 'Error'.");
        }

        if (run.Vendor is null != run.CollectorRef is null)
        {
            return WriteResult.Fail(
                "Evidence vendor and collector reference must be both present or both absent.");
        }

        // Exactly one identity, so every run is dedupped by exactly one key and no run is dedupped by
        // both. An attestation run carries no cycle whatever the caller passed, so it satisfies this
        // through the vendor and collector reference every attestation path already sets.
        var hasProducerIdentity = run.Vendor is not null;
        var hasCycleIdentity = run.CycleId is not null;

        if (hasProducerIdentity && hasCycleIdentity)
        {
            return WriteResult.Fail(
                "An evidence run carries either a vendor with a collector reference or a collection "
                + "cycle, never both.");
        }

        if (!hasProducerIdentity && !hasCycleIdentity)
        {
            return WriteResult.Fail(
                "An evidence run must carry either a vendor with a collector reference or a collection "
                + "cycle.");
        }

        // A blank collector id names no collector to the read side, which derives an identity only from a
        // non-empty value, so a cycle-keyed run carrying one would store and then be assessed for no
        // collector. Effective() has already normalized a blank value to null.
        if (hasCycleIdentity && run.CollectorId is null)
        {
            return WriteResult.Fail("A cycle-keyed evidence run must name the collector that produced it.");
        }

        // A cycle-keyed run must also carry a known cadence. Staleness derives its window from the
        // cadence, so a run with none is never stale and holds its verdict forever. A cycle can be left
        // part-written when its collector dies, and the newest cycle is the whole assessed set, so a
        // part-written cycle with no cadence would report the machines that did land and never decay.
        if (hasCycleIdentity && !CollectorFrequency.Tokens.Contains(run.Frequency ?? string.Empty))
        {
            return WriteResult.Fail(
                "A cycle-keyed evidence run must record a known collection cadence.");
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

            if (!IsCheckResult(check.Result))
            {
                return WriteResult.Fail("Evidence check result must be 'Pass' or 'Fail'.");
            }
        }

        return null;
    }

    private const string ResultError = "Error";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // The run-overall result set is wider than the per-check one: only a run can record that the
    // collection attempt itself failed.
    private static bool IsRunResult(string value)
        => IsCheckResult(value) || string.Equals(value, ResultError, StringComparison.Ordinal);

    private static bool IsCheckResult(string value)
        => string.Equals(value, "Pass", StringComparison.Ordinal) || string.Equals(value, "Fail", StringComparison.Ordinal);

    private static bool IsSeverity(string value)
        => string.Equals(value, "Hard", StringComparison.Ordinal) || string.Equals(value, "Soft", StringComparison.Ordinal);

    // MySQL names the violated constraint in the message, as: Check constraint 'name' is violated.
    internal static string ConstraintName(string message)
    {
        var open = message.IndexOf('\'');
        if (open < 0)
        {
            return "on evidence_runs";
        }

        var close = message.IndexOf('\'', open + 1);
        return close > open ? message[(open + 1)..close] : "on evidence_runs";
    }
}
