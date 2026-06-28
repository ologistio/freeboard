using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Freeboard.Web.Tests;

/// <summary>
/// Admin user-management tests: create returns the temp password once and sets
/// force_password_reset; list has no password; get 404; disable revokes sessions; enable;
/// reset-password returns a fresh temp password and revokes; non-admin 403; duplicate email 422.
/// </summary>
public sealed class UserAdminEndpointTests
{
    private const string Prefix = "/api/v1/freeboard";

    private static HttpClient AdminClient(AuthWebFactory f)
        => f.CreateAuthenticatedClient(AuthWebFactory.MakeUser("admin1", role: "admin"));

    [Fact]
    public async Task NonAdminIsForbidden()
    {
        using var factory = new AuthWebFactory();
        using var client = factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("plain", role: "member"));

        var response = await client.GetAsync($"{Prefix}/users");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CreateReturnsTempPasswordOnceAndSetsForceReset()
    {
        using var factory = new AuthWebFactory();
        using var client = AdminClient(factory);

        var response = await client.PostAsJsonAsync(
            $"{Prefix}/users", new { email = "new@example.com", name = "New", global_role = "member" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(json.GetProperty("temporary_password").GetString()));
        Assert.True(json.GetProperty("user").GetProperty("force_password_reset").GetBoolean());
    }

    [Fact]
    public async Task CreateDuplicateEmailIs422()
    {
        using var factory = new AuthWebFactory();
        using var client = AdminClient(factory);

        await client.PostAsJsonAsync(
            $"{Prefix}/users", new { email = "dup@example.com", name = "A", global_role = "member" });
        var dup = await client.PostAsJsonAsync(
            $"{Prefix}/users", new { email = "dup@example.com", name = "B", global_role = "member" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, dup.StatusCode);
    }

    [Fact]
    public async Task ListHasNoPasswordField()
    {
        using var factory = new AuthWebFactory();
        using var client = AdminClient(factory);
        await client.PostAsJsonAsync(
            $"{Prefix}/users", new { email = "l1@example.com", name = "L1", global_role = "member" });

        var json = await client.GetFromJsonAsync<JsonElement>($"{Prefix}/users");
        foreach (var user in json.EnumerateArray())
        {
            Assert.False(user.TryGetProperty("password", out _));
            Assert.False(user.TryGetProperty("password_hash", out _));
            Assert.False(user.TryGetProperty("temporary_password", out _));
        }
    }

    [Fact]
    public async Task GetMissingUserIs404()
    {
        using var factory = new AuthWebFactory();
        using var client = AdminClient(factory);

        var response = await client.GetAsync($"{Prefix}/users/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DisableRevokesSessionsThenEnable()
    {
        using var factory = new AuthWebFactory();
        using var client = AdminClient(factory);

        var created = await (await client.PostAsJsonAsync(
            $"{Prefix}/users", new { email = "d1@example.com", name = "D1", global_role = "member" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("user").GetProperty("id").GetString()!;

        await factory.Sessions.CreateAsync(
            id, [2, 2, 2], 1, Persistence.Auth.SessionAuthState.Full, DateTime.UtcNow.AddHours(1));

        var disable = await client.PostAsync($"{Prefix}/users/{id}/disable", null);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.Empty(await factory.Sessions.ListByUserAsync(id));

        var enable = await client.PostAsync($"{Prefix}/users/{id}/enable", null);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
    }

    [Fact]
    public async Task ResetPasswordReturnsFreshTempPasswordAndRevokes()
    {
        using var factory = new AuthWebFactory();
        using var client = AdminClient(factory);

        var created = await (await client.PostAsJsonAsync(
            $"{Prefix}/users", new { email = "r1@example.com", name = "R1", global_role = "member" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("user").GetProperty("id").GetString()!;
        await factory.Sessions.CreateAsync(
            id, [3, 3, 3], 1, Persistence.Auth.SessionAuthState.Full, DateTime.UtcNow.AddHours(1));

        var response = await client.PostAsync($"{Prefix}/users/{id}/reset-password", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(json.GetProperty("temporary_password").GetString()));
        Assert.Empty(await factory.Sessions.ListByUserAsync(id));
    }
}
