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
    public async Task DeleteOrganisationAllowedOffReadOnlyMode()
    {
        var writes = new FakeComplianceWriteStore();
        using var factory = new WriteFactory(writes);
        using var client = AdminClient(factory);

        var response = await client.DeleteAsync("/api/v1/freeboard/organisations/org-a");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
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
            new { title = "Scope A", subject = "org-a", standard = "std-a", disposition = "In" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("scope-a", writes.LastScopeId);
    }

    [Fact]
    public async Task SetRequirementScopeDispositionAllowedOffReadOnlyMode()
    {
        var writes = new FakeComplianceWriteStore();
        using var factory = new WriteFactory(writes);
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/freeboard/requirement-scopes/rs-a",
            new { title = "RS A", subject = "org-a", requirement = "req-a", disposition = "Out", justification = "Not applicable." });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("rs-a", writes.LastRequirementScopeId);
    }

    [Fact]
    public async Task DuplicateRequirementScopePairPreCheckReturns422()
    {
        // A plain duplicate the write-store pre-check catches returns 422 (WriteResult.Fail).
        var writes = new FakeComplianceWriteStore
        {
            RequirementScopeResult = WriteResult.Fail(
                "A requirement-scope already maps organisation 'org-a' to requirement 'req-a'."),
        };
        using var factory = new WriteFactory(writes);
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/freeboard/requirement-scopes/rs-b",
            new { title = "RS B", subject = "org-a", requirement = "req-a", disposition = "Out", justification = "Not applicable." });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(writes.LastRequirementScopeId);
    }

    [Fact]
    public async Task ConcurrentRequirementScopeDuplicateKeyReturns409()
    {
        // A duplicate that races the pre-check hits the unique key; the driver raises SQLSTATE 23000.
        var writes = new FakeComplianceWriteStore { Throw = new FakeDbException("Duplicate entry", "23000") };
        using var factory = new WriteFactory(writes);
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/freeboard/requirement-scopes/rs-a",
            new { title = "RS A", subject = "org-a", requirement = "req-a", disposition = "Out", justification = "Not applicable." });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task RequirementScopeWriteBlockedWith409InReadOnlyMode()
    {
        var writes = new FakeComplianceWriteStore();
        using var factory = new WriteFactory(writes, readOnly: true);
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/freeboard/requirement-scopes/rs-a",
            new { title = "RS A", subject = "org-a", requirement = "req-a", disposition = "Out", justification = "Not applicable." });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(writes.LastRequirementScopeId);
    }

    [Fact]
    public async Task DeleteOrganisationReferencedByRequirementScopeRejectedWithProblem()
    {
        var writes = new FakeComplianceWriteStore
        {
            OrganisationResult = WriteResult.Fail("Cannot delete an organisation that still has requirement-scopes."),
        };
        using var factory = new WriteFactory(writes);
        using var client = AdminClient(factory);

        var response = await client.DeleteAsync("/api/v1/freeboard/organisations/org-a");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
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
            new { title = "Scope A", subject = "org-a", standard = "std-a", disposition = "In" });

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

    [Fact]
    public async Task ScopeOutWithNoJustificationRejectedWith422()
    {
        var writes = new FakeComplianceWriteStore();
        using var factory = new WriteFactory(writes);
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/freeboard/scopes/scope-a",
            new { title = "Scope A", subject = "org-a", standard = "std-a", disposition = "Out" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Null(writes.LastScopeId);
    }

    [Fact]
    public async Task ScopeOutWithJustificationSucceeds()
    {
        var writes = new FakeComplianceWriteStore();
        using var factory = new WriteFactory(writes);
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/freeboard/scopes/scope-a",
            new { title = "Scope A", subject = "org-a", standard = "std-a", disposition = "Out", justification = "Compensating control in place." });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("scope-a", writes.LastScopeId);
    }

    [Fact]
    public async Task WrongTargetKindScopePutReturns404NotConflict()
    {
        // The store's global-id branch finds an existing row of a different target kind and returns the
        // not-found result; the endpoint maps it to 404, not a duplicate-key 409.
        var writes = new FakeComplianceWriteStore { ScopeResult = WriteResult.NotFound() };
        using var factory = new WriteFactory(writes);
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/freeboard/scopes/rs-1",
            new { title = "S", subject = "org-a", standard = "std-a", disposition = "In" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(writes.LastScopeId);
    }

    [Fact]
    public async Task GatedWriteAnswers403WhenTheAssetReadFails()
    {
        // The organisation gate resolves its resource from the shared asset read, so an unreadable assets
        // table fails the selector. The permission filter catches a throwing selector as a deny: it refuses
        // the write rather than performing it on an authorization decision it could not make.
        using var factory = new WriteFactory(new FakeComplianceWriteStore())
        {
            Compliance = new FakeComplianceStore { Faulted = ComplianceReadSet.Assets },
        };
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/freeboard/organisations/org-a",
            new { title = "Org A", kind = "Company", parent = (string?)null });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GatedWriteSucceedsWhenOnlyTheAssuranceReadFails()
    {
        // The gate reads the assets and no payload table, so a schema missing vendor_assurances degrades
        // the vendor register rather than closing every gated compliance write. Before the shared read was
        // narrowed this answered 403, which made an unapplied migration a total write outage.
        var writes = new FakeComplianceWriteStore();
        using var factory = new WriteFactory(writes)
        {
            Compliance = new FakeComplianceStore { Assets = [TestAssets.Org("org-a")], Faulted = ComplianceReadSet.VendorAssurances },
        };
        using var client = AdminClient(factory);

        var response = await client.PutAsJsonAsync(
            "/api/v1/freeboard/organisations/org-a",
            new { title = "Org A", kind = "Company", parent = (string?)null });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("org-a", writes.LastOrganisationId);
    }

    /// <summary>A concrete <see cref="DbException"/> with a settable SQLSTATE for the mapping tests.</summary>
    private sealed class FakeDbException(string message, string? sqlState) : DbException(message)
    {
        public override string? SqlState { get; } = sqlState;
    }
}

internal sealed class WriteFactory(IComplianceWriteStore writes, bool readOnly = false) : AuthWebFactory
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

internal sealed class FakeComplianceWriteStore : IComplianceWriteStore
{
    public WriteResult OrganisationResult { get; init; } = WriteResult.Success;

    public WriteResult ScopeResult { get; init; } = WriteResult.Success;

    public WriteResult RequirementScopeResult { get; init; } = WriteResult.Success;

    /// <summary>When set, every write throws it, simulating a store failure past the pre-checks.</summary>
    public Exception? Throw { get; init; }

    public string? LastOrganisationId { get; private set; }

    public string? LastScopeId { get; private set; }

    public string? LastRequirementScopeId { get; private set; }

    public Task<WriteResult> UpsertOrganisationAsync(
        string id, string title, string kind, string? parent,
        bool expectExisting = false, string? expectedCurrentParent = null, CancellationToken cancellationToken = default)
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
        string id, string title, string subject, string standard, string disposition,
        string? justification = null, string? expectedCurrentOrganisation = null, CancellationToken cancellationToken = default)
    {
        if (Throw is not null)
        {
            throw Throw;
        }

        // Mirror the store invariant: an Out disposition requires a non-blank justification.
        if (disposition == "Out" && string.IsNullOrWhiteSpace(justification))
        {
            return Task.FromResult(WriteResult.Fail("An Out disposition requires a justification."));
        }

        if (ScopeResult.Ok)
        {
            LastScopeId = id;
        }

        return Task.FromResult(ScopeResult);
    }

    public Task<WriteResult> DeleteScopeAsync(string id, string expectedOwner, CancellationToken cancellationToken = default) =>
        Throw is not null ? throw Throw : Task.FromResult(ScopeResult);

    public Task<WriteResult> UpsertRequirementScopeDispositionAsync(
        string id, string title, string subject, string requirement, string disposition,
        string? justification = null, string? expectedCurrentOrganisation = null, CancellationToken cancellationToken = default)
    {
        if (Throw is not null)
        {
            throw Throw;
        }

        if (disposition == "Out" && string.IsNullOrWhiteSpace(justification))
        {
            return Task.FromResult(WriteResult.Fail("An Out disposition requires a justification."));
        }

        if (RequirementScopeResult.Ok)
        {
            LastRequirementScopeId = id;
        }

        return Task.FromResult(RequirementScopeResult);
    }

    public Task<WriteResult> DeleteRequirementScopeAsync(string id, string expectedOwner, CancellationToken cancellationToken = default) =>
        Throw is not null ? throw Throw : Task.FromResult(RequirementScopeResult);
}
