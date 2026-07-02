using System.Net;
using Freeboard.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// Server-rendered Statement of Applicability page: requires an authenticated user (an anonymous
/// browser is redirected to /login; the admin role is NOT required), renders the resolved node list
/// for a chosen standard ordered by id, is GET-only and served in GitOps read-only mode, and reads
/// through the injected <see cref="IComplianceStore"/> (no MySQL).
/// </summary>
public sealed class StatementOfApplicabilityPageTests
{
    private const string Path = "/compliance/statement-of-applicability";

    private static FakeComplianceStore PopulatedStore() => new()
    {
        Standards = [new StandardRow("std-a", "Standard A", "1.0", "Example Authority", null, null)],
        Organisations =
        [
            new OrganisationRow("org-a", "Org A", "Company", null),
            new OrganisationRow("org-eng", "Engineering", "Department", "org-a"),
        ],
        Scopes = [new ScopeRow("scope-a", "Scope A", "org-a", "std-a", "In")],
    };

    private static AuthWebFactory Factory(FakeComplianceStore store, bool readOnly = false)
        => new() { Compliance = store, ReadOnly = readOnly };

    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>A GET carrying a non-admin (member) session cookie, so the page auth is satisfied.</summary>
    private static async Task<HttpResponseMessage> GetAuthenticatedAsync(
        AuthWebFactory factory, HttpClient client, string relativeUrl)
    {
        var token = factory.SeedSession(AuthWebFactory.MakeUser("soa1"));
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task AnonymousGetRedirectsToLogin()
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        var response = await client.GetAsync($"{Path}?standard=std-a");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task RendersResolvedNodesForChosenStandard()
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        // Explicit In at org-a; org-eng inherits In. Ordered by id.
        var orgIndex = html.IndexOf("org-a", StringComparison.Ordinal);
        var engIndex = html.IndexOf("org-eng", StringComparison.Ordinal);
        Assert.True(orgIndex >= 0 && engIndex > orgIndex, "nodes should render ordered by id");
        Assert.Contains("explicit", html, StringComparison.Ordinal);
        Assert.Contains("inherited", html, StringComparison.Ordinal);
        Assert.Contains("In", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoStandardChosenRendersSelectorWithoutNodes()
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Standard A", html, StringComparison.Ordinal); // selector option
        Assert.DoesNotContain("data-node-id", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServedInReadOnlyModeToAuthenticatedUser()
    {
        using var factory = Factory(PopulatedStore(), readOnly: true);
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnreachableStoreRendersNoticeNot500()
    {
        using var factory = Factory(new FakeComplianceStore { Unreachable = true });
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, $"{Path}?standard=std-a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("could not be reached", html, StringComparison.Ordinal);
    }
}
