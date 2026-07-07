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

/// <summary>
/// Records IFreeboardApiClient calls and returns canned ApiResults so the user commands run with
/// no live API and no database. Each method's return value is configurable; calls are counted so
/// tests can assert the command hit the API (and, implicitly, opened no DB connection - this fake
/// has no IDbConnectionFactory).
/// </summary>
internal sealed class FakeApiClient : IFreeboardApiClient
{
    public int CreateCalls { get; private set; }

    public int ListCalls { get; private set; }

    public int DisableCalls { get; private set; }

    public int EnableCalls { get; private set; }

    public int ResetCalls { get; private set; }

    public int BootstrapCalls { get; private set; }

    public int VendorListCalls { get; private set; }

    public int VendorScopeListCalls { get; private set; }

    public string? LastId { get; private set; }

    public ApiResult<CreatedUser> CreateResult { get; init; } =
        ApiResult<CreatedUser>.Success(new CreatedUser(SampleUser, "temp-create-pw"));

    public ApiResult<IReadOnlyList<ApiUser>> ListResult { get; init; } =
        ApiResult<IReadOnlyList<ApiUser>>.Success([SampleUser]);

    public ApiResult<Unit> DisableResult { get; init; } = ApiResult<Unit>.Success(Unit.Value);

    public ApiResult<Unit> EnableResult { get; init; } = ApiResult<Unit>.Success(Unit.Value);

    public ApiResult<ResetPassword> ResetResult { get; init; } =
        ApiResult<ResetPassword>.Success(new ResetPassword("temp-reset-pw"));

    public ApiResult<BootstrapResult> BootstrapResult { get; init; } =
        ApiResult<BootstrapResult>.Success(new BootstrapResult(SampleUser, "admin-token-xyz"));

    public ApiResult<IReadOnlyList<ApiVendor>> VendorListResult { get; init; } =
        ApiResult<IReadOnlyList<ApiVendor>>.Success([SampleVendor]);

    public ApiResult<IReadOnlyList<ApiVendorScope>> VendorScopeListResult { get; init; } =
        ApiResult<IReadOnlyList<ApiVendorScope>>.Success([SampleVendorScope]);

    public static ApiUser SampleUser { get; } =
        new("01HZZ0000000000000000000AA", "user@example.test", "User", "member", true);

    public static ApiVendor SampleVendor { get; } = new("vendor-a", "Vendor A");

    public static ApiVendorScope SampleVendorScope { get; } =
        new("vs-a", "Except req-a", "vendor-a", "req-a", null, "Out", "Supports MFA but not SSO.");

    public Task<ApiResult<CreatedUser>> CreateUserAsync(string email, string name, string role, CancellationToken ct)
    {
        CreateCalls++;
        return Task.FromResult(CreateResult);
    }

    public Task<ApiResult<IReadOnlyList<ApiUser>>> ListUsersAsync(CancellationToken ct)
    {
        ListCalls++;
        return Task.FromResult(ListResult);
    }

    public Task<ApiResult<Unit>> DisableUserAsync(string id, CancellationToken ct)
    {
        DisableCalls++;
        LastId = id;
        return Task.FromResult(DisableResult);
    }

    public Task<ApiResult<Unit>> EnableUserAsync(string id, CancellationToken ct)
    {
        EnableCalls++;
        LastId = id;
        return Task.FromResult(EnableResult);
    }

    public Task<ApiResult<ResetPassword>> ResetPasswordAsync(string id, CancellationToken ct)
    {
        ResetCalls++;
        LastId = id;
        return Task.FromResult(ResetResult);
    }

    public Task<ApiResult<BootstrapResult>> BootstrapAsync(
        string email, string name, string? password, string bootstrapSecret, CancellationToken ct)
    {
        BootstrapCalls++;
        return Task.FromResult(BootstrapResult);
    }

    public Task<ApiResult<IReadOnlyList<ApiVendor>>> ListVendorsAsync(CancellationToken ct)
    {
        VendorListCalls++;
        return Task.FromResult(VendorListResult);
    }

    public Task<ApiResult<IReadOnlyList<ApiVendorScope>>> ListVendorScopesAsync(CancellationToken ct)
    {
        VendorScopeListCalls++;
        return Task.FromResult(VendorScopeListResult);
    }
}
