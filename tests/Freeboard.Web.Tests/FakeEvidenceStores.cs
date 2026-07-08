using System.Data.Common;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// A DbException stand-in the fakes throw to simulate a down store. It derives from DbException so it
/// matches the shared store-failure predicate (DbException, InvalidOperationException, or
/// TimeoutException), exactly as a real MySqlConnector.MySqlException would.
/// </summary>
internal sealed class FakeStoreDbException(string message) : DbException(message);

/// <summary>
/// In-memory <see cref="IEvidenceIngestStore"/> double. Keyed on <c>(collector_id, run_id)</c>: an
/// identical body replays, a changed body conflicts. <see cref="Unreachable"/> makes every append throw
/// to simulate a down store. Appended runs are recorded so tests can assert the snapshot and counts.
/// </summary>
internal sealed class FakeEvidenceIngestStore : IEvidenceIngestStore
{
    private readonly Dictionary<(string, string), Stored> _byKey = new();

    public bool Unreachable { get; init; }

    /// <summary>When set, every append throws it, to exercise a specific store-failure exception type.</summary>
    public Exception? Fault { get; init; }

    public List<EvidenceRunInput> Appended { get; } = [];

    public Task<EvidenceAppendResult> TryAppendAsync(EvidenceRunInput run, CancellationToken cancellationToken = default)
    {
        if (Fault is not null)
        {
            throw Fault;
        }

        if (Unreachable)
        {
            throw new FakeStoreDbException("evidence store unreachable");
        }

        var key = (run.CollectorId, run.RunId);
        if (_byKey.TryGetValue(key, out var existing))
        {
            var matches = existing.Hash.AsSpan().SequenceEqual(run.RequestBodySha256);
            return Task.FromResult(new EvidenceAppendResult(
                existing.EvidenceId, WasNew: false, BodyMatches: matches, existing.ReceivedAt,
                existing.HardFailCount, existing.SoftFailCount, existing.TotalCount));
        }

        var id = $"ev-{_byKey.Count + 1}";
        var receivedAt = DateTime.UtcNow;
        _byKey[key] = new Stored(
            id, run.RequestBodySha256, receivedAt, run.HardFailCount, run.SoftFailCount, run.TotalCount);
        Appended.Add(run);
        return Task.FromResult(new EvidenceAppendResult(
            id, WasNew: true, BodyMatches: true, receivedAt,
            run.HardFailCount, run.SoftFailCount, run.TotalCount));
    }

    private sealed record Stored(
        string EvidenceId, byte[] Hash, DateTime ReceivedAt, int HardFailCount, int SoftFailCount, int TotalCount);
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
