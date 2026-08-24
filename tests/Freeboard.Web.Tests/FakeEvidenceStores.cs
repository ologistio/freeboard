using System.Data.Common;
using Freeboard.Core.GitOps;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// A DbException stand-in the fakes throw to simulate a down store. It derives from DbException so it
/// matches the shared store-failure predicate (DbException, InvalidOperationException, or
/// TimeoutException), exactly as a real MySqlConnector.MySqlException would.
/// </summary>
internal sealed class FakeStoreDbException(string message) : DbException(message);

/// <summary>
/// In-memory <see cref="IEvidenceWriteStore"/> double. Keyed the way the real store is: on
/// <c>(vendor, collector_ref)</c> for a run carrying the producer identity, and on
/// <c>(cycle_id, organisation_id, requirement_id, asset_id)</c> otherwise. A re-append of the same key
/// returns <see cref="WriteResult.Conflict"/> (the idempotency collision), mirroring the real store's two
/// unique keys. Keying every run on the producer pair alone would make every cycle-keyed run collide on a
/// pair of nulls. <see cref="Unreachable"/> makes every append throw to simulate a down store. Appended
/// runs are recorded so tests can assert the mapped <see cref="NewEvidenceRun"/>.
/// </summary>
internal sealed class FakeEvidenceWriteStore : IEvidenceWriteStore
{
    private readonly HashSet<(string?, string?, string?, string?)> _keys = [];

    public bool Unreachable { get; init; }

    /// <summary>When set, every append throws it, to exercise a specific store-failure exception type.</summary>
    public Exception? Fault { get; init; }

    /// <summary>When set, the FIRST append returns this failing result instead of succeeding.</summary>
    public WriteResult? FailFirstWith { get; set; }

    public List<NewEvidenceRun> Appended { get; } = [];

    public Task<WriteResult> AppendEvidenceAsync(NewEvidenceRun run, CancellationToken cancellationToken = default)
    {
        if (Fault is not null)
        {
            throw Fault;
        }

        if (Unreachable)
        {
            throw new FakeStoreDbException("evidence store unreachable");
        }

        if (FailFirstWith is { } forced)
        {
            FailFirstWith = null;
            return Task.FromResult(forced);
        }

        var key = run.Vendor is not null || run.CollectorRef is not null
            ? (run.Vendor, run.CollectorRef, null, null)
            : (run.CycleId, run.OrganisationId, run.RequirementId, run.AssetId ?? string.Empty);
        if (!_keys.Add(key))
        {
            return Task.FromResult(WriteResult.Conflict(
                "This evidence already exists (duplicate vendor/collector reference, duplicate collection "
                + "cycle for this organisation, requirement and machine, or duplicate check name)."));
        }

        Appended.Add(run);
        return Task.FromResult(WriteResult.Success);
    }

    public Task<WriteResult> AppendAttestationResponseAsync(
        NewEvidenceRun run, NewAttestationResponse attestation, CancellationToken cancellationToken = default)
        => AppendEvidenceAsync(run, cancellationToken);
}

/// <summary>
/// In-memory <see cref="IEvidenceStore"/> read double. Holds seeded runs and returns one status per
/// <c>(organisation, requirement, collector)</c> - not a full-history dump - so a render test exercises
/// realistic store output. It pins the latest run with the same tie-break as the real store, applies the
/// shared staleness rule, and folds the same precedence,
/// <c>HardFailure &gt; Errored &gt; Stale &gt; SoftFailure &gt; Passing</c>. It does NOT model the real
/// store's assessed set: the real store widens the set to every run sharing the pinned run's
/// <c>cycle_id</c>, and this double always assesses the pinned run alone, which is the real store's
/// behaviour for a run that carries no cycle. Seed one run per collector to stay inside what it models.
/// <see cref="Clock"/> is the clock staleness is judged against (default system).
/// </summary>
internal sealed class FakeEvidenceStore : IEvidenceStore
{
    // Same latest-run tie-break as the real store: collected_at, received_at, created_at, id descending.
    private static readonly Comparison<EvidenceRunRow> LatestFirst = (a, b) =>
    {
        var c = b.CollectedAt.CompareTo(a.CollectedAt);
        if (c != 0)
        {
            return c;
        }

        c = Nullable.Compare(b.ReceivedAt, a.ReceivedAt);
        if (c != 0)
        {
            return c;
        }

        c = b.CreatedAt.CompareTo(a.CreatedAt);
        return c != 0 ? c : string.CompareOrdinal(b.Id, a.Id);
    };

    public bool Unreachable { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public List<EvidenceRunRow> Runs { get; } = [];

    /// <summary>Seeds a collector run whose run-overall result is <c>Pass</c>, with its checks.</summary>
    public FakeEvidenceStore AddCollectorRun(
        string organisationId, string requirementId, string collectorId, string? frequency,
        DateTime collectedAt, params (string Severity, string Result)[] checks)
        => AddCollectorRun(organisationId, requirementId, collectorId, frequency, collectedAt, "Pass", checks);

    /// <summary>
    /// Seeds a collector run with an explicit run-overall <paramref name="result"/>, so a test can stage
    /// an errored collection. Id and created_at follow insertion for a stable order.
    /// </summary>
    public FakeEvidenceStore AddCollectorRun(
        string organisationId, string requirementId, string collectorId, string? frequency,
        DateTime collectedAt, string result, params (string Severity, string Result)[] checks)
    {
        var n = Runs.Count + 1;
        var checkRows = checks
            .Select((c, i) => new EvidenceCheckRow($"chk-{n}-{i}", $"run-{n:D22}", $"c{i}", c.Severity, c.Result, i, null))
            .ToList();
        Runs.Add(new EvidenceRunRow(
            $"run-{n:D22}", "Collector", organisationId, requirementId, "vendor", $"{collectorId}:run{n}",
            result, collectedAt, collectedAt, null, collectedAt, checkRows, null, collectorId, frequency,
            null, null, string.Equals(result, "Error", StringComparison.Ordinal) ? "collection failed" : null));
        return this;
    }

    public Task<IReadOnlyList<EvidenceRunRow>> GetEvidenceRunsAsync(
        string organisationId, string requirementId, CancellationToken cancellationToken = default)
    {
        Guard();
        var runs = Runs
            .Where(r => string.Equals(r.OrganisationId, organisationId, StringComparison.Ordinal)
                && string.Equals(r.RequirementId, requirementId, StringComparison.Ordinal))
            .ToList();
        runs.Sort(LatestFirst);
        return Task.FromResult<IReadOnlyList<EvidenceRunRow>>(runs);
    }

    public async Task<EvidenceRunRow?> GetLatestEvidenceRunAsync(
        string organisationId, string requirementId, CancellationToken cancellationToken = default)
        => (await GetEvidenceRunsAsync(organisationId, requirementId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault();

    public Task<IReadOnlyList<CollectorEvidenceStatusRow>> GetCollectorEvidenceStatusesAsync(
        IReadOnlyCollection<string> organisationIds, CancellationToken cancellationToken = default)
    {
        Guard();
        var orgs = organisationIds.ToHashSet(StringComparer.Ordinal);
        var nowUtc = Clock.GetUtcNow().UtcDateTime;

        var results = Runs
            .Where(r => string.Equals(r.Kind, "Collector", StringComparison.Ordinal) && orgs.Contains(r.OrganisationId))
            .Select(r => (Run: r, CollectorId: EffectiveCollectorId(r)))
            .Where(x => x.CollectorId is not null)
            .GroupBy(
                x => (x.Run.OrganisationId, x.Run.RequirementId, x.CollectorId!),
                x => x.Run)
            .Select(g =>
            {
                var latest = g.OrderBy(r => r, Comparer<EvidenceRunRow>.Create(LatestFirst)).First();
                var stale = CollectorFrequency.IsStale(latest.CollectedAt, latest.Frequency, nowUtc);
                return new CollectorEvidenceStatusRow(
                    g.Key.OrganisationId, g.Key.RequirementId, g.Key.Item3, DeriveStatus(latest, stale),
                    latest.CollectedAt);
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<CollectorEvidenceStatusRow>>(results);
    }

    // The real store states this fallback rule once, in the SQL of its status query. This double has no
    // database behind it, so it needs its own copy: a deliberate stand-in, not a second statement of the
    // rule to be deleted as a duplicate. A run carrying no legacy reference has none to fall back to.
    private static string? EffectiveCollectorId(EvidenceRunRow run)
    {
        if (!string.IsNullOrEmpty(run.CollectorId))
        {
            return run.CollectorId;
        }

        if (run.CollectorRef is null)
        {
            return null;
        }

        var delimiter = run.CollectorRef.IndexOf(':', StringComparison.Ordinal);
        return delimiter > 0 ? run.CollectorRef[..delimiter] : null;
    }

    private static string DeriveStatus(EvidenceRunRow run, bool stale)
    {
        if (run.Checks.Any(c => c.Severity == "Hard" && c.Result == "Fail"))
        {
            return "HardFailure";
        }

        if (string.Equals(run.Result, "Error", StringComparison.Ordinal))
        {
            return "Errored";
        }

        if (stale)
        {
            return "Stale";
        }

        return run.Checks.Any(c => c.Severity == "Soft" && c.Result == "Fail") ? "SoftFailure" : "Passing";
    }

    private void Guard()
    {
        if (Unreachable)
        {
            throw new FakeStoreDbException("evidence store unreachable");
        }
    }
}

/// <summary>
/// In-memory <see cref="ICollectorCredentialStore"/> double. Stores rows in a list; lookup is by
/// keyed-HMAC hash via a byte-wise compare (mirroring the real store's unique token_hash). Seed a
/// credential with <see cref="Add"/>. <see cref="Unreachable"/> makes issue/find throw.
/// </summary>
internal sealed class FakeCollectorCredentialStore : ICollectorCredentialStore
{
    private readonly List<(byte[] Hash, CollectorCredentialRow Row)> _rows = [];

    public bool Unreachable { get; init; }

    /// <summary>When set, issue throws it, to exercise a specific store-failure exception type.</summary>
    public Exception? Fault { get; init; }

    public int NextId { get; set; } = 1;

    /// <summary>Seeds a credential row against a token hash. Returns the row.</summary>
    public CollectorCredentialRow Add(byte[] tokenHash, CollectorCredentialRow row)
    {
        _rows.Add((tokenHash, row));
        return row;
    }

    public Task<CollectorCredentialRow?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken = default)
    {
        if (Unreachable)
        {
            throw new FakeStoreDbException("credential store unreachable");
        }

        foreach (var (hash, row) in _rows)
        {
            if (hash.AsSpan().SequenceEqual(tokenHash))
            {
                return Task.FromResult<CollectorCredentialRow?>(row);
            }
        }

        return Task.FromResult<CollectorCredentialRow?>(null);
    }

    public Task<string> IssueAsync(
        string collectorId, byte[] tokenHash, int tokenKeyVersion, DateTime? expiresAt,
        CancellationToken cancellationToken = default)
    {
        if (Fault is not null)
        {
            throw Fault;
        }

        if (Unreachable)
        {
            throw new FakeStoreDbException("credential store unreachable");
        }

        var id = $"cred-{NextId++}";
        _rows.Add((tokenHash, new CollectorCredentialRow(
            id, collectorId, tokenKeyVersion, DateTime.UtcNow, null, expiresAt, null)));
        return Task.FromResult(id);
    }

    public Task<bool> RevokeAsync(string collectorId, string credentialId, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var (hash, row) = _rows[i];
            if (string.Equals(row.Id, credentialId, StringComparison.Ordinal)
                && string.Equals(row.CollectorId, collectorId, StringComparison.Ordinal)
                && row.RevokedAt is null)
            {
                _rows[i] = (hash, row with { RevokedAt = DateTime.UtcNow });
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    public Task<bool> TouchLastSeenAsync(string credentialId, DateTime seenAt, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var (hash, row) = _rows[i];
            if (string.Equals(row.Id, credentialId, StringComparison.Ordinal))
            {
                _rows[i] = (hash, row with { LastSeenAt = seenAt });
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }
}
