using System.Net;
using System.Net.Http.Json;
using Freeboard.Auth;
using Freeboard.Core.Authz;

namespace Freeboard.Web.Tests;

/// <summary>
/// The role-assignment management API: the assignment-write and system-admin gates, 403-vs-404
/// non-disclosure, the last-super-admin and last-owner guards, and force-enforcement in every mode.
/// </summary>
public sealed class RoleAssignmentEndpointTests
{
    private const string Prefix = "/api/v1/freeboard";

    [Fact]
    public async Task SuperAdminGrantsOrgRole()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("sa", role: "admin"));

        var response = await client.PutAsJsonAsync($"{Prefix}/organisations/org-a/role-assignments",
            new { user_id = "u1", role_key = "compliance-reader" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task GrantDeniedWithoutAssignmentPermissionButOrgVisible()
    {
        using var factory = new AuthWebFactory { Authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a") };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync($"{Prefix}/organisations/org-a/role-assignments",
            new { user_id = "u2", role_key = "compliance-reader" });

        // Can see the org (org.read) but lacks authz.assignment.write -> 403.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InvisibleOrgReturns404UnderEnforce()
    {
        using var factory = new AuthWebFactory { AuthzMode = "Enforce", Authz = new FakeAuthzStore() };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync($"{Prefix}/organisations/org-a/role-assignments",
            new { user_id = "u2", role_key = "compliance-reader" });

        // Zero-grant caller cannot even see the org under Enforce -> 404 (non-disclosure).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SystemGrantDeniedWithoutSystemAdmin()
    {
        using var factory = new AuthWebFactory { Authz = new FakeAuthzStore().GrantOrgOwner("u1", "org-a") };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync($"{Prefix}/system-role-assignments", new { user_id = "u2" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RevokeLastSuperAdminReturns409()
    {
        using var factory = new AuthWebFactory();
        var admin = AuthWebFactory.MakeUser("sa", role: "admin"); // sole super-admin (auto-seeded)
        using var client = factory.CreateAuthenticatedClient(admin);

        var response = await client.DeleteAsync($"{Prefix}/system-role-assignments/{admin.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task RevokeLastDirectOrgOwnerReturns409()
    {
        using var factory = new AuthWebFactory();
        factory.AuthzAdmin.SeedOrgAssignment("u1", AuthzRoles.OrgOwner, "org-a");
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("sa", role: "admin"));

        var response = await client.DeleteAsync($"{Prefix}/organisations/org-a/role-assignments/u1/org-owner");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task OwnerCannotRevokeItsOwnOrgOwnerGrantSelfLockout()
    {
        using var factory = new AuthWebFactory { Authz = new FakeAuthzStore().GrantOrgOwner("u1", "org-a") };
        // Two direct owners, so the last-owner guard does not fire; the caller revoking its OWN grant is
        // the self-lockout the actor parameter enforces.
        factory.AuthzAdmin.SeedOrgAssignment("u1", AuthzRoles.OrgOwner, "org-a");
        factory.AuthzAdmin.SeedOrgAssignment("u2", AuthzRoles.OrgOwner, "org-a");
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var self = await client.DeleteAsync($"{Prefix}/organisations/org-a/role-assignments/u1/org-owner");
        Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);

        // Revoking ANOTHER owner's grant is permitted (not self-lockout).
        var other = await client.DeleteAsync($"{Prefix}/organisations/org-a/role-assignments/u2/org-owner");
        Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
    }

    [Fact]
    public async Task DeniedRoleManagementMutationBlockedUnderObserve()
    {
        using var factory = new AuthWebFactory
        {
            AuthzMode = "Observe",
            Authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a"),
        };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PutAsJsonAsync($"{Prefix}/organisations/org-a/role-assignments",
            new { user_id = "u2", role_key = "compliance-reader" });

        // Management endpoints force-enforce regardless of mode.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RoleAssignmentPageForbiddenForNonManager()
    {
        using var factory = new AuthWebFactory { Authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a") };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("u1"));
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/settings/role-assignments?orgId=org-a");
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
