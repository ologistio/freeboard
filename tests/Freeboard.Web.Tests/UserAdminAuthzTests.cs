using System.Net;
using Freeboard.Auth;

namespace Freeboard.Web.Tests;

/// <summary>
/// User-admin enforcement: the super-admin gate, the last-super-admin disable guard (closing the API
/// self-disable gap), and the mutation audit trail.
/// </summary>
public sealed class UserAdminAuthzTests
{
    [Fact]
    public async Task NonSuperAdminIsDeniedUserManage()
    {
        using var factory = new AuthWebFactory();
        var target = AuthWebFactory.MakeUser("t1");
        factory.Users.Add(target);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("m1")); // plain member

        var response = await client.PostAsync($"/api/v1/freeboard/users/{target.Id}/disable", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SelfDisableOfLastSuperAdminIsRejected()
    {
        using var factory = new AuthWebFactory();
        var admin = AuthWebFactory.MakeUser("sa1", role: "admin");
        using var client = factory.CreateAuthenticatedClient(admin); // sole super-admin

        var response = await client.PostAsync($"/api/v1/freeboard/users/{admin.Id}/disable", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DisableAllowedWhileAnotherSuperAdminRemains()
    {
        using var factory = new AuthWebFactory();
        var admin1 = AuthWebFactory.MakeUser("sa1", role: "admin");
        var admin2 = AuthWebFactory.MakeUser("sa2", role: "admin");
        factory.SeedSession(admin2); // second usable super-admin
        using var client = factory.CreateAuthenticatedClient(admin1);

        var response = await client.PostAsync($"/api/v1/freeboard/users/{admin2.Id}/disable", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UserAdminMutationIsAudited()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("sa1", role: "admin"));

        var response = await client.PostAsJsonAsync("/api/v1/freeboard/users",
            new { email = "new@example.com", name = "New", global_role = "member" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains(factory.AuthzAdmin.Events, e => e.EventType == "user.admin.create");
    }

    [Fact]
    public async Task FailedMutationAuditIsLoggedNotFatal()
    {
        // The mutation audit is best-effort: a failed audit write is logged at warning and does not turn
        // a succeeded mutation into an error.
        using var factory = new AuthWebFactory { AuthzAdmin = new FakeAuthzAdministrationStore { ThrowOnAudit = true } };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("sa1", role: "admin"));

        var response = await client.PostAsJsonAsync("/api/v1/freeboard/users",
            new { email = "new@example.com", name = "New", global_role = "member" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode); // mutation still succeeds
        Assert.Contains(factory.Logs.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && e.Text.Contains("mutation audit row failed", StringComparison.Ordinal));
    }
}
