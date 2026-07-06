using System.Net;
using Freeboard.Auth;
using Freeboard.Core.Authz;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// The custom-role designer page: the entitlement gate (404 when off), the in-page super-admin gate
/// (403 for a non-admin), a page create that writes an audit event through the shared store, and the
/// GitOps read-only 409 on its POST (the page carries no AuthEndpoint exemption).
/// </summary>
public sealed class CustomRolesPageTests
{
    private const string Path = "/admin/custom-roles";

    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static IEnumerable<KeyValuePair<string, string>> SessionCookieFor(string token)
        => [new KeyValuePair<string, string>(SessionCookie.Name, token)];

    [Fact]
    public async Task NonAdminGetIsForbidden()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("member1", role: "member"));
        using var client = NoRedirectClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, Path);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EntitlementOffGetIsNotFound()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = false };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("admin1", role: "admin"));
        using var client = NoRedirectClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, Path);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SuperAdminCreateViaPageWritesAuditEvent()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("admin1", role: "admin"));
        using var client = NoRedirectClient(factory);

        var response = await AuthFormTestHelpers.PostFormAsync(
            client, $"{Path}?handler=Create",
            [
                new("roleKey", "custom:auditor"),
                new("title", "Auditor"),
                new("description", "Read-only auditor."),
                new("permissionKeys", AuthzActions.OrgRead),
            ],
            extraCookies: SessionCookieFor(token).ToList(), getPath: Path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(factory.AuthzAdmin.Events, e => e.EventType == "authz.role.create" && e.ResourceId == "custom:auditor");
    }

    [Fact]
    public async Task NoticeSurvivesRedirectAfterCreate()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("admin1", role: "admin"));
        using var client = NoRedirectClient(factory);

        var post = await AuthFormTestHelpers.PostFormAsync(
            client, $"{Path}?handler=Create",
            [
                new("roleKey", "custom:auditor"),
                new("title", "Auditor"),
                new("description", "Read-only auditor."),
                new("permissionKeys", AuthzActions.OrgRead),
            ],
            extraCookies: SessionCookieFor(token).ToList(), getPath: Path);
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);

        // Follow the redirect carrying the session plus the TempData cookie the POST set.
        var followCookies = SessionCookieFor(token).ToList();
        followCookies.AddRange(AuthFormTestHelpers.ParseSetCookies(post));
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, Path);
        getRequest.Headers.Add("Cookie", string.Join("; ", followCookies.Select(c => $"{c.Key}={c.Value}")));
        var get = await client.SendAsync(getRequest);

        var html = await get.Content.ReadAsStringAsync();
        Assert.Contains("Role created.", html);
    }

    [Fact]
    public async Task PagePostUnderReadOnlyReturns409()
    {
        using var factory = new AuthWebFactory { ReadOnly = true, CustomPoliciesEntitled = true };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("admin1", role: "admin"));
        using var client = NoRedirectClient(factory);

        var response = await AuthFormTestHelpers.PostFormAsync(
            client, $"{Path}?handler=Create",
            [
                new("roleKey", "custom:auditor"),
                new("title", "Auditor"),
                new("description", ""),
                new("permissionKeys", AuthzActions.OrgRead),
            ],
            extraCookies: SessionCookieFor(token).ToList(), getPath: Path);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
