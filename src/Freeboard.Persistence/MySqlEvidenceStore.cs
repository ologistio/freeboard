using System.Data;
using System.Data.Common;
using Dapper;

namespace Freeboard.Persistence;

/// <summary>
/// MySQL-backed <see cref="IEvidenceStore"/> using hand-written joined reads via Dapper. Runs are
/// ordered newest first and their checks by ordinal. The AssessmentResult projection is computed on
/// read under a <see cref="IsolationLevel.RepeatableRead"/> snapshot from <c>evidence_runs</c> and
/// <c>evidence_checks</c> only; it never reads scopes and never enumerates in-scope pairs.
/// </summary>
public sealed class MySqlEvidenceStore(IDbConnectionFactory connectionFactory) : IEvidenceStore
{
    private const string RunColumns =
        "id AS Id, kind AS Kind, organisation_id AS OrganisationId, requirement_id AS RequirementId, "
        + "vendor AS Vendor, collector_ref AS CollectorRef, result AS Result, collected_at AS CollectedAt, "
        + "received_at AS ReceivedAt, raw_payload AS RawPayload, created_at AS CreatedAt";

    // Pins a pair's latest run deterministically: the ULID id is a monotonic total-order tie-break, so
    // "latest" is never ambiguous when several runs share a collected_at.
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

    public async Task<IReadOnlyList<AssessmentResultRow>> GetAssessmentResultsAsync(
        string organisationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        // The runs and the checks they are assessed over must come from one snapshot so an append cannot
        // change the pinned latest run between the two queries.
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        // Ordered so the first row per requirement is that pair's latest run.
        var runs = (await connection.QueryAsync<(string Id, string RequirementId)>(new CommandDefinition(
            "SELECT id AS Id, requirement_id AS RequirementId FROM evidence_runs "
            + "WHERE organisation_id = @OrganisationId "
            + $"ORDER BY requirement_id, {LatestOrder};",
            new { OrganisationId = organisationId },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var latest = runs
            .GroupBy(r => r.RequirementId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        var results = new List<AssessmentResultRow>(latest.Count);
        if (latest.Count > 0)
        {
            var checks = (await connection.QueryAsync<(string EvidenceId, string Severity, string Result)>(
                new CommandDefinition(
                    "SELECT evidence_id AS EvidenceId, severity AS Severity, result AS Result "
                    + "FROM evidence_checks WHERE evidence_id IN @Ids;",
                    new { Ids = latest.Select(l => l.Id).ToArray() },
                    transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

            var checksByRun = checks
                .GroupBy(c => c.EvidenceId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            foreach (var run in latest)
            {
                var runChecks = checksByRun.TryGetValue(run.Id, out var list) ? list : [];
                results.Add(new AssessmentResultRow(organisationId, run.RequirementId, DeriveStatus(runChecks)));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    // Fail = failing. A failing Hard check fails the requirement; a failing Soft check only warns.
    private static string DeriveStatus(IReadOnlyList<(string EvidenceId, string Severity, string Result)> checks)
    {
        var hardFail = checks.Any(c =>
            string.Equals(c.Severity, "Hard", StringComparison.Ordinal) && string.Equals(c.Result, "Fail", StringComparison.Ordinal));
        if (hardFail)
        {
            return "HardFailure";
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
                attestationByRun.TryGetValue(r.Id, out var att) ? att : null))
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
        DateTime CreatedAt);
}
