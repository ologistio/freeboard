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
/// In-memory <see cref="IEvidenceWriteStore"/> double. Keyed on <c>(vendor, collector_ref)</c>: a
/// re-append of the same key returns <see cref="WriteResult.Conflict"/> (the idempotency collision),
/// mirroring the real store's unique key. <see cref="Unreachable"/> makes every append throw to simulate
/// a down store. Appended runs are recorded so tests can assert the mapped <see cref="NewEvidenceRun"/>.
/// </summary>
internal sealed class FakeEvidenceWriteStore : IEvidenceWriteStore
{
    private readonly HashSet<(string, string)> _keys = [];

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

        var key = (run.Vendor, run.CollectorRef);
        if (!_keys.Add(key))
        {
            return Task.FromResult(WriteResult.Conflict(
                "This evidence already exists (duplicate vendor/collector reference or check name)."));
        }

        Appended.Add(run);
        return Task.FromResult(WriteResult.Success);
    }

    public Task<WriteResult> AppendAttestationResponseAsync(
        NewEvidenceRun run, NewAttestationResponse attestation, CancellationToken cancellationToken = default)
        => AppendEvidenceAsync(run, cancellationToken);
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
