using Freeboard.Core.GitOps;
using Freeboard.Persistence.GitOps;
using Freeboard.Persistence.System;

namespace Freeboard.CLI.Tests;

/// <summary>Records ImportAsync calls for assertions.</summary>
internal sealed class FakeImporter : IGitOpsImporter
{
    public int Calls { get; private set; }

    public GitOpsConfig? LastConfig { get; private set; }

    public Task ImportAsync(GitOpsConfig config, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastConfig = config;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Configurable migration runner double. <see cref="Current"/> drives the
/// migrate-first gate; <see cref="ThrowOnApply"/>/<see cref="ThrowOnState"/> simulate
/// failures.
/// </summary>
internal sealed class FakeMigrationRunner : IMigrationRunner
{
    public bool Current { get; init; } = true;

    public MigrationException? ThrowOnApply { get; init; }

    public MigrationException? ThrowOnState { get; init; }

    /// <summary>
    /// When set, GetState returns a side-effect-free integrity-violated state (IsCorrupt)
    /// carrying this message, instead of throwing. Mirrors the read-path integrity report.
    /// </summary>
    public string? StateIntegrityError { get; init; }

    public int ApplyCalls { get; private set; }

    public int StateCalls { get; private set; }

    public Task<MigrationState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        StateCalls++;
        if (ThrowOnState is not null)
        {
            throw ThrowOnState;
        }

        if (StateIntegrityError is not null)
        {
            return Task.FromResult(new MigrationState(["001"], [], StateIntegrityError));
        }

        return Task.FromResult(Current
            ? new MigrationState(["001"], [])
            : new MigrationState([], ["001"]));
    }

    public Task<IReadOnlyList<string>> ApplyPendingAsync(CancellationToken cancellationToken = default)
    {
        ApplyCalls++;
        if (ThrowOnApply is not null)
        {
            throw ThrowOnApply;
        }

        return Task.FromResult<IReadOnlyList<string>>(["001"]);
    }
}
