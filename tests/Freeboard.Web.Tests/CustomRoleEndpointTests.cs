using System.Net;
using System.Net.Http.Json;
using Freeboard.Core.Authz;

namespace Freeboard.Web.Tests;

/// <summary>
/// The custom-role designer API: the entitlement gate (404 when off, ahead of the super-admin gate),
/// the super-admin gate (403 when unauthorised), CRUD happy paths, endpoint validation (422), and the
/// in-use delete conflict (409). Audit rows are asserted on the admin fake, which records what the real
/// store writes inside the mutation transaction.
/// </summary>
public sealed class CustomRoleEndpointTests
{
    private const string Prefix = "/api/v1/freeboard/custom-roles";

    private static object Body(string roleKey = "custom:auditor", string title = "Auditor",
        string description = "", string[]? permissionKeys = null) => new
    {
        role_key = roleKey,
        title,
        description,
        permission_keys = permissionKeys ?? [AuthzActions.OrgRead, AuthzActions.ComplianceRead],
    };

    [Fact]
    public async Task EntitlementOffReturns404EvenForSuperAdmin()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = false };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("sa", role: "admin"));

        var response = await client.PostAsJsonAsync(Prefix, Body());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task NonSuperAdminForbiddenWhenEntitled()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("u1"));

        var response = await client.PostAsJsonAsync(Prefix, Body());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SuperAdminCreatesEditsAndDeletes()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("sa", role: "admin"));

        var create = await client.PostAsJsonAsync(Prefix, Body());
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.Contains(factory.AuthzAdmin.Events, e => e.EventType == "authz.role.create" && e.ResourceId == "custom:auditor");

        var edit = await client.PutAsJsonAsync($"{Prefix}/custom:auditor",
            Body(title: "Auditor v2", permissionKeys: [AuthzActions.OrgRead]));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var edited = await edit.Content.ReadFromJsonAsync<CustomRoleResponse>();
        Assert.Equal("Auditor v2", edited!.title);
        Assert.Equal([AuthzActions.OrgRead], edited.permission_keys);
        Assert.Contains(factory.AuthzAdmin.Events, e => e.EventType == "authz.role.update");

        var loaded = await client.GetFromJsonAsync<CustomRoleResponse>($"{Prefix}/custom:auditor");
        Assert.Equal("Auditor v2", loaded!.title);
        Assert.Equal([AuthzActions.OrgRead], loaded.permission_keys);

        var delete = await client.DeleteAsync($"{Prefix}/custom:auditor");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Contains(factory.AuthzAdmin.Events, e => e.EventType == "authz.role.delete");
    }

    [Theory]
    [InlineData(AuthzActions.SystemAdmin)]
    [InlineData("no-such-key")]
    public async Task NonAuthorableKeyCreateReturns422(string key)
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("sa", role: "admin"));

        var response = await client.PostAsJsonAsync(Prefix, Body(permissionKeys: [key]));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain(factory.AuthzAdmin.Events, e => e.EventType.StartsWith("authz.role.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("auditor")] // no custom: prefix
    [InlineData("custom:BadCase")]
    public async Task MalformedRoleKeyCreateReturns422(string roleKey)
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("sa", role: "admin"));

        var response = await client.PostAsJsonAsync(Prefix, Body(roleKey: roleKey));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task BlankOrOverLongFieldsCreateReturn422()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("sa", role: "admin"));

        var blank = await client.PostAsJsonAsync(Prefix, Body(title: "   "));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);

        var longTitle = await client.PostAsJsonAsync(Prefix, Body(title: new string('t', 191)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, longTitle.StatusCode);

        var longDescription = await client.PostAsJsonAsync(Prefix, Body(description: new string('d', 513)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, longDescription.StatusCode);
    }

    [Fact]
    public async Task DuplicatePermissionKeysAreCollapsedNotConflict()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("sa", role: "admin"));

        var create = await client.PostAsJsonAsync(Prefix,
            Body(permissionKeys: [AuthzActions.OrgRead, AuthzActions.OrgRead, AuthzActions.ComplianceRead]));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var loaded = await client.GetFromJsonAsync<CustomRoleResponse>($"{Prefix}/custom:auditor");
        Assert.Equal([AuthzActions.OrgRead, AuthzActions.ComplianceRead], loaded!.permission_keys);
    }

    [Fact]
    public async Task OmittedDescriptionCreatesWithEmptyDescription()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("sa", role: "admin"));

        // No description property at all: it is optional and stored as an empty string.
        var create = await client.PostAsJsonAsync(Prefix, new
        {
            role_key = "custom:auditor",
            title = "Auditor",
            permission_keys = new[] { AuthzActions.OrgRead },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var loaded = await client.GetFromJsonAsync<CustomRoleDescriptionResponse>($"{Prefix}/custom:auditor");
        Assert.Equal(string.Empty, loaded!.description);
    }

    [Fact]
    public async Task RejectedUpdateLeavesRoleUnchangedAndWritesNoAuditEvent()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("sa", role: "admin"));

        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync(Prefix, Body(permissionKeys: [AuthzActions.OrgRead]))).StatusCode);

        var bad = await client.PutAsJsonAsync($"{Prefix}/custom:auditor",
            Body(title: "Auditor", permissionKeys: [AuthzActions.SystemAdmin]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

        var loaded = await client.GetFromJsonAsync<CustomRoleResponse>($"{Prefix}/custom:auditor");
        Assert.Equal([AuthzActions.OrgRead], loaded!.permission_keys);
        Assert.DoesNotContain(factory.AuthzAdmin.Events, e => e.EventType == "authz.role.update");
    }

    [Fact]
    public async Task InUseDeleteReturns409()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("sa", role: "admin"));

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Prefix, Body())).StatusCode);
        factory.AuthzAdmin.SeedOrgAssignment("u1", "custom:auditor", "org-a");

        var response = await client.DeleteAsync($"{Prefix}/custom:auditor");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private sealed record CustomRoleResponse(string role_key, string title, IReadOnlyList<string> permission_keys);

    private sealed record CustomRoleDescriptionResponse(string description);
}
