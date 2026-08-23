using System.Data;
using System.Data.Common;
using Dapper;
using Freeboard.Core.GitOps;

namespace Freeboard.Persistence;

/// <summary>
/// MySQL-backed <see cref="IEvidenceStore"/> using hand-written joined reads via Dapper. Runs are
/// ordered newest first and their checks by ordinal. The per-collector status projection is computed on
/// read from <c>evidence_runs</c> and <c>evidence_checks</c> only; it never reads scopes and never
/// enumerates configured collectors. That projection is one statement, which reads one consistent
/// snapshot on its own, so the pin, the assessed set, and the check outcomes cannot straddle an append.
/// The two run reads still take a <see cref="IsolationLevel.RepeatableRead"/> snapshot, because each
/// assembles a run from several statements. <paramref name="timeProvider"/> is the clock staleness is
/// judged against; it defaults to <see cref="TimeProvider.System"/> so existing construction sites keep
/// compiling.
/// </summary>
public sealed class MySqlEvidenceStore(IDbConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    : IEvidenceStore
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    private const string RunColumns =
        "id AS Id, kind AS Kind, organisation_id AS OrganisationId, requirement_id AS RequirementId, "
        + "vendor AS Vendor, collector_ref AS CollectorRef, result AS Result, collected_at AS CollectedAt, "
        + "received_at AS ReceivedAt, raw_payload AS RawPayload, created_at AS CreatedAt, "
        + "collector_id AS CollectorId, frequency AS Frequency, asset_id AS AssetId, "
        + "cycle_id AS CycleId, error_detail AS ErrorDetail";

    // Pins a collector's latest run deterministically: the ULID id is a monotonic total-order tie-break,
    // so "latest" is never ambiguous when several runs share a collected_at.
    private const string LatestOrder =
        "collected_at DESC, received_at DESC, created_at DESC, id DESC";

    // The assessed set of every (organisation, requirement, collector) group, in one statement.
    //
    // The effective collector id follows the fallback rule: the first-class column when it is present
    // AND non-empty, else the collector_ref prefix before the first ':' (ingest composes the reference as
    // collector_id:run_id). An empty column is absence rather than an identity, a reference with no ':'
    // yields nothing, and a leading ':' yields nothing rather than an empty identity, so COALESCE cannot
    // express it - COALESCE reads '' as a present value. A run with no recoverable identity is excluded.
    //
    // A window function pins each group's latest run, and the outer WHERE returns the assessed set: the
    // pinned run alone when it carries no cycle, otherwise every run of the group sharing its cycle, so
    // one collection cycle is assessed as one outcome and a machine absent from the newest cycle stops
    // contributing. The two check outcomes aggregate server-side, so the result set is bounded by the
    // newest cycle rather than by the whole run history and no second round trip is needed.
    //
    // Every compared literal declares COLLATE utf8mb4_0900_bin with the _utf8mb4 introducer, because
    // kind, severity, and result carry no explicit collation and so inherit the case-insensitive server
    // default. That collation is binary and NO PAD, so these predicates accept exactly what the ordinal
    // C# comparison they replace accepts: neither a lowercase nor a space-padded value counts. The
    // empty-string test on collector_id takes the same collation for the NO PAD half of that rule. The
    // column's own utf8mb4_bin is PAD SPACE, which would read a space-only id as empty, where the
    // ordinal comparison reads it as a present identity.
    private const string StatusSql =
        $"""
        WITH identified AS (
            SELECT id, organisation_id, requirement_id, cycle_id, result, frequency, collected_at,
                   received_at, created_at,
                   CASE
                       WHEN collector_id IS NOT NULL
                            AND collector_id <> _utf8mb4'' COLLATE utf8mb4_0900_bin
                           THEN collector_id
                       WHEN collector_ref IS NOT NULL AND LOCATE(_utf8mb4':', collector_ref) > 1
                           THEN LEFT(collector_ref, LOCATE(_utf8mb4':', collector_ref) - 1)
                   END COLLATE utf8mb4_0900_bin AS effective_collector_id
            FROM evidence_runs
            WHERE kind = _utf8mb4'Collector' COLLATE utf8mb4_0900_bin
              AND organisation_id IN @OrganisationIds
        ),
        pinned AS (
            SELECT identified.*,
                   ROW_NUMBER() OVER (
                       PARTITION BY organisation_id COLLATE utf8mb4_0900_bin,
                                    requirement_id COLLATE utf8mb4_0900_bin,
                                    effective_collector_id
                       ORDER BY {LatestOrder}) AS pin
            FROM identified
            WHERE effective_collector_id IS NOT NULL
        ),
        latest AS (
            SELECT organisation_id, requirement_id, effective_collector_id,
                   cycle_id AS pinned_cycle_id, collected_at AS pinned_collected_at
            FROM pinned
            WHERE pin = 1
        )
        SELECT pinned.organisation_id AS OrganisationId,
               pinned.requirement_id AS RequirementId,
               pinned.effective_collector_id AS CollectorId,
               pinned.result AS Result,
               pinned.frequency AS Frequency,
               pinned.collected_at AS CollectedAt,
               latest.pinned_collected_at AS PinnedCollectedAt,
               EXISTS (
                   SELECT 1 FROM evidence_checks
                   WHERE evidence_checks.evidence_id = pinned.id
                     AND evidence_checks.severity = _utf8mb4'Hard' COLLATE utf8mb4_0900_bin
                     AND evidence_checks.result = _utf8mb4'Fail' COLLATE utf8mb4_0900_bin
               ) AS HasHardFailure,
               EXISTS (
                   SELECT 1 FROM evidence_checks
                   WHERE evidence_checks.evidence_id = pinned.id
                     AND evidence_checks.severity = _utf8mb4'Soft' COLLATE utf8mb4_0900_bin
                     AND evidence_checks.result = _utf8mb4'Fail' COLLATE utf8mb4_0900_bin
               ) AS HasSoftFailure
        FROM pinned
        JOIN latest
          ON latest.organisation_id = pinned.organisation_id COLLATE utf8mb4_0900_bin
         AND latest.requirement_id = pinned.requirement_id COLLATE utf8mb4_0900_bin
         AND latest.effective_collector_id = pinned.effective_collector_id
        WHERE (latest.pinned_cycle_id IS NULL AND pinned.pin = 1)
           OR (latest.pinned_cycle_id IS NOT NULL AND pinned.cycle_id = latest.pinned_cycle_id);
        """;

    public async Task<IReadOnlyList<EvidenceRunRow>> GetEvidenceRunsAsync(
        string organisationId, string requirementId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // One snapshot so a run and its checks/extension cannot straddle a concurrent append commit.
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        var runs = (await connection.QueryAsync<RunScalar>(new CommandDefinition(
            $"SELECT {RunColumns} FROM evidence_runs "
            + "WHERE organisation_id = @OrganisationId AND requirement_id = @RequirementId "
            + $"ORDER BY {LatestOrder};",
            new { OrganisationId = organisationId, RequirementId = requirementId },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var assembled = await AssembleRunsAsync(connection, transaction, runs, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return assembled;
    }

    public async Task<EvidenceRunRow?> GetLatestEvidenceRunAsync(
        string organisationId, string requirementId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        var run = await connection.QuerySingleOrDefaultAsync<RunScalar>(new CommandDefinition(
            $"SELECT {RunColumns} FROM evidence_runs "
            + "WHERE organisation_id = @OrganisationId AND requirement_id = @RequirementId "
            + $"ORDER BY {LatestOrder} LIMIT 1;",
            new { OrganisationId = organisationId, RequirementId = requirementId },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (run is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var assembled = await AssembleRunsAsync(connection, transaction, [run], cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return assembled[0];
    }

    public async Task<IReadOnlyList<CollectorEvidenceStatusRow>> GetCollectorEvidenceStatusesAsync(
        IReadOnlyCollection<string> organisationIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organisationIds);
        if (organisationIds.Count == 0)
        {
            return [];
        }

        var orgIds = organisationIds.ToArray();

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var assessed = (await connection.QueryAsync<StatusScalar>(new CommandDefinition(
            StatusSql,
            new { OrganisationIds = orgIds },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        return assessed
            .GroupBy(
                r => (r.OrganisationId, r.RequirementId, r.CollectorId),
                StringTupleComparer.Instance)
            .Select(g => new CollectorEvidenceStatusRow(
                g.Key.Item1, g.Key.Item2, g.Key.Item3, DeriveStatus(g, nowUtc), g.First().PinnedCollectedAt))
            .ToList();
    }

    // Precedence over the assessed set, most severe first:
    // HardFailure > Errored > Stale > SoftFailure > Passing.
    //
    // HardFailure outranks Errored because an observed breach is actionable and is never a false green,
    // and red is reserved for exactly that breach; an error only means the answer is not known there. This
    // holds whether the failing check and the error come from different runs of one cycle or from one
    // partially collected run, because the failing check WAS observed. Errored outranks Stale because it
    // is the sharper and fresher statement of the same problem: a collection that failed is failing now,
    // where Stale only says the last collection is overdue. Errored is the one status read from a run's
    // own result rather than from its checks, because an errored run usually has no checks to derive from.
    private static string DeriveStatus(IEnumerable<StatusScalar> assessed, DateTime nowUtc)
    {
        var errored = false;
        var stale = false;
        var softFail = false;

        foreach (var run in assessed)
        {
            if (run.HardFailure)
            {
                return "HardFailure";
            }

            errored |= string.Equals(run.Result, "Error", StringComparison.Ordinal);
            stale |= CollectorFrequency.IsStale(run.CollectedAt, run.Frequency, nowUtc);
            softFail |= run.SoftFailure;
        }

        if (errored)
        {
            return "Errored";
        }

        if (stale)
        {
            return "Stale";
        }

        return softFail ? "SoftFailure" : "Passing";
    }

    private static async Task<IReadOnlyList<EvidenceRunRow>> AssembleRunsAsync(
        DbConnection connection, DbTransaction transaction, IReadOnlyList<RunScalar> runs, CancellationToken cancellationToken)
    {
        if (runs.Count == 0)
        {
            return [];
        }

        var ids = runs.Select(r => r.Id).ToArray();

        var checks = (await connection.QueryAsync<EvidenceCheckRow>(new CommandDefinition(
            "SELECT id AS Id, evidence_id AS EvidenceId, name AS Name, severity AS Severity, "
            + "result AS Result, ordinal AS Ordinal, detail AS Detail FROM evidence_checks "
            + "WHERE evidence_id IN @Ids ORDER BY evidence_id, ordinal;",
            new { Ids = ids }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var attestations = (await connection.QueryAsync<AttestationResponseRow>(new CommandDefinition(
            "SELECT evidence_id AS EvidenceId, user_id AS UserId, quiz_passed AS QuizPassed, score AS Score "
            + "FROM attestation_responses WHERE evidence_id IN @Ids;",
            new { Ids = ids }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var checksByRun = checks
            .GroupBy(c => c.EvidenceId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<EvidenceCheckRow>)g.ToList(), StringComparer.Ordinal);
        var attestationByRun = attestations.ToDictionary(a => a.EvidenceId, StringComparer.Ordinal);

        return runs
            .Select(r => new EvidenceRunRow(
                r.Id, r.Kind, r.OrganisationId, r.RequirementId, r.Vendor, r.CollectorRef, r.Result,
                r.CollectedAt, r.ReceivedAt, r.RawPayload, r.CreatedAt,
                checksByRun.TryGetValue(r.Id, out var runChecks) ? runChecks : [],
                attestationByRun.TryGetValue(r.Id, out var att) ? att : null,
                r.CollectorId, r.Frequency, r.AssetId, r.CycleId, r.ErrorDetail))
            .ToList();
    }

    private sealed record RunScalar(
        string Id,
        string Kind,
        string OrganisationId,
        string RequirementId,
        string? Vendor,
        string? CollectorRef,
        string Result,
        DateTime CollectedAt,
        DateTime? ReceivedAt,
        string? RawPayload,
        DateTime CreatedAt,
        string? CollectorId,
        string? Frequency,
        string? AssetId,
        string? CycleId,
        string? ErrorDetail);

    // One run of the assessed set, with its group key and its check outcomes already aggregated by the
    // server. PinnedCollectedAt is the group's latest run's collected_at, repeated on every row of the
    // set, and is what the returned status reports as LastCollectedAt.
    private sealed record StatusScalar(
        string OrganisationId,
        string RequirementId,
        string CollectorId,
        string Result,
        string? Frequency,
        DateTime CollectedAt,
        DateTime PinnedCollectedAt,
        long HasHardFailure,
        long HasSoftFailure)
    {
        // MySQL has no boolean type, so each EXISTS comes back as 1 or 0.
        public bool HardFailure => HasHardFailure != 0;

        public bool SoftFailure => HasSoftFailure != 0;
    }

    // Ordinal, case-sensitive equality across the (organisation, requirement, collector) grouping key,
    // consistent with the exact-byte id identity used everywhere else.
    private sealed class StringTupleComparer : IEqualityComparer<(string, string, string)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string, string, string) x, (string, string, string) y) =>
            string.Equals(x.Item1, y.Item1, StringComparison.Ordinal)
            && string.Equals(x.Item2, y.Item2, StringComparison.Ordinal)
            && string.Equals(x.Item3, y.Item3, StringComparison.Ordinal);

        public int GetHashCode((string, string, string) obj) =>
            HashCode.Combine(obj.Item1, obj.Item2, obj.Item3);
    }
}
