using System.Data;
using System.Data.Common;
using Dapper;
using Freeboard.Core.GitOps;

namespace Freeboard.Persistence;

/// <summary>
/// MySQL-backed <see cref="IEvidenceStore"/> using hand-written joined reads via Dapper. Runs are
/// ordered newest first and their checks by ordinal. The per-collector status projection is computed on
/// read under a <see cref="IsolationLevel.RepeatableRead"/> snapshot from <c>evidence_runs</c> and
/// <c>evidence_checks</c> only; it never reads scopes and never enumerates configured collectors.
/// <paramref name="timeProvider"/> is the clock staleness is judged against; it defaults to
/// <see cref="TimeProvider.System"/> so existing construction sites keep compiling.
/// </summary>
public sealed class MySqlEvidenceStore(IDbConnectionFactory connectionFactory, TimeProvider? timeProvider = null)
    : IEvidenceStore
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    private const string RunColumns =
        "id AS Id, kind AS Kind, organisation_id AS OrganisationId, requirement_id AS RequirementId, "
        + "vendor AS Vendor, collector_ref AS CollectorRef, result AS Result, collected_at AS CollectedAt, "
        + "received_at AS ReceivedAt, raw_payload AS RawPayload, created_at AS CreatedAt, "
        + "collector_id AS CollectorId, frequency AS Frequency";

    // Pins a collector's latest run deterministically: the ULID id is a monotonic total-order tie-break,
    // so "latest" is never ambiguous when several runs share a collected_at.
    private const string LatestOrder =
        "collected_at DESC, received_at DESC, created_at DESC, id DESC";

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
        // The runs and the checks they are assessed over must come from one snapshot so an append cannot
        // change the pinned latest run between the two queries.
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        // Collector-kind runs only; ordered so the first row per (org, requirement, collector) group is
        // that group's latest run once grouping is applied in memory.
        var runs = (await connection.QueryAsync<StatusScalar>(new CommandDefinition(
            "SELECT id AS Id, organisation_id AS OrganisationId, requirement_id AS RequirementId, "
            + "collector_id AS CollectorId, collector_ref AS CollectorRef, frequency AS Frequency, "
            + "collected_at AS CollectedAt FROM evidence_runs "
            + "WHERE kind = 'Collector' AND organisation_id IN @OrganisationIds "
            + $"ORDER BY organisation_id, requirement_id, {LatestOrder};",
            new { OrganisationIds = orgIds },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        // Effective collector id: the first-class column, else the collector_ref prefix before ':' for a
        // pre-migration row. A run with no recoverable identity is not attributed to a collector.
        var latest = runs
            .Select(r => (Run: r, CollectorId: EffectiveCollectorId(r)))
            .Where(x => x.CollectorId is not null)
            .GroupBy(
                x => (x.Run.OrganisationId, x.Run.RequirementId, x.CollectorId!),
                x => x.Run,
                StringTupleComparer.Instance)
            .Select(g => (Group: g.Key, Run: g.First()))
            .ToList();

        var results = new List<CollectorEvidenceStatusRow>(latest.Count);
        if (latest.Count > 0)
        {
            var checks = (await connection.QueryAsync<(string EvidenceId, string Severity, string Result)>(
                new CommandDefinition(
                    "SELECT evidence_id AS EvidenceId, severity AS Severity, result AS Result "
                    + "FROM evidence_checks WHERE evidence_id IN @Ids;",
                    new { Ids = latest.Select(l => l.Run.Id).ToArray() },
                    transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

            var checksByRun = checks
                .GroupBy(c => c.EvidenceId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
            foreach (var (group, run) in latest)
            {
                var runChecks = checksByRun.TryGetValue(run.Id, out var list) ? list : [];
                var stale = EvidenceCollectorFrequency.IsStale(run.CollectedAt, run.Frequency, nowUtc);
                results.Add(new CollectorEvidenceStatusRow(
                    group.Item1, group.Item2, group.Item3, DeriveStatus(runChecks, stale), run.CollectedAt));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    private static string? EffectiveCollectorId(StatusScalar run)
    {
        if (!string.IsNullOrEmpty(run.CollectorId))
        {
            return run.CollectorId;
        }

        var delimiter = run.CollectorRef.IndexOf(':', StringComparison.Ordinal);
        return delimiter > 0 ? run.CollectorRef[..delimiter] : null;
    }

    // Precedence, most severe first: HardFailure > Stale > SoftFailure > Passing. A known hard failure is
    // the most actionable signal and not a false green, so it outranks Stale; Stale overrides an
    // otherwise-passing verdict because a stopped collector's last green is a false green.
    private static string DeriveStatus(
        IReadOnlyList<(string EvidenceId, string Severity, string Result)> checks, bool stale)
    {
        var hardFail = checks.Any(c =>
            string.Equals(c.Severity, "Hard", StringComparison.Ordinal) && string.Equals(c.Result, "Fail", StringComparison.Ordinal));
        if (hardFail)
        {
            return "HardFailure";
        }

        if (stale)
        {
            return "Stale";
        }

        var softFail = checks.Any(c =>
            string.Equals(c.Severity, "Soft", StringComparison.Ordinal) && string.Equals(c.Result, "Fail", StringComparison.Ordinal));
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
                r.CollectorId, r.Frequency))
            .ToList();
    }

    private sealed record RunScalar(
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
        string? CollectorId,
        string? Frequency);

    private sealed record StatusScalar(
        string Id,
        string OrganisationId,
        string RequirementId,
        string? CollectorId,
        string CollectorRef,
        string? Frequency,
        DateTime CollectedAt);

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
