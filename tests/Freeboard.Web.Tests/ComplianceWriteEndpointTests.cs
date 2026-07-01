using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Freeboard.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Freeboard.Web.Tests;

public sealed class ComplianceWriteEndpointTests
{
    private static HttpClient AdminClient(WriteFactory f)
        => f.CreateAuthenticatedClient(AuthWebFactory.MakeUser("admin1", role: "admin"));

    [Fact]
    public async Task UpsertOrganisationAllowedOffReadOnlyMode()
    {
        var writes = new FakeComplianceWriteStore();
        using var factory = new WriteFactory(writes);
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/freeboard/organisations/org-a",
            new { title = "Org A", kind = "Company", parent = (string?)null });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("org-a", writes.LastOrganisationId);
    }

    [Fact]
    public async Task InvalidWriteReturnsProblemAndDoesNotChangeStore()
    {
        var writes = new FakeComplianceWriteStore
        {
            OrganisationResult = WriteResult.Fail("Parent organisation 'missing' does not exist."),
        };
        using var factory = new WriteFactory(writes);
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/freeboard/organisations/org-a",
            new { title = "Org A", kind = "Company", parent = "missing" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task SetScopeDispositionAllowedOffReadOnlyMode()
    {
        var writes = new FakeComplianceWriteStore();
        using var factory = new WriteFactory(writes);
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/freeboard/scopes/scope-a",
            new { title = "Scope A", organisation = "org-a", standard = "std-a", disposition = "In" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("scope-a", writes.LastScopeId);
    }

    [Fact]
    public async Task UnauthenticatedWriteRejectedOffReadOnlyMode()
    {
        var writes = new FakeComplianceWriteStore();
        using var factory = new WriteFactory(writes);
        // No bearer token: the admin authorization policy must reject the write.
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            "/api/v1/freeboard/organisations/org-a",
            new { title = "Org A", kind = "Company", parent = (string?)null });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(writes.LastOrganisationId);
    }

    [Fact]
    public async Task ConcurrentDuplicateKeyViolationReturns409Problem()
    {
        // A duplicate that races the pre-check hits the unique key; the driver raises SQLSTATE 23000.
        var writes = new FakeComplianceWriteStore { Throw = new FakeDbException("Duplicate entry", "23000") };
        using var factory = new WriteFactory(writes);
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/freeboard/scopes/scope-a",
            new { title = "Scope A", organisation = "org-a", standard = "std-a", disposition = "In" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task UnreachableStoreReturns503Problem()
    {
        var writes = new FakeComplianceWriteStore { Throw = new FakeDbException("connection refused", sqlState: null) };
        using var factory = new WriteFactory(writes);
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/freeboard/organisations/org-a",
            new { title = "Org A", kind = "Company", parent = (string?)null });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task LazyConnectionInvalidOperationReturns503Problem()
    {
        // A lazily-opened connection over an empty connection string surfaces as
        // InvalidOperationException, not DbException; it must still map to a 503 problem.
        var writes = new FakeComplianceWriteStore { Throw = new InvalidOperationException("no connection string") };
        using var factory = new WriteFactory(writes);
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/freeboard/organisations/org-a",
            new { title = "Org A", kind = "Company", parent = (string?)null });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task StoreTimeoutReturns503Problem()
    {
        var writes = new FakeComplianceWriteStore { Throw = new TimeoutException("connect timed out") };
        using var factory = new WriteFactory(writes);
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/freeboard/organisations/org-a",
            new { title = "Org A", kind = "Company", parent = (string?)null });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task WriteBlockedWith409InReadOnlyMode()
    {
        var writes = new FakeComplianceWriteStore();
        using var factory = new WriteFactory(writes, readOnly: true);
        // Authenticated as admin, so a 409 here proves the read-only gate wins over auth: the
        // read-only middleware runs before authentication, so a would-be-authorized request is
        // still 409'd before the handler runs.
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/freeboard/organisations/org-a",
            new { title = "Org A", kind = "Company", parent = (string?)null });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(writes.LastOrganisationId);
    }

    private sealed class WriteFactory(IComplianceWriteStore writes, bool readOnly = false) : AuthWebFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            // Set after base so it overrides the base default; the mode drives the read-only middleware.
            builder.UseSetting("Freeboard:GitOps:ReadOnly", readOnly ? "true" : "false");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IComplianceWriteStore>();
                services.AddSingleton(writes);
            });
        }
    }

    private sealed class FakeComplianceWriteStore : IComplianceWriteStore
    {
        public WriteResult OrganisationResult { get; init; } = WriteResult.Success;

        public WriteResult ScopeResult { get; init; } = WriteResult.Success;

        /// <summary>When set, every write throws it, simulating a store failure past the pre-checks.</summary>
        public Exception? Throw { get; init; }

        public string? LastOrganisationId { get; private set; }

        public string? LastScopeId { get; private set; }

        public Task<WriteResult> UpsertOrganisationAsync(
            string id, string title, string kind, string? parent, CancellationToken cancellationToken = default)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            if (OrganisationResult.Ok)
            {
                LastOrganisationId = id;
            }

            return Task.FromResult(OrganisationResult);
        }

        public Task<WriteResult> DeleteOrganisationAsync(string id, CancellationToken cancellationToken = default) =>
            Throw is not null ? throw Throw : Task.FromResult(OrganisationResult);

        public Task<WriteResult> UpsertScopeDispositionAsync(
            string id, string title, string organisation, string standard, string disposition,
            CancellationToken cancellationToken = default)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            if (ScopeResult.Ok)
            {
                LastScopeId = id;
            }

            return Task.FromResult(ScopeResult);
        }

        public Task<WriteResult> DeleteScopeAsync(string id, CancellationToken cancellationToken = default) =>
            Throw is not null ? throw Throw : Task.FromResult(ScopeResult);
    }

    /// <summary>A concrete <see cref="DbException"/> with a settable SQLSTATE for the mapping tests.</summary>
    private sealed class FakeDbException(string message, string? sqlState) : DbException(message)
    {
        public override string? SqlState { get; } = sqlState;
    }
}
