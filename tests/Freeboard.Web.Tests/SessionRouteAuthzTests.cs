using System.Net;
using Freeboard.Auth;

namespace Freeboard.Web.Tests;

/// <summary>
/// The dual-purpose session routes: the cross-user branch is gated by an in-handler user.manage
/// decision, so the legacy admin claim grants nothing there, while the self path is unchanged.
/// </summary>
public sealed class SessionRouteAuthzTests
{
    [Fact]
    public async Task LegacyClaimAdminCannotListAnotherUsersSessions()
    {
        using var factory = new AuthWebFactory();
        var other = AuthWebFactory.MakeUser("other1");
        factory.SeedSession(other); // give the target a live session
        // An admin holding ONLY the legacy claim (no super-admin authz grant).
        using var client = factory.CreateAuthenticatedClient(
            AuthWebFactory.MakeUser("adm1", role: "admin"), grantRoleAuthz: false);

        var response = await client.GetAsync($"/api/v1/freeboard/users/{other.Id}/sessions");

        // Non-disclosure: a caller without user.manage sees 404, not 403, for another user.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LegacyClaimAdminCannotRevokeAnotherUsersSessions()
    {
        using var factory = new AuthWebFactory();
        var other = AuthWebFactory.MakeUser("other2");
        factory.SeedSession(other);
        using var client = factory.CreateAuthenticatedClient(
            AuthWebFactory.MakeUser("adm2", role: "admin"), grantRoleAuthz: false);

        var response = await client.DeleteAsync($"/api/v1/freeboard/users/{other.Id}/sessions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnyUserListsOwnSessionsWithNoGrant()
    {
        using var factory = new AuthWebFactory();
        var me = AuthWebFactory.MakeUser("me1");
        using var client = factory.CreateAuthenticatedClient(me);

        var response = await client.GetAsync($"/api/v1/freeboard/users/{me.Id}/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SelfSessionActionsDoNotEvaluateOrAuditTheAuthorizer()
    {
        // A normal user reading/revoking its OWN sessions must not trigger the always-enforce authorizer,
        // so no spurious Deny audit row is written on the self path.
        using var factory = new AuthWebFactory();
        var me = AuthWebFactory.MakeUser("me2");
        using var client = factory.CreateAuthenticatedClient(me);

        var list = await client.GetAsync($"/api/v1/freeboard/users/{me.Id}/sessions");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var readOwn = await client.GetAsync($"/api/v1/freeboard/auth/sessions/{AuthWebFactory.SessionIdFor(me)}");
        Assert.Equal(HttpStatusCode.OK, readOwn.StatusCode);

        // No authz_audit_events row (neither a denied decision nor a cross-user permit) on the self path.
        Assert.Empty(factory.AuthzAdmin.Events);
    }

    [Fact]
    public async Task SuperAdminListsAndRevokesAnotherUsersSessionsAndAudits()
    {
        using var factory = new AuthWebFactory();
        var other = AuthWebFactory.MakeUser("other3");
        factory.SeedSession(other);
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("sa1", role: "admin"));

        var list = await client.GetAsync($"/api/v1/freeboard/users/{other.Id}/sessions");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var revoke = await client.DeleteAsync($"/api/v1/freeboard/users/{other.Id}/sessions");
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        Assert.Contains(factory.AuthzAdmin.Events, e => e.EventType == "auth.user.sessions.list");
        Assert.Contains(factory.AuthzAdmin.Events, e => e.EventType == "auth.user.sessions.revoke");
    }
}
