using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Freeboard.Pages.Compliance;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Freeboard.Web.Tests;

/// <summary>
/// The integration-connection read surfaces: the server-rendered list page
/// (/settings/integration-connections) and the JSON endpoint (/api/v1/freeboard/integration-connections).
/// Both compose the token-resolvable health flag at read time from the out-of-band resolver and never
/// expose, render, or log the token value. The page requires an authenticated user (anonymous redirects
/// to /login), degrades to an in-page notice on a store outage, and shows an empty state when there are
/// none. The endpoint 401s an anonymous caller and 503s on a store outage. The startup warning names an
/// unresolvable connection id and never the token value.
/// </summary>
public sealed class IntegrationConnectionsTests
{
    private const string Path = "/settings/integration-connections";
    private const string Endpoint = "/api/v1/freeboard/integration-connections";
    private const string SecretToken = "super-secret-token-value";

    private static FakeComplianceStore PopulatedStore() => new()
    {
        Connections =
        [
            new IntegrationConnectionRow("fleet-prod", "fleet", "https://fleet.example.com", "daily", "vendor-a"),
            new IntegrationConnectionRow("fleet-dev", "fleet", "https://dev.example.com", "weekly", null),
        ],
    };

    /// <summary>fleet-prod has a configured token (resolvable); fleet-dev has none (unresolvable).</summary>
    private static AuthWebFactory Factory(FakeComplianceStore store, bool readOnly = false) => new()
    {
        Compliance = store,
        ReadOnly = readOnly,
        Settings = new Dictionary<string, string?> { ["Freeboard:Integrations:fleet-prod:ApiToken"] = SecretToken },
    };

    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> GetAuthenticatedAsync(
        AuthWebFactory factory, HttpClient client, string relativeUrl)
    {
        var token = factory.SeedSession(AuthWebFactory.MakeUser("conn1"));
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        return await client.SendAsync(request);
    }

    private static HttpClient MemberClient(AuthWebFactory factory)
        => factory.CreateAuthenticatedClient(AuthWebFactory.MakeUser("member1"));

    [Fact]
    public async Task AnonymousGetRedirectsToLogin()
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task PageListsConnectionsWithHealthFlagAndNeverTheToken()
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-connection-id=\"fleet-prod\"", html, StringComparison.Ordinal);
        Assert.Contains("data-connection-id=\"fleet-dev\"", html, StringComparison.Ordinal);
        Assert.Contains("https://fleet.example.com", html, StringComparison.Ordinal);
        Assert.Contains("daily", html, StringComparison.Ordinal);
        Assert.Contains("vendor-a", html, StringComparison.Ordinal);

        // fleet-prod has a configured token (resolvable); fleet-dev does not (unresolvable).
        var prod = html[html.IndexOf("data-connection-id=\"fleet-prod\"", StringComparison.Ordinal)..];
        prod = prod[..prod.IndexOf("</tr>", StringComparison.Ordinal)];
        Assert.Contains("data-token-resolvable=\"true\"", prod, StringComparison.Ordinal);

        var dev = html[html.IndexOf("data-connection-id=\"fleet-dev\"", StringComparison.Ordinal)..];
        dev = dev[..dev.IndexOf("</tr>", StringComparison.Ordinal)];
        Assert.Contains("data-token-resolvable=\"false\"", dev, StringComparison.Ordinal);

        // The token value never reaches the rendered page.
        Assert.DoesNotContain(SecretToken, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListSortsUnresolvableConnectionsFirst()
    {
        // L1 exceptions-first: a failing (unresolvable) connection sorts above a resolvable one even when
        // its id sorts later. aaa-ready has a token; zzz-failing has none.
        var store = new FakeComplianceStore
        {
            Connections =
            [
                new IntegrationConnectionRow("aaa-ready", "fleet", "https://a.example.com", "daily", null),
                new IntegrationConnectionRow("zzz-failing", "fleet", "https://z.example.com", "daily", null),
            ],
        };
        using var factory = new AuthWebFactory
        {
            Compliance = store,
            Settings = new Dictionary<string, string?> { ["Freeboard:Integrations:aaa-ready:ApiToken"] = SecretToken },
        };
        using var client = NoRedirectClient(factory);

        var html = await (await GetAuthenticatedAsync(factory, client, Path)).Content.ReadAsStringAsync();
        var failing = html.IndexOf("data-connection-id=\"zzz-failing\"", StringComparison.Ordinal);
        var ready = html.IndexOf("data-connection-id=\"aaa-ready\"", StringComparison.Ordinal);
        Assert.True(failing >= 0 && ready >= 0 && failing < ready, "unresolvable connection should sort first (L1)");
    }

    [Fact]
    public async Task ServedInReadOnlyModeToAuthenticatedUser()
    {
        using var factory = Factory(PopulatedStore(), readOnly: true);
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnreachableStoreRendersNoticeNot500()
    {
        using var factory = Factory(new FakeComplianceStore { Unreachable = true });
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("could not be reached", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-connection-id", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyStoreRendersEmptyState()
    {
        using var factory = Factory(new FakeComplianceStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("data-empty", html, StringComparison.Ordinal);
        Assert.Contains("Integration", html, StringComparison.Ordinal);
        Assert.DoesNotContain("IntegrationConnection", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndpointReturnsSnakeCaseShapeWithTokenResolvableAndNoToken()
    {
        using var factory = Factory(PopulatedStore());
        using var client = MemberClient(factory);

        var json = await client.GetFromJsonAsync<JsonElement>(Endpoint);

        Assert.Equal(2, json.GetArrayLength());
        var prod = json[0];
        Assert.Equal("fleet-prod", prod.GetProperty("id").GetString());
        Assert.Equal("fleet", prod.GetProperty("provider").GetString());
        Assert.Equal("https://fleet.example.com", prod.GetProperty("base_url").GetString());
        Assert.Equal("daily", prod.GetProperty("discovery_cadence").GetString());
        Assert.Equal("vendor-a", prod.GetProperty("vendor").GetString());
        Assert.True(prod.GetProperty("token_resolvable").GetBoolean());

        var dev = json[1];
        Assert.Equal("fleet-dev", dev.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, dev.GetProperty("vendor").ValueKind);
        Assert.False(dev.GetProperty("token_resolvable").GetBoolean());

        // No property carries the token value.
        Assert.DoesNotContain(SecretToken, json.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndpointAnonymousIsUnauthorized()
    {
        using var factory = Factory(PopulatedStore());
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EndpointStoreOutageReturns503()
    {
        using var factory = Factory(new FakeComplianceStore { Unreachable = true });
        using var client = MemberClient(factory);

        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task StartupWarningNamesUnresolvableConnectionAndNeverTheToken()
    {
        var store = new FakeComplianceStore
        {
            Connections =
            [
                new IntegrationConnectionRow("fleet-prod", "fleet", "https://fleet.example.com", "daily", "vendor-a"),
                new IntegrationConnectionRow("fleet-dev", "fleet", "https://dev.example.com", "weekly", null),
            ],
            Collectors =
            [
                new EvidenceCollectorRow("collector-prod", "MFA", "ctrl-a", null, "integration", "daily", null,
                    new Dictionary<string, string>(), "fleet-prod"),
                new EvidenceCollectorRow("collector-dev", "MFA", "ctrl-a", null, "integration", "daily", null,
                    new Dictionary<string, string>(), "fleet-dev"),
            ],
        };
        using var factory = Factory(store);

        // Building a client boots the app. The token-resolvability warning read runs just after the
        // server starts, off the boot path, so poll the captured logs with a short bounded wait.
        using var client = factory.CreateClient();

        List<string> warnings;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            warnings = factory.Logs.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Text).ToList();
            if (warnings.Any(w => w.Contains("fleet-dev", StringComparison.Ordinal)))
            {
                break;
            }

            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);

        // fleet-dev has no configured token, so it is named; fleet-prod is resolvable, so it is not.
        Assert.Contains(warnings, w => w.Contains("fleet-dev", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("fleet-prod", StringComparison.Ordinal));
        // The token value never appears in any log entry.
        Assert.DoesNotContain(factory.Logs.Entries, e => e.Text.Contains(SecretToken, StringComparison.Ordinal));
    }

    [Fact]
    public void PageConstructorTakesStoreAndTokenResolver()
    {
        var ctor = Assert.Single(typeof(IntegrationConnectionsModel).GetConstructors());
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        Assert.Equal([typeof(IComplianceStore), typeof(Freeboard.Compliance.IIntegrationTokenResolver)], paramTypes);
    }
}
