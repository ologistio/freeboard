using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Freeboard.Web.Tests;

/// <summary>
/// First-admin bootstrap tests: the correct secret creates the first admin and
/// returns a token; a second call self-disables with 409 (sentinel collision); a wrong secret
/// is 401.
/// </summary>
public sealed class BootstrapEndpointTests
{
    private const string Setup = "/api/v1/freeboard/setup";
    private const string Secret = "test-bootstrap-secret";

    private static object Body(string secret) => new
    {
        email = "admin@example.com",
        name = "Admin",
        password = "adminpass",
        bootstrap_secret = secret,
    };

    [Fact]
    public async Task CorrectSecretCreatesFirstAdminWithToken()
    {
        using var factory = new AuthWebFactory { BootstrapSecret = Secret };
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Setup, Body(Secret));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("admin", json.GetProperty("user").GetProperty("global_role").GetString());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("token").GetString()));
    }

    [Fact]
    public async Task SecondCallSelfDisablesWith409()
    {
        using var factory = new AuthWebFactory { BootstrapSecret = Secret };
        using var client = factory.CreateClient();

        var first = await client.PostAsJsonAsync(Setup, Body(Secret));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync(Setup, Body(Secret));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task WrongSecretIs401()
    {
        using var factory = new AuthWebFactory { BootstrapSecret = Secret };
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Setup, Body("wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // No admin was created (the wrong secret never opened the transaction).
        Assert.Equal(0, await factory.Users.CountAsync());
    }
}
