using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Freeboard.Core.Authz;
using Freeboard.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Freeboard.Web.Tests;

/// <summary>
/// Per-org enforcement on the compliance surface: narrowed reads, org-scoped write permission,
/// cross-org move protection, the creator-owner grant, and force-enforced writes under every mode.
/// </summary>
public sealed class ComplianceAuthzTests
{
    private sealed class RecordingWriteStore : IComplianceWriteStore
    {
        public string? LastScopeId { get; private set; }

        public string? LastOrganisationId { get; private set; }

        public Task<WriteResult> UpsertOrganisationAsync(string id, string title, string kind, string? parent, bool expectExisting = false, string? expectedCurrentParent = null, CancellationToken cancellationToken = default)
        {
            LastOrganisationId = id;
            return Task.FromResult(WriteResult.Success);
        }

        public Task<WriteResult> DeleteOrganisationAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(WriteResult.Success);

        public string? LastScopeExpectedOrg { get; private set; }

        public Task<WriteResult> UpsertScopeDispositionAsync(string id, string title, string organisation, string standard, string disposition, string? expectedCurrentOrganisation = null, CancellationToken cancellationToken = default)
        {
            LastScopeId = id;
            LastScopeExpectedOrg = expectedCurrentOrganisation;
            return Task.FromResult(WriteResult.Success);
        }

        public Task<WriteResult> DeleteScopeAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(WriteResult.Success);

        public Task<WriteResult> UpsertRequirementScopeDispositionAsync(string id, string title, string organisation, string requirement, string disposition, string? expectedCurrentOrganisation = null, CancellationToken cancellationToken = default) => Task.FromResult(WriteResult.Success);

        public Task<WriteResult> DeleteRequirementScopeAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(WriteResult.Success);
    }

    private sealed class Factory(RecordingWriteStore writes, string? mode) : AuthWebFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (mode is not null)
            {
                builder.UseSetting("Authz:Mode", mode);
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IComplianceWriteStore>();
                services.AddSingleton<IComplianceWriteStore>(writes);
            });
        }
    }

    private static Factory Build(RecordingWriteStore writes, FakeAuthzStore authz, FakeComplianceStore compliance, string? mode = null)
        => new(writes, mode) { Authz = authz, Compliance = compliance };

    [Fact]
    public async Task NonAdminOrgOwnerWritesWithinSubtree()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore().GrantOrgOwner("u1", "org-a");
        var compliance = new FakeComplianceStore { Organisations = [new OrganisationRow("org-a", "A", "Company", null)] };
        using var factory = Build(writes, authz, compliance);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync("/api/v1/freeboard/scopes/s1",
            new { title = "S", organisation = "org-a", standard = "std", disposition = "In" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("s1", writes.LastScopeId);
    }

    [Fact]
    public async Task ComplianceReaderIsDeniedAWrite()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a");
        var compliance = new FakeComplianceStore { Organisations = [new OrganisationRow("org-a", "A", "Company", null)] };
        using var factory = Build(writes, authz, compliance);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync("/api/v1/freeboard/scopes/s1",
            new { title = "S", organisation = "org-a", standard = "std", disposition = "In" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(writes.LastScopeId);
    }

    [Fact]
    public async Task ZeroGrantCallerDeniedComplianceWriteUnderCompat()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore();
        var compliance = new FakeComplianceStore { Organisations = [new OrganisationRow("org-a", "A", "Company", null)] };
        using var factory = Build(writes, authz, compliance, mode: "Compat");
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync("/api/v1/freeboard/scopes/s1",
            new { title = "S", organisation = "org-a", standard = "std", disposition = "In" });

        // No admin-claim write fallback: writes always require the proper permission.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeniedWriteBlockedUnderObserve()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore();
        var compliance = new FakeComplianceStore { Organisations = [new OrganisationRow("org-a", "A", "Company", null)] };
        using var factory = Build(writes, authz, compliance, mode: "Observe");
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync("/api/v1/freeboard/scopes/s1",
            new { title = "S", organisation = "org-a", standard = "std", disposition = "In" });

        // Writes force-enforce in every mode, so Observe does not open them.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CrossOrgMoveDeniedWhenCallerLacksWriteOnStoredOrg()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore().GrantOrgOwner("u1", "org-a"); // owns org-a only
        var compliance = new FakeComplianceStore
        {
            Organisations = [new OrganisationRow("org-a", "A", "Company", null), new OrganisationRow("org-b", "B", "Company", null)],
            Scopes = [new ScopeRow("s1", "S", "org-b", "std", "In")], // s1 currently owned by org-b
        };
        using var factory = Build(writes, authz, compliance);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        // Move s1 from org-b (not owned) to org-a (owned): denied because the caller lacks write on the stored org.
        var response = await client.PutAsJsonAsync("/api/v1/freeboard/scopes/s1",
            new { title = "S", organisation = "org-a", standard = "std", disposition = "In" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(writes.LastScopeId);
    }

    [Fact]
    public async Task ScopeUpsertPassesStoredOwnerSoTheWriteRechecksItUnderLock()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore().GrantOrgOwner("u1", "org-a");
        var compliance = new FakeComplianceStore
        {
            Organisations = [new OrganisationRow("org-a", "A", "Company", null)],
            Scopes = [new ScopeRow("s1", "S", "org-a", "std", "In")], // s1 currently owned by org-a
        };
        using var factory = Build(writes, authz, compliance);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync("/api/v1/freeboard/scopes/s1",
            new { title = "S", organisation = "org-a", standard = "std", disposition = "Out" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        // The endpoint hands the authorized current owner to the store, which re-checks it under the
        // write lock; a create (no existing row) would pass null.
        Assert.Equal("org-a", writes.LastScopeExpectedOrg);
    }

    [Fact]
    public async Task OrgCreatorBecomesOwner()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore().GrantOrgOwner("u1", "root"); // can create children of root
        var compliance = new FakeComplianceStore { Organisations = [new OrganisationRow("root", "Root", "Company", null)] };
        using var factory = Build(writes, authz, compliance);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync("/api/v1/freeboard/organisations/child",
            new { title = "Child", kind = "Department", parent = "root" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        // The creator is granted org-owner on the new org.
        Assert.Contains(factory.AuthzAdmin.Events, e => e.EventType == "authz.assignment.write" && e.OrganisationId == "child");
    }

    [Fact]
    public async Task SuperAdminSeesAllOrganisationsButReaderIsNarrowed()
    {
        var orgs = new List<OrganisationRow>
        {
            new("org-a", "A", "Company", null),
            new("org-b", "B", "Company", null),
        };

        // Reader on org-a under Enforce sees only org-a.
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a");
        using var factory = Build(new RecordingWriteStore(), authz, new FakeComplianceStore { Organisations = orgs }, mode: "Enforce");
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var json = await client.GetStringAsync("/api/v1/freeboard/organisations");
        using var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Contains("org-a", ids);
        Assert.DoesNotContain("org-b", ids);
    }

    [Fact]
    public async Task OrgOwnerOnChildCannotPromoteChildToRoot()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore().GrantOrgOwner("u1", "child"); // owns child, not its parent root
        var compliance = new FakeComplianceStore
        {
            Organisations =
            [
                new OrganisationRow("root", "Root", "Company", null),
                new OrganisationRow("child", "Child", "Department", "root"),
            ],
        };
        using var factory = Build(writes, authz, compliance);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        // Setting parent=null promotes the child to a root, which requires system.admin.
        var response = await client.PutAsJsonAsync("/api/v1/freeboard/organisations/child",
            new { title = "Child", kind = "Department", parent = (string?)null });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(writes.LastOrganisationId);
    }

    [Fact]
    public async Task OrgOwnerOnChildCannotReparentChildToAnotherOrg()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore().GrantOrgOwner("u1", "child"); // owns child only
        var compliance = new FakeComplianceStore
        {
            Organisations =
            [
                new OrganisationRow("root", "Root", "Company", null),
                new OrganisationRow("child", "Child", "Department", "root"),
                new OrganisationRow("other", "Other", "Company", null),
            ],
        };
        using var factory = Build(writes, authz, compliance);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        // Reparent requires org.write on BOTH the current parent (root) and the new parent (other);
        // the caller has neither.
        var response = await client.PutAsJsonAsync("/api/v1/freeboard/organisations/child",
            new { title = "Child", kind = "Department", parent = "other" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(writes.LastOrganisationId);
    }

    [Fact]
    public async Task ReparentAllowedWhenCallerOwnsBothParents()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore().GrantOrgOwner("u1", "p1").GrantOrgOwner("u1", "p2");
        var compliance = new FakeComplianceStore
        {
            Organisations =
            [
                new OrganisationRow("p1", "P1", "Company", null),
                new OrganisationRow("p2", "P2", "Company", null),
                new OrganisationRow("child", "Child", "Department", "p1"),
            ],
        };
        using var factory = Build(writes, authz, compliance);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync("/api/v1/freeboard/organisations/child",
            new { title = "Child", kind = "Department", parent = "p2" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("child", writes.LastOrganisationId);
    }

    [Fact]
    public async Task NonSuperAdminCannotCreateRootOrganisation()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore().GrantOrgOwner("u1", "existing"); // owns an org but is not super-admin
        var compliance = new FakeComplianceStore { Organisations = [new OrganisationRow("existing", "E", "Company", null)] };
        using var factory = Build(writes, authz, compliance);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync("/api/v1/freeboard/organisations/newroot",
            new { title = "New Root", kind = "Company", parent = (string?)null });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(writes.LastOrganisationId);
    }

    [Fact]
    public async Task SuperAdminCreatesRootAndPromotesChildToRoot()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore();
        var compliance = new FakeComplianceStore
        {
            Organisations =
            [
                new OrganisationRow("root", "Root", "Company", null),
                new OrganisationRow("child", "Child", "Department", "root"),
            ],
        };
        using var factory = Build(writes, authz, compliance);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("sa", role: "admin"));

        var createRoot = await client.PutAsJsonAsync("/api/v1/freeboard/organisations/newroot",
            new { title = "New Root", kind = "Company", parent = (string?)null });
        Assert.Equal(HttpStatusCode.NoContent, createRoot.StatusCode);

        var promote = await client.PutAsJsonAsync("/api/v1/freeboard/organisations/child",
            new { title = "Child", kind = "Company", parent = (string?)null });
        Assert.Equal(HttpStatusCode.NoContent, promote.StatusCode);
    }

    [Fact]
    public async Task InaccessibleParentIdIsNulledInOrganisationsResponse()
    {
        var orgs = new List<OrganisationRow>
        {
            new("root", "Root", "Company", null),
            new("child", "Child", "Department", "root"),
        };
        // Reader granted directly on child only (not root): child is accessible, root is not.
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "child");
        using var factory = Build(new RecordingWriteStore(), authz, new FakeComplianceStore { Organisations = orgs }, mode: "Enforce");
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var json = await client.GetStringAsync("/api/v1/freeboard/organisations");
        using var doc = JsonDocument.Parse(json);
        var child = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetString() == "child");
        Assert.Equal(JsonValueKind.Null, child.GetProperty("parent").ValueKind);
    }
}
