using System.Net;
using Freeboard.Auth;
using Freeboard.Persistence.Auth;

namespace Freeboard.Web.Tests;

/// <summary>
/// The admin user-management pages gate on an in-handler AuthzPageGuard for user.manage, so the
/// legacy freeboard:role=admin claim grants nothing on them, in every rollout mode.
/// </summary>
public sealed class AdminPageAuthzTests
{
    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> GetWithCookie(AuthWebFactory factory, string token, string path)
    {
        using var client = NoRedirectClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        return await client.SendAsync(request);
    }

    [Theory]
    [InlineData("Observe")]
    [InlineData("Compat")]
    [InlineData("Enforce")]
    public async Task LegacyClaimAdminIsForbiddenOnAdminUsersInEveryMode(string mode)
    {
        using var factory = new AuthWebFactory { AuthzMode = mode };
        // A global-admin holding ONLY the legacy claim, with no super-admin authz grant.
        var token = factory.SeedSession(AuthWebFactory.MakeUser("adm", role: "admin"), grantRoleAuthz: false);

        var response = await GetWithCookie(factory, token, "/settings/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LegacyClaimAdminIsForbiddenOnUserCredentialPage()
    {
        using var factory = new AuthWebFactory();
        var token = factory.SeedSession(AuthWebFactory.MakeUser("adm2", role: "admin"), grantRoleAuthz: false);

        var response = await GetWithCookie(factory, token, "/settings/usercredential");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SuperAdminIsAdmittedToAdminUsers()
    {
        using var factory = new AuthWebFactory();
        var token = factory.SeedSession(AuthWebFactory.MakeUser("sa", role: "admin"));

        var response = await GetWithCookie(factory, token, "/settings/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
