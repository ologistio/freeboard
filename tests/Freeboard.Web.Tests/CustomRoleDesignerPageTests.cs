using System.Net;
using Freeboard.Auth;
using Freeboard.Core.Authz;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// The custom-role designer (/admin/custom-roles/designer/{slug?}): the create/edit wizard. Covers the
/// entitlement (404) and super-admin (403) gates, that Save composes the reserved key from the slug and
/// writes the create audit, that an invalid key re-renders the design step with an error (no write),
/// that GitOps read-only 409s the POST, and that editing an existing role prefills and updates it.
/// </summary>
public sealed class CustomRoleDesignerPageTests
{
    private const string CreatePath = "/admin/custom-roles/designer";

    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static IEnumerable<KeyValuePair<string, string>> SessionCookieFor(string token)
        => [new KeyValuePair<string, string>(SessionCookie.Name, token)];

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task EntitlementOffGetIsNotFound()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = false };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("admin1", role: "admin"));
        using var client = NoRedirectClient(factory);

        var response = await GetAsync(client, token, CreatePath);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task NonAdminGetIsForbidden()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("member1", role: "member"));
        using var client = NoRedirectClient(factory);

        var response = await GetAsync(client, token, CreatePath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SaveComposesKeyFromSlugAndWritesCreateAudit()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("admin1", role: "admin"));
        using var client = NoRedirectClient(factory);

        var response = await AuthFormTestHelpers.PostFormAsync(
            client, $"{CreatePath}?handler=Save",
            [
                new("KeyInput", "auditor"),
                new("Title", "Auditor"),
                new("Description", "Read-only auditor."),
                new("SelectedPermissions", AuthzActions.OrgRead),
            ],
            extraCookies: SessionCookieFor(token).ToList(), getPath: CreatePath);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(factory.AuthzAdmin.Events, e => e.EventType == "authz.role.create" && e.ResourceId == "custom:auditor");
    }

    [Fact]
    public async Task NoticeSurvivesRedirectToList()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("admin1", role: "admin"));
        using var client = NoRedirectClient(factory);

        var post = await AuthFormTestHelpers.PostFormAsync(
            client, $"{CreatePath}?handler=Save",
            [
                new("KeyInput", "auditor"),
                new("Title", "Auditor"),
                new("Description", "Read-only auditor."),
                new("SelectedPermissions", AuthzActions.OrgRead),
            ],
            extraCookies: SessionCookieFor(token).ToList(), getPath: CreatePath);
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);

        var followCookies = SessionCookieFor(token).ToList();
        followCookies.AddRange(AuthFormTestHelpers.ParseSetCookies(post));
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/custom-roles");
        getRequest.Headers.Add("Cookie", string.Join("; ", followCookies.Select(c => $"{c.Key}={c.Value}")));
        var get = await client.SendAsync(getRequest);

        var html = await get.Content.ReadAsStringAsync();
        Assert.Contains("Role created.", html);
    }

    [Fact]
    public async Task ContinueWithInvalidKeyRedisplaysStepWithErrorAndNoWrite()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("admin1", role: "admin"));
        using var client = NoRedirectClient(factory);

        var response = await AuthFormTestHelpers.PostFormAsync(
            client, $"{CreatePath}?handler=Continue",
            [
                new("KeyInput", "Bad Key"),
                new("Title", "Auditor"),
                new("SelectedPermissions", AuthzActions.OrgRead),
            ],
            extraCookies: SessionCookieFor(token).ToList(), getPath: CreatePath);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Role key must be", html);
        Assert.DoesNotContain(factory.AuthzAdmin.Events, e => e.EventType == "authz.role.create");
    }

    [Fact]
    public async Task SaveUnderReadOnlyReturns409()
    {
        using var factory = new AuthWebFactory { ReadOnly = true, CustomPoliciesEntitled = true };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("admin1", role: "admin"));
        using var client = NoRedirectClient(factory);

        var response = await AuthFormTestHelpers.PostFormAsync(
            client, $"{CreatePath}?handler=Save",
            [
                new("KeyInput", "auditor"),
                new("Title", "Auditor"),
                new("Description", ""),
                new("SelectedPermissions", AuthzActions.OrgRead),
            ],
            extraCookies: SessionCookieFor(token).ToList(), getPath: CreatePath);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task EditGetPrefillsExistingRole()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("admin1", role: "admin"));
        using var client = NoRedirectClient(factory);
        // Seed after the host is built: ShareRolesWith rebinds the roles dictionary on host build.
        factory.AuthzAdmin.SeedCustomRole("custom:auditor", AuthzActions.OrgRead);

        var response = await GetAsync(client, token, $"{CreatePath}/auditor");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("custom:auditor", html);
        Assert.Contains("Edit custom role", html);
        Assert.Contains("Role key cannot be changed.", html);
    }

    [Fact]
    public async Task EditSaveWritesUpdateAudit()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("admin1", role: "admin"));
        using var client = NoRedirectClient(factory);
        factory.AuthzAdmin.SeedCustomRole("custom:auditor", AuthzActions.OrgRead);

        var response = await AuthFormTestHelpers.PostFormAsync(
            client, $"{CreatePath}/auditor?handler=Save",
            [
                new("Title", "Auditor (renamed)"),
                new("Description", "Now also writes."),
                new("SelectedPermissions", AuthzActions.OrgRead),
                new("SelectedPermissions", AuthzActions.OrgWrite),
            ],
            extraCookies: SessionCookieFor(token).ToList(), getPath: $"{CreatePath}/auditor");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(factory.AuthzAdmin.Events, e => e.EventType == "authz.role.update" && e.ResourceId == "custom:auditor");
    }
}
