using System.Net;
using System.Net.Http.Json;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Freeboard.Web.Tests;

/// <summary>
/// Every organisation gate anchors on the ORGANISATION-BOUNDED ancestry chain. The unified asset read
/// makes the ancestry walk follow a parent edge across a non-organisation asset, so without the cut a
/// caller granted on the far side of a machine would pass gates that refuse everyone but a super-admin.
///
/// Each case uses a caller who is NOT a super-admin but DOES hold the route's permission on the
/// organisation the widened walk would reach, and asserts the status the route gives today - never a
/// 400 and never a no-op 204, either of which would mean the refusal came from the store rather than
/// from authorization.
/// </summary>
public sealed class WritePathAncestryGuardTests
{
    private const string Prefix = "/api/v1/freeboard";

    // far-company -> far-dept -> m-1 (a live Machine). Two organisations hang off m-1's far side:
    // org-on-machine, whose own parent names the machine, and the create/update targets below. A caller
    // granted only on far-company reaches m-1's chain if - and only if - the cut is missing.
    private static IReadOnlyList<AssetNode> Tree() =>
    [
        TestAssets.Org("far-company"),
        TestAssets.Org("far-dept", "far-company", "Department"),
        TestAssets.Machine("m-1", "far-dept"),
        TestAssets.Org("org-on-machine", "m-1", "Department"),
        TestAssets.Org("own-org"),
        TestAssets.Org("movable", "own-org", "Department"),
        TestAssets.Vendor("vendor-a", "far-company"),
    ];

    private sealed class RecordingWriteStore : IComplianceWriteStore
    {
        /// <summary>Every store call the request reached, so a refusal can be proved to precede the store.</summary>
        public List<string> Calls { get; } = [];

        /// <summary>The result the organisation upsert returns; the id-collision case sets a conflict.</summary>
        public WriteResult OrganisationResult { get; init; } = WriteResult.Success;

        public Task<WriteResult> UpsertOrganisationAsync(
            string id, string title, string kind, string? parent, bool expectExisting = false,
            string? expectedCurrentParent = null, CancellationToken cancellationToken = default)
        {
            Calls.Add($"upsert-org:{id}");
            return Task.FromResult(OrganisationResult);
        }

        public Task<WriteResult> DeleteOrganisationAsync(string id, CancellationToken cancellationToken = default)
        {
            Calls.Add($"delete-org:{id}");
            return Task.FromResult(WriteResult.Success);
        }

        public Task<WriteResult> UpsertScopeDispositionAsync(
            string id, string title, string subject, string standard, string disposition, string? justification = null,
            string? expectedCurrentOrganisation = null, CancellationToken cancellationToken = default)
        {
            Calls.Add($"upsert-scope:{id}");
            return Task.FromResult(WriteResult.Success);
        }

        public Task<WriteResult> DeleteScopeAsync(
            string id, string expectedOwner, CancellationToken cancellationToken = default)
        {
            Calls.Add($"delete-scope:{id}");
            return Task.FromResult(WriteResult.Success);
        }

        public Task<WriteResult> UpsertRequirementScopeDispositionAsync(
            string id, string title, string subject, string requirement, string disposition,
            string? justification = null, string? expectedCurrentOrganisation = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"upsert-req-scope:{id}");
            return Task.FromResult(WriteResult.Success);
        }

        public Task<WriteResult> DeleteRequirementScopeAsync(
            string id, string expectedOwner, CancellationToken cancellationToken = default)
        {
            Calls.Add($"delete-req-scope:{id}");
            return Task.FromResult(WriteResult.Success);
        }
    }

    private sealed class Factory(RecordingWriteStore writes) : AuthWebFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IComplianceWriteStore>();
                services.AddSingleton<IComplianceWriteStore>(writes);
            });
        }
    }

    /// <summary>
    /// A caller holding org-owner (org.write, compliance.scope.write, requirement-scope write, and
    /// assignment write) on <paramref name="grantedOrgs"/> and nothing else - never a super-admin.
    /// </summary>
    private static Factory Build(
        RecordingWriteStore writes, IReadOnlyList<ScopeRow>? scopes = null, params string[] grantedOrgs)
    {
        var authz = new FakeAuthzStore();
        foreach (var org in grantedOrgs.Length == 0 ? ["far-company"] : grantedOrgs)
        {
            authz.GrantOrgOwner("u1", org);
        }

        return new Factory(writes)
        {
            Authz = authz,
            Compliance = new FakeComplianceStore { Assets = Tree(), Scopes = scopes ?? [] },
        };
    }

    private static HttpClient Client(AuthWebFactory factory)
        => factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

    // A machine id supplied to an organisation gate. Its chain is cut at itself, so the grant on the
    // organisation above it no longer matches and every route refuses at the authorization layer.
    [Fact]
    public async Task DeleteOrganisationOnAMachineIdIsForbidden()
    {
        var writes = new RecordingWriteStore();
        using var factory = Build(writes, grantedOrgs: "far-dept");
        using var client = Client(factory);

        var response = await client.DeleteAsync($"{Prefix}/organisations/m-1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(writes.Calls);
    }

    [Fact]
    public async Task ScopePutWithAMachineSubjectIsForbidden()
    {
        var writes = new RecordingWriteStore();
        using var factory = Build(writes, grantedOrgs: "far-dept");
        using var client = Client(factory);

        var response = await client.PutAsJsonAsync($"{Prefix}/scopes/s1",
            new { title = "S", subject = "m-1", standard = "std-a", disposition = "In" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(writes.Calls);
    }

    [Fact]
    public async Task RequirementScopePutWithAMachineSubjectIsForbidden()
    {
        var writes = new RecordingWriteStore();
        using var factory = Build(writes, grantedOrgs: "far-dept");
        using var client = Client(factory);

        var response = await client.PutAsJsonAsync($"{Prefix}/requirement-scopes/rs1",
            new { title = "S", subject = "m-1", requirement = "req-a", disposition = "In" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(writes.Calls);
    }

    [Fact]
    public async Task CreatingAnOrganisationUnderAMachineParentIsForbidden()
    {
        var writes = new RecordingWriteStore();
        using var factory = Build(writes, grantedOrgs: "far-dept");
        using var client = Client(factory);

        var response = await client.PutAsJsonAsync($"{Prefix}/organisations/new-child",
            new { title = "Child", kind = "Department", parent = "m-1" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(writes.Calls);
    }

    [Fact]
    public async Task ReparentingAnExistingOrganisationOntoAMachineIsForbidden()
    {
        // The caller owns movable's current parent, so the stored-parent side authorizes and the refusal
        // has to come from the NEW parent. It also owns far-company, which the machine's uncut chain
        // would reach - so without the cut this reparent would succeed.
        var writes = new RecordingWriteStore();
        using var factory = Build(writes, grantedOrgs: ["own-org", "movable", "far-company"]);
        using var client = Client(factory);

        var response = await client.PutAsJsonAsync($"{Prefix}/organisations/movable",
            new { title = "Movable", kind = "Department", parent = "m-1" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(writes.Calls);
    }

    // PUT /organisations/{machineId} is still a CREATE, because the existence test is organisation-only.
    // The status therefore depends on the body, and both outcomes are today's.
    [Fact]
    public async Task PutOnAMachineIdWithANullParentIsForbiddenForWantOfSystemAdmin()
    {
        var writes = new RecordingWriteStore();
        using var factory = Build(writes, grantedOrgs: "far-dept");
        using var client = Client(factory);

        var response = await client.PutAsJsonAsync($"{Prefix}/organisations/m-1",
            new { title = "M", kind = "Company", parent = (string?)null });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(writes.Calls);
    }

    [Fact]
    public async Task PutOnAMachineIdUnderAWritableParentReachesTheStoreAndCollides()
    {
        // The selector authorizes the PARENT here, which the caller may write, so the request is meant
        // to reach the store: it finds no organisation row to lock and then collides on the existing
        // assets.id. A 403 here would be a behaviour change, not a tightening.
        var writes = new RecordingWriteStore { OrganisationResult = WriteResult.Conflict("id already exists") };
        using var factory = Build(writes, grantedOrgs: "far-dept");
        using var client = Client(factory);

        var response = await client.PutAsJsonAsync($"{Prefix}/organisations/m-1",
            new { title = "M", kind = "Company", parent = "far-dept" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(["upsert-org:m-1"], writes.Calls);
    }

    // The id class the cut exists for: an ORGANISATION whose own parent names a live machine. The guard
    // cannot key on the id class, because this id IS an organisation.
    [Fact]
    public async Task StoredOwnerScopeDeleteOnAnOrganisationBeyondAMachineIsForbidden()
    {
        var writes = new RecordingWriteStore();
        using var factory = Build(
            writes, scopes: [new ScopeRow("s1", "S", "org-on-machine", "std-a", null, null, "In", null)]);
        using var client = Client(factory);

        var response = await client.DeleteAsync($"{Prefix}/scopes/s1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(writes.Calls);
    }

    [Fact]
    public async Task StoredOwnerRequirementScopeDeleteOnAnOrganisationBeyondAMachineIsForbidden()
    {
        var writes = new RecordingWriteStore();
        using var factory = Build(
            writes, scopes: [new ScopeRow("rs1", "S", "org-on-machine", null, "req-a", null, "In", null)]);
        using var client = Client(factory);

        var response = await client.DeleteAsync($"{Prefix}/requirement-scopes/rs1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(writes.Calls);
    }

    [Fact]
    public async Task PlainOrganisationUpdateBeyondAMachineIsForbidden()
    {
        var writes = new RecordingWriteStore();
        using var factory = Build(writes);
        using var client = Client(factory);

        var response = await client.PutAsJsonAsync($"{Prefix}/organisations/org-on-machine",
            new { title = "Renamed", kind = "Department", parent = "m-1" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(writes.Calls);
    }

    [Fact]
    public async Task ScopePutWithAnOrganisationSubjectBeyondAMachineIsForbidden()
    {
        var writes = new RecordingWriteStore();
        using var factory = Build(writes);
        using var client = Client(factory);

        var response = await client.PutAsJsonAsync($"{Prefix}/scopes/s-new",
            new { title = "S", subject = "org-on-machine", standard = "std-a", disposition = "In" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(writes.Calls);
    }

    // The grant on the organisation's OWN chain still works: the cut removes the crossing, not the tree.
    [Fact]
    public async Task AGrantOnTheOrganisationItselfStillWrites()
    {
        var writes = new RecordingWriteStore();
        using var factory = Build(writes, grantedOrgs: "org-on-machine");
        using var client = Client(factory);

        var response = await client.PutAsJsonAsync($"{Prefix}/scopes/s-new",
            new { title = "S", subject = "org-on-machine", standard = "std-a", disposition = "In" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["upsert-scope:s-new"], writes.Calls);
    }

    [Fact]
    public async Task AnOrdinaryAllOrganisationChainIsUnaffected()
    {
        // far-dept's chain has no cut point, so the grant on far-company above it still matches.
        var writes = new RecordingWriteStore();
        using var factory = Build(writes);
        using var client = Client(factory);

        var response = await client.DeleteAsync($"{Prefix}/organisations/far-dept");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["delete-org:far-dept"], writes.Calls);
    }

    // A super-admin is unaffected: the guard removes an ancestry match, and SystemPolicy does not read
    // one, so the request reaches the store and gets whatever the store answers for a non-organisation id.
    [Fact]
    public async Task SuperAdminStillReachesTheStoreOnAMachineId()
    {
        var writes = new RecordingWriteStore();
        using var factory = new Factory(writes) { Compliance = new FakeComplianceStore { Assets = Tree() } };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("sa", role: "admin"));

        var response = await client.DeleteAsync($"{Prefix}/organisations/m-1");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["delete-org:m-1"], writes.Calls);
    }

    [Fact]
    public async Task SuperAdminStillWritesAScopeBeyondAMachine()
    {
        var writes = new RecordingWriteStore();
        using var factory = new Factory(writes) { Compliance = new FakeComplianceStore { Assets = Tree() } };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("sa", role: "admin"));

        var response = await client.PutAsJsonAsync($"{Prefix}/scopes/s-new",
            new { title = "S", subject = "org-on-machine", standard = "std-a", disposition = "In" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["upsert-scope:s-new"], writes.Calls);
    }

    // The three /organisations/{orgId}/role-assignments routes keep their existence-non-disclosure 404
    // rather than taking the 403 every other gate gives: their own mode-aware org.read check runs first
    // and returns a null resource, and a 403 on a machine id would disclose that the id exists.
    [Fact]
    public async Task RoleAssignmentApiOnAMachineIdIs404UnderEnforce()
    {
        using var factory = new AuthWebFactory
        {
            AuthzMode = "Enforce",
            Authz = new FakeAuthzStore().GrantOrgOwner("u1", "far-dept"),
            Compliance = new FakeComplianceStore { Assets = Tree() },
        };
        using var client = Client(factory);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"{Prefix}/organisations/m-1/role-assignments")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Prefix}/organisations/m-1/role-assignments",
                new { user_id = "u2", role_key = "compliance-reader" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.DeleteAsync($"{Prefix}/organisations/m-1/role-assignments/u2/compliance-reader")).StatusCode);
    }

    [Fact]
    public async Task RoleAssignmentApiOnAMachineIdIs403UnderObserve()
    {
        // Observe relaxes the selector's read check, so the resource IS selected; the route's
        // force-enforced assignment gate then answers 403. The mode dependence is pinned, not discovered.
        using var factory = new AuthWebFactory
        {
            AuthzMode = "Observe",
            Authz = new FakeAuthzStore().GrantOrgOwner("u1", "far-dept"),
            Compliance = new FakeComplianceStore { Assets = Tree() },
        };
        using var client = Client(factory);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync($"{Prefix}/organisations/m-1/role-assignments")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync($"{Prefix}/organisations/m-1/role-assignments",
                new { user_id = "u2", role_key = "compliance-reader" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.DeleteAsync($"{Prefix}/organisations/m-1/role-assignments/u2/compliance-reader")).StatusCode);
    }

    // The role-assignment PAGE guard is a plain 403 on a deny, so it does not share the API's 404.
    private const string PagePath = "/settings/role-assignments";

    private static AuthWebFactory PageFactory() => new()
    {
        AuthzMode = "Enforce",
        Authz = new FakeAuthzStore().GrantOrgOwner("u1", "far-dept"),
        Compliance = new FakeComplianceStore { Assets = Tree() },
    };

    [Fact]
    public async Task RoleAssignmentPageGetOnAMachineIdIsForbidden()
    {
        using var factory = PageFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var token = factory.SeedSession(AuthWebFactory.MakeUser("u1"));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{PagePath}?orgId=m-1");
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("Grant")]
    [InlineData("Revoke")]
    public async Task RoleAssignmentPagePostOnAMachineIdIsForbidden(string handler)
    {
        using var factory = PageFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var session = factory.SeedSession(AuthWebFactory.MakeUser("u1"));

        // The page GET for an accessible org yields the antiforgery pair; the POST then names the
        // machine id, so only the handler guard can refuse it.
        var response = await AuthFormTestHelpers.PostFormAsync(
            client,
            $"{PagePath}?orgId=m-1&handler={handler}",
            [new("orgId", "m-1"), new("userId", "u2"), new("roleKey", "compliance-reader")],
            extraCookies: [new(SessionCookie.Name, session)],
            getPath: $"{PagePath}?orgId=far-dept");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
