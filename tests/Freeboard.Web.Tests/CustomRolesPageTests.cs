using System.Net;
using Freeboard.Auth;
using Freeboard.Core.Authz;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// The custom-role LIST page (/settings/custom-roles): the entitlement gate (404 when off), the in-page
/// super-admin gate (403 for a non-admin), an expandable table of roles with an Edit link into the
/// designer, and an inline Delete that writes an audit event through the shared store. Create and edit
/// live on the designer page (see <see cref="CustomRoleDesignerPageTests"/>).
/// </summary>
public sealed class CustomRolesPageTests
{
    private const string Path = "/settings/custom-roles";

    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static IEnumerable<KeyValuePair<string, string>> SessionCookieFor(string token)
        => [new KeyValuePair<string, string>(SessionCookie.Name, token)];

    private static async Task<HttpResponseMessage> GetAsync(AuthWebFactory factory, HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task NonAdminGetIsForbidden()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("member1", role: "member"));
        using var client = NoRedirectClient(factory);

        var response = await GetAsync(factory, client, token, Path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EntitlementOffGetIsNotFound()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = false };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("admin1", role: "admin"));
        using var client = NoRedirectClient(factory);

        var response = await GetAsync(factory, client, token, Path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TableRendersSeededRoleWithEditLink()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("admin1", role: "admin"));
        using var client = NoRedirectClient(factory);
        // Seed after the host is built: ShareRolesWith rebinds the roles dictionary on host build.
        factory.AuthzAdmin.SeedCustomRole("custom:auditor", AuthzActions.OrgRead);

        var response = await GetAsync(factory, client, token, Path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("custom:auditor", html);
        Assert.Contains("/settings/custom-roles/designer/auditor", html);
    }

    [Fact]
    public async Task DeleteRemovesRoleAndWritesAuditEvent()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("admin1", role: "admin"));
        using var client = NoRedirectClient(factory);
        factory.AuthzAdmin.SeedCustomRole("custom:auditor", AuthzActions.OrgRead);

        var response = await AuthFormTestHelpers.PostFormAsync(
            client, $"{Path}?handler=Delete",
            [new("roleKey", "custom:auditor")],
            extraCookies: SessionCookieFor(token).ToList(), getPath: Path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(factory.AuthzAdmin.Events, e => e.EventType == "authz.role.delete" && e.ResourceId == "custom:auditor");
    }

    [Fact]
    public async Task PageUsesSharedButtonComponentNotUndefinedClass()
    {
        // Guards against buttons reverting to the nonexistent .app-button class (which renders unstyled).
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("admin1", role: "admin"));
        using var client = NoRedirectClient(factory);

        var response = await GetAsync(factory, client, token, Path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("btn-primary", html);
        Assert.DoesNotContain("app-button", html);
    }
}
