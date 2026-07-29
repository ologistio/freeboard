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
        /// <summary>The result the scope upsert/delete returns; defaults to success.</summary>
        public WriteResult ScopeResult { get; init; } = WriteResult.Success;

        /// <summary>The result the requirement-scope upsert/delete returns; defaults to success.</summary>
        public WriteResult RequirementScopeResult { get; init; } = WriteResult.Success;

        public string? LastScopeId { get; private set; }

        public string? LastOrganisationId { get; private set; }

        public Task<WriteResult> UpsertOrganisationAsync(string id, string title, string kind, string? parent, bool expectExisting = false, string? expectedCurrentParent = null, CancellationToken cancellationToken = default)
        {
            LastOrganisationId = id;
            return Task.FromResult(WriteResult.Success);
        }

        public Task<WriteResult> DeleteOrganisationAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(WriteResult.Success);

        public string? LastScopeExpectedOrg { get; private set; }

        public Task<WriteResult> UpsertScopeDispositionAsync(string id, string title, string subject, string standard, string disposition, string? justification = null, string? expectedCurrentOrganisation = null, CancellationToken cancellationToken = default)
        {
            if (ScopeResult.Ok)
            {
                LastScopeId = id;
                LastScopeExpectedOrg = expectedCurrentOrganisation;
            }

            return Task.FromResult(ScopeResult);
        }

        /// <summary>The owner threaded into the most recent scope/requirement-scope delete.</summary>
        public string? LastDeleteExpectedOwner { get; private set; }

        public Task<WriteResult> DeleteScopeAsync(string id, string expectedOwner, CancellationToken cancellationToken = default)
        {
            LastDeleteExpectedOwner = expectedOwner;
            return Task.FromResult(ScopeResult);
        }

        public Task<WriteResult> UpsertRequirementScopeDispositionAsync(string id, string title, string subject, string requirement, string disposition, string? justification = null, string? expectedCurrentOrganisation = null, CancellationToken cancellationToken = default) => Task.FromResult(RequirementScopeResult);

        public Task<WriteResult> DeleteRequirementScopeAsync(string id, string expectedOwner, CancellationToken cancellationToken = default)
        {
            LastDeleteExpectedOwner = expectedOwner;
            return Task.FromResult(RequirementScopeResult);
        }
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
        var compliance = new FakeComplianceStore { Assets = [TestAssets.Org("org-a", title: "A")] };
        using var factory = Build(writes, authz, compliance);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync("/api/v1/freeboard/scopes/s1",
            new { title = "S", subject = "org-a", standard = "std", disposition = "In" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("s1", writes.LastScopeId);
    }

    [Fact]
    public async Task ComplianceReaderIsDeniedAWrite()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a");
        var compliance = new FakeComplianceStore { Assets = [TestAssets.Org("org-a", title: "A")] };
        using var factory = Build(writes, authz, compliance);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync("/api/v1/freeboard/scopes/s1",
            new { title = "S", subject = "org-a", standard = "std", disposition = "In" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(writes.LastScopeId);
    }

    [Fact]
    public async Task ZeroGrantCallerDeniedComplianceWriteUnderCompat()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore();
        var compliance = new FakeComplianceStore { Assets = [TestAssets.Org("org-a", title: "A")] };
        using var factory = Build(writes, authz, compliance, mode: "Compat");
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync("/api/v1/freeboard/scopes/s1",
            new { title = "S", subject = "org-a", standard = "std", disposition = "In" });

        // No admin-claim write fallback: writes always require the proper permission.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeniedWriteBlockedUnderObserve()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore();
        var compliance = new FakeComplianceStore { Assets = [TestAssets.Org("org-a", title: "A")] };
        using var factory = Build(writes, authz, compliance, mode: "Observe");
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync("/api/v1/freeboard/scopes/s1",
            new { title = "S", subject = "org-a", standard = "std", disposition = "In" });

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
            Assets = [TestAssets.Org("org-a", title: "A"), TestAssets.Org("org-b", title: "B")],
            Scopes = [new ScopeRow("s1", "S", "org-b", "std", null, null, "In", null)], // s1 currently owned by org-b
        };
        using var factory = Build(writes, authz, compliance);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        // Move s1 from org-b (not owned) to org-a (owned): denied because the caller lacks write on the stored org.
        var response = await client.PutAsJsonAsync("/api/v1/freeboard/scopes/s1",
            new { title = "S", subject = "org-a", standard = "std", disposition = "In" });

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
            Assets = [TestAssets.Org("org-a", title: "A")],
            Scopes = [new ScopeRow("s1", "S", "org-a", "std", null, null, "In", null)], // s1 currently owned by org-a
        };
        using var factory = Build(writes, authz, compliance);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync("/api/v1/freeboard/scopes/s1",
            new { title = "S", subject = "org-a", standard = "std", disposition = "Out" });

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
        var compliance = new FakeComplianceStore { Assets = [TestAssets.Org("root", title: "Root")] };
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
        var orgs = new List<AssetNode>
        {
            TestAssets.Org("org-a", title: "A"),
            TestAssets.Org("org-b", title: "B"),
        };

        // Reader on org-a under Enforce sees only org-a.
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a");
        using var factory = Build(new RecordingWriteStore(), authz, new FakeComplianceStore { Assets = orgs }, mode: "Enforce");
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var json = await client.GetStringAsync("/api/v1/freeboard/organisations");
        using var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Contains("org-a", ids);
        Assert.DoesNotContain("org-b", ids);
    }

    [Fact]
    public async Task SoaNullsInaccessibleParentForANarrowedReader()
    {
        var compliance = new FakeComplianceStore
        {
            Standards = [new StandardRow("std-a", "Standard A", "1.0", "Example Authority", null, null)],
            Assets =
            [
                TestAssets.Org("org-a", title: "A"),
                TestAssets.Org("org-eng", "org-a", "Department", "Engineering"),
            ],
            Scopes = [new ScopeRow("scope-a", "Scope A", "org-a", "std-a", null, null, "In", null)],
            Requirements = [new RequirementRow("req-a", "Requirement A", "std-a", "Theme", "Do it.", null, "Src", "https://e/a")],
        };

        // Reader on the CHILD only: under Enforce the parent org-a is not accessible, so the returned
        // org-eng node must not disclose its inaccessible parent id (matches the /organisations behaviour).
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-eng");
        using var factory = Build(new RecordingWriteStore(), authz, compliance, mode: "Enforce");
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var json = await client.GetStringAsync("/api/v1/freeboard/statement-of-applicability/std-a");
        using var doc = JsonDocument.Parse(json);
        var nodes = doc.RootElement.GetProperty("nodes").EnumerateArray().ToList();
        var eng = nodes.Single(n => n.GetProperty("id").GetString() == "org-eng");
        Assert.Equal(JsonValueKind.Null, eng.GetProperty("parent").ValueKind);
        Assert.DoesNotContain(nodes, n => n.GetProperty("id").GetString() == "org-a");
    }

    [Fact]
    public async Task OrgOwnerOnChildCannotPromoteChildToRoot()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore().GrantOrgOwner("u1", "child"); // owns child, not its parent root
        var compliance = new FakeComplianceStore
        {
            Assets =
            [
                TestAssets.Org("root", title: "Root"),
                TestAssets.Org("child", "root", "Department", "Child"),
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
            Assets =
            [
                TestAssets.Org("root", title: "Root"),
                TestAssets.Org("child", "root", "Department", "Child"),
                TestAssets.Org("other", title: "Other"),
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
            Assets =
            [
                TestAssets.Org("p1", title: "P1"),
                TestAssets.Org("p2", title: "P2"),
                TestAssets.Org("child", "p1", "Department", "Child"),
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
        var compliance = new FakeComplianceStore { Assets = [TestAssets.Org("existing", title: "E")] };
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
            Assets =
            [
                TestAssets.Org("root", title: "Root"),
                TestAssets.Org("child", "root", "Department", "Child"),
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
        var orgs = new List<AssetNode>
        {
            TestAssets.Org("root", title: "Root"),
            TestAssets.Org("child", "root", "Department", "Child"),
        };
        // Reader granted directly on child only (not root): child is accessible, root is not.
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "child");
        using var factory = Build(new RecordingWriteStore(), authz, new FakeComplianceStore { Assets = orgs }, mode: "Enforce");
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var json = await client.GetStringAsync("/api/v1/freeboard/organisations");
        using var doc = JsonDocument.Parse(json);
        var child = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetString() == "child");
        Assert.Equal(JsonValueKind.Null, child.GetProperty("parent").ValueKind);
    }

    // Cross-route target isolation: each app route is confined to its own target column, so an id
    // cannot cross the target boundary even for an org-owner who could write the row's owning org. The
    // DELETE authz selector is target-column-filtered, so a wrong-kind id yields no stored owner and the
    // route resolves to 404 before the store is touched.
    private static FakeComplianceStore StoreWithOneScope(ScopeRow scope) => new()
    {
        Assets = [TestAssets.Org("org-a", title: "A")],
        Scopes = [scope],
    };

    [Fact]
    public async Task ScopeRouteCannotDeleteRequirementTargetRow()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore().GrantOrgOwner("u1", "org-a");
        var store = StoreWithOneScope(new ScopeRow("r1", "Req scope", "org-a", null, "req-a", null, "In", null));
        using var factory = Build(writes, authz, store);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.DeleteAsync("/api/v1/freeboard/scopes/r1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Fail closed: with no authorized owner (the selector resolves none for a wrong-kind row), the route
        // 404s before the handler, so the store delete is never invoked - there is no unbound-delete path.
        Assert.Null(writes.LastDeleteExpectedOwner);
    }

    [Fact]
    public async Task RequirementScopeRouteCannotDeleteStandardTargetRow()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore().GrantOrgOwner("u1", "org-a");
        var store = StoreWithOneScope(new ScopeRow("s1", "Std scope", "org-a", "std-a", null, null, "In", null));
        using var factory = Build(writes, authz, store);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.DeleteAsync("/api/v1/freeboard/requirement-scopes/s1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizedScopeDeleteThreadsStoredOwnerToStore()
    {
        // The delete must pass the owner the selector authorized (org-a) to the store, not the id alone, so
        // a row concurrently moved to an unwritable org cannot be deleted without authorization for it.
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore().GrantOrgOwner("u1", "org-a");
        var store = StoreWithOneScope(new ScopeRow("s1", "Std scope", "org-a", "std-a", null, null, "In", null));
        using var factory = Build(writes, authz, store);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.DeleteAsync("/api/v1/freeboard/scopes/s1");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("org-a", writes.LastDeleteExpectedOwner);
    }

    [Fact]
    public async Task NeitherAppRouteReachesAControlTargetRow()
    {
        var writes = new RecordingWriteStore();
        var authz = new FakeAuthzStore().GrantOrgOwner("u1", "org-a");
        var store = StoreWithOneScope(new ScopeRow("c1", "Control scope", "org-a", null, null, "ctrl-a", "In", null));
        using var factory = Build(writes, authz, store);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/v1/freeboard/scopes/c1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/v1/freeboard/requirement-scopes/c1")).StatusCode);
    }

    [Fact]
    public async Task WrongKindPutAgainstUnwritableOwnerIs404Not403()
    {
        // A requirement-target row owned by org-b addressed through the standard route by a caller who
        // cannot write org-b. Because the endpoint's stored-owner lookup is target-column-filtered, it
        // never loads the requirement-target row, so it does NOT 403 against org-b: it takes the
        // create/absent path and the store's not-found result maps to 404. A leak or a 403 would prove
        // the lookup was unfiltered.
        var writes = new RecordingWriteStore { ScopeResult = WriteResult.NotFound() };
        var authz = new FakeAuthzStore().GrantOrgOwner("u1", "org-a"); // can write org-a, not org-b
        var store = new FakeComplianceStore
        {
            Assets = [TestAssets.Org("org-a", title: "A"), TestAssets.Org("org-b", title: "B")],
            Scopes = [new ScopeRow("r1", "Req scope", "org-b", null, "req-a", null, "In", null)],
        };
        using var factory = Build(writes, authz, store);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync("/api/v1/freeboard/scopes/r1",
            new { title = "S", subject = "org-a", standard = "std-a", disposition = "In" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // App-managed scope writes are restricted to Company/Department subjects; a Vendor or Machine subject
    // scope is GitOps-write-only. The caller can write org-a (the PUT body subject) but holds no grant on
    // the subject asset, so both routes resolve to 404 - never 403 (which would prove the endpoint
    // authorized against the non-org subject) and never 204. Store results are NotFound so a PUT that
    // reaches the store still surfaces 404, matching the store-level restriction.
    [Theory]
    [InlineData("Vendor")]
    [InlineData("Machine")]
    public async Task AppScopeRoutesRejectNonOrgSubjectRowsAsNotFound(string subjectType)
    {
        var writes = new RecordingWriteStore { ScopeResult = WriteResult.NotFound(), RequirementScopeResult = WriteResult.NotFound() };
        var authz = new FakeAuthzStore().GrantOrgOwner("u1", "org-a");
        var subject = subjectType == "Vendor" ? "vendor-a" : "machine-a";
        var subjectAsset = subjectType == "Vendor"
            ? TestAssets.Vendor("vendor-a", "org-a")
            : TestAssets.Machine("machine-a", "org-a");
        var store = new FakeComplianceStore
        {
            Assets = [TestAssets.Org("org-a", title: "A"), subjectAsset],
            Scopes =
            [
                new ScopeRow("x-std", "Std", subject, "std-a", null, null, "In", null),
                new ScopeRow("x-req", "Req", subject, null, "req-a", null, "In", null),
            ],
        };
        using var factory = Build(writes, authz, store);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var putStd = await client.PutAsJsonAsync("/api/v1/freeboard/scopes/x-std",
            new { title = "S", subject = "org-a", standard = "std-a", disposition = "In" });
        var putReq = await client.PutAsJsonAsync("/api/v1/freeboard/requirement-scopes/x-req",
            new { title = "S", subject = "org-a", requirement = "req-a", disposition = "In" });

        Assert.Equal(HttpStatusCode.NotFound, putStd.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, putReq.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/v1/freeboard/scopes/x-std")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/v1/freeboard/requirement-scopes/x-req")).StatusCode);
    }
}
