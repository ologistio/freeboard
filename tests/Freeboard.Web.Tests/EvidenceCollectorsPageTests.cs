using System.Net;
using Freeboard.Pages.Compliance;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// Server-rendered evidence-collector register page: requires an authenticated user (anonymous is
/// redirected to /login; admin is NOT required), renders each control with its evaluation rule and its
/// attached collectors (type, vendor, frequency, threshold, config), is GET-only and served in GitOps
/// read-only mode, reads through the injected <see cref="IComplianceStore"/> (no MySQL), and never
/// narrows to the caller's accessible organisations.
/// </summary>
public sealed class EvidenceCollectorsPageTests
{
    private const string Path = "/compliance/evidence-collectors";

    private static FakeComplianceStore PopulatedStore() => new()
    {
        Controls =
        [
            new ControlRow("ctrl-a", "Control A", ["req-a"], "all"),
            new ControlRow("ctrl-b", "Control B", ["req-b"], null),
        ],
        Collectors =
        [
            new EvidenceCollectorRow("collector-a", "Endpoint MFA", "ctrl-a", "vendor-a", "integration", "daily", 100,
                new Dictionary<string, string> { ["endpoint"] = "policies.mfa" }),
        ],
    };

    private static AuthWebFactory Factory(FakeComplianceStore store, bool readOnly = false)
        => new() { Compliance = store, ReadOnly = readOnly };

    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> GetAuthenticatedAsync(
        AuthWebFactory factory, HttpClient client, string relativeUrl, UserRow? user = null)
    {
        var token = factory.SeedSession(user ?? AuthWebFactory.MakeUser("collectors1"));
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        return await client.SendAsync(request);
    }

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
    public async Task RendersControlsTheirEvaluationAndCollectors()
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-control-id=\"ctrl-a\"", html, StringComparison.Ordinal);
        Assert.Contains("data-control-id=\"ctrl-b\"", html, StringComparison.Ordinal);
        // The control's evaluation rule and its attached collector's fields render.
        Assert.Contains("data-collector-id=\"collector-a\"", html, StringComparison.Ordinal);

        var section = html[html.IndexOf("data-control-id=\"ctrl-a\"", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("</section>", StringComparison.Ordinal)];
        Assert.Contains("all", section, StringComparison.Ordinal);
        Assert.Contains("integration", section, StringComparison.Ordinal);
        Assert.Contains("vendor-a", section, StringComparison.Ordinal);
        Assert.Contains("daily", section, StringComparison.Ordinal);
        Assert.Contains("policies.mfa", section, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ControlWithoutCollectorsRendersNote()
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        var html = await response.Content.ReadAsStringAsync();

        var section = html[html.IndexOf("data-control-id=\"ctrl-b\"", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("</section>", StringComparison.Ordinal)];
        Assert.Contains("data-no-collectors", section, StringComparison.Ordinal);
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
        Assert.DoesNotContain("data-control-id", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZeroGrantEnforceCallerSeesEveryControlAndCollector()
    {
        // The register does not narrow by accessible organisation, so a zero-grant Enforce caller still
        // sees every control and collector.
        using var factory = new AuthWebFactory { Compliance = PopulatedStore(), AuthzMode = "Enforce", Authz = new FakeAuthzStore() };
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path, AuthWebFactory.MakeUser("u1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-control-id=\"ctrl-a\"", html, StringComparison.Ordinal);
        Assert.Contains("data-control-id=\"ctrl-b\"", html, StringComparison.Ordinal);
        Assert.Contains("data-collector-id=\"collector-a\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorTakesOnlyComplianceStore()
    {
        var ctor = Assert.Single(typeof(EvidenceCollectorsModel).GetConstructors());
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        Assert.Equal([typeof(IComplianceStore)], paramTypes);
    }
}
