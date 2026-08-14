using System.Net;
using Freeboard.Compliance;
using Freeboard.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Freeboard.Web.Tests;

/// <summary>
/// Reading a list a snapshot does not name is a programming error, and it has to fail as one. It throws
/// <see cref="ComplianceReadSetNotRequestedException"/> rather than reading as empty, and the read
/// endpoints do NOT turn that into their store-unreachable 503: an operator must not be sent to check
/// the database over a missing flag at a call site. It surfaces as a 500 instead.
/// </summary>
public sealed class ComplianceUnrequestedReadSetTests
{
    private const string Scopes = "/api/v1/freeboard/scopes";

    [Fact]
    public void ReadingAnUnrequestedListThrowsAndNamesTheSet()
    {
        var snapshot = new ComplianceSnapshot(ComplianceReadSet.Assets, assets: []);

        var ex = Assert.Throws<ComplianceReadSetNotRequestedException>(() => snapshot.Scopes);

        Assert.Equal(ComplianceReadSet.Scopes, ex.Set);
        Assert.Contains("Scopes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AListSuppliedForASetTheSnapshotDidNotNameIsStillRefused()
    {
        // The declared sets are the contract, not a hint about which fields happen to be populated. A
        // snapshot that served an undeclared list would let a decision draw on rows its shape never
        // named, and the structural assertions on Sets would stop meaning anything.
        var snapshot = new ComplianceSnapshot(ComplianceReadSet.Assets, assets: [], scopes: []);

        var ex = Assert.Throws<ComplianceReadSetNotRequestedException>(() => snapshot.Scopes);

        Assert.Equal(ComplianceReadSet.Scopes, ex.Set);
        Assert.Empty(snapshot.Assets);
    }

    [Fact]
    public void TheThrowIsNotAStoreFailure()
    {
        // The read paths catch DbException, InvalidOperationException and TimeoutException and report
        // "compliance store unreachable". This type must sit outside that catch, so the endpoint filter
        // is asked directly rather than inferred from a status code.
        var ex = new ComplianceReadSetNotRequestedException(ComplianceReadSet.Scopes);

        Assert.False(ComplianceEndpoints.IsStoreFailure(ex));
        Assert.IsNotAssignableFrom<InvalidOperationException>(ex);
    }

    [Fact]
    public async Task AReadEndpointDoesNotConvertItIntoAServiceUnavailable()
    {
        // The store answers every ask with an assets-only snapshot, so the endpoint's read of the scopes
        // it named throws. It must reach the client as a 500 and never as the 503 problem document.
        using var factory = new UnderReadingFactory();
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("m1"));

        var response = await client.GetAsync(Scopes);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(
            "unreachable", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheSameEndpointStillReturnsServiceUnavailableOnARealStoreFailure()
    {
        // The contrast that gives the case above its meaning: the 503 path is live on this route, so the
        // 500 above is the endpoint declining to treat a programming error as an outage.
        using var factory = new AuthWebFactory { Compliance = new FakeComplianceStore { Unreachable = true } };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("m1"));

        var response = await client.GetAsync(Scopes);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>Answers every ask with an assets-only snapshot, however wide the ask was.</summary>
    private sealed class UnderReadingStore : IComplianceStore
    {
        public Task<ComplianceSnapshot> GetSnapshotAsync(
            ComplianceReadSet sets, CancellationToken cancellationToken = default)
            => Task.FromResult(new ComplianceSnapshot(ComplianceReadSet.Assets, assets: []));

        public Task<ComplianceCounts> GetCountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ComplianceCounts(0, 0, 0, 0, 0, 0, 0));
    }

    private sealed class UnderReadingFactory : AuthWebFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IComplianceStore>();
                services.AddSingleton<IComplianceStore>(new UnderReadingStore());
            });
        }
    }
}
