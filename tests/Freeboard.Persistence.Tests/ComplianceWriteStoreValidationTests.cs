using System.Data.Common;
using Freeboard.Persistence;

namespace Freeboard.Persistence.Tests;

/// <summary>
/// Unit tests for the input validation both app-managed disposition routes run BEFORE opening a
/// connection. These need no MySQL: a connection factory that throws on open proves the write is rejected
/// (and nothing is written) purely from the arguments. The DB-backed behaviour (target-column isolation,
/// uniqueness, concurrency) is covered by the gated integration tests.
/// </summary>
public sealed class ComplianceWriteStoreValidationTests
{
    // A factory whose OpenAsync throws, so any test that reaches the database fails loudly. A passing test
    // therefore proves the write returned before touching the store.
    private sealed class ThrowingConnectionFactory : IDbConnectionFactory
    {
        public Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The write must be rejected before opening a connection.");
    }

    private static MySqlComplianceWriteStore Store() => new(new ThrowingConnectionFactory());

    [Fact]
    public async Task StandardRouteRejectsOutWithNoJustificationBeforeTouchingTheStore()
    {
        var result = await Store().UpsertScopeDispositionAsync("sc-a", "T", "org-a", "std-a", "Out");

        Assert.False(result.Ok);
        Assert.Contains("justification", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequirementRouteRejectsOutWithNoJustificationBeforeTouchingTheStore()
    {
        var result = await Store().UpsertRequirementScopeDispositionAsync("sc-a", "T", "org-a", "req-a", "Out");

        Assert.False(result.Ok);
        Assert.Contains("justification", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BlankJustificationCountsAsMissingOnOut()
    {
        Assert.False((await Store().UpsertScopeDispositionAsync("sc-a", "T", "org-a", "std-a", "Out", "   ")).Ok);
        Assert.False((await Store().UpsertRequirementScopeDispositionAsync("sc-a", "T", "org-a", "req-a", "Out", "   ")).Ok);
    }

    [Fact]
    public async Task BothRoutesRejectAnInvalidDisposition()
    {
        Assert.False((await Store().UpsertScopeDispositionAsync("sc-a", "T", "org-a", "std-a", "Sideways", "r")).Ok);
        Assert.False((await Store().UpsertRequirementScopeDispositionAsync("sc-a", "T", "org-a", "req-a", "Sideways", "r")).Ok);
    }

    [Fact]
    public async Task BothRoutesRejectMissingIdOrTitle()
    {
        Assert.False((await Store().UpsertScopeDispositionAsync("", "T", "org-a", "std-a", "In")).Ok);
        Assert.False((await Store().UpsertScopeDispositionAsync("sc-a", "", "org-a", "std-a", "In")).Ok);
        Assert.False((await Store().UpsertRequirementScopeDispositionAsync("", "T", "org-a", "req-a", "In")).Ok);
        Assert.False((await Store().UpsertRequirementScopeDispositionAsync("sc-a", "", "org-a", "req-a", "In")).Ok);
    }
}
