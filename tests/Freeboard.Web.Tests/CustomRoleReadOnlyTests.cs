using System.Net;
using System.Net.Http.Json;
using Freeboard.Core.Authz;

namespace Freeboard.Web.Tests;

/// <summary>
/// GitOps read-only precedence: minting a role definition is policy configuration, so the custom-role
/// API carries no <c>AuthEndpoint</c> exemption and its mutations return 409 under read-only. The
/// entitlement is ON, so the 409 proves read-only precedes the entitlement filter (not a 404 from the
/// gate).
/// </summary>
public sealed class CustomRoleReadOnlyTests
{
    private const string Prefix = "/api/v1/freeboard/custom-roles";

    [Fact]
    public async Task SuperAdminMutationsReturn409UnderReadOnly()
    {
        using var factory = new AuthWebFactory { ReadOnly = true, CustomPoliciesEntitled = true };
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("sa", role: "admin"));

        var create = await client.PostAsJsonAsync(Prefix, new
        {
            role_key = "custom:auditor",
            title = "Auditor",
            description = "",
            permission_keys = new[] { AuthzActions.OrgRead },
        });
        Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);

        var update = await client.PutAsJsonAsync($"{Prefix}/custom:auditor", new
        {
            title = "Auditor",
            permission_keys = new[] { AuthzActions.OrgRead },
        });
        Assert.Equal(HttpStatusCode.Conflict, update.StatusCode);

        var delete = await client.DeleteAsync($"{Prefix}/custom:auditor");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }
}
