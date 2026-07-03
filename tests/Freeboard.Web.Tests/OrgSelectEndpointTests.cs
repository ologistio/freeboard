using System.Net;
using System.Security.Claims;
using Freeboard.Persistence;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// The GET <c>/org/select</c> endpoint: it sets or clears the <c>freeboard-org</c> view cookie and
/// redirects back to the selecting page. Requires an authenticated user (an anonymous browser is
/// 302-redirected to /login, not 401'd), preserves the return target's query state, falls back to an
/// app page on a non-local return, and is served in GitOps read-only mode.
/// </summary>
public sealed class OrgSelectEndpointTests
{
    private const string SoaPath = "/compliance/statement-of-applicability";

    private static FakeComplianceStore PopulatedStore() => new()
    {
        Standards = [new StandardRow("std-a", "Standard A", "1.0", "Example Authority", null, null)],
        Organisations = [new OrganisationRow("org-a", "Org A", "Company", null)],
        Scopes = [new ScopeRow("scope-a", "Scope A", "org-a", "std-a", "In")],
    };

    private static AuthWebFactory Factory(bool readOnly = false, IOrgAccess? orgAccess = null)
        => new() { Compliance = PopulatedStore(), ReadOnly = readOnly, OrgAccess = orgAccess };

    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static HttpRequestMessage AuthGet(AuthWebFactory factory, string url, string? orgCookie = null)
    {
        var token = factory.SeedSession(AuthWebFactory.MakeUser("orgsel"));
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var cookie = $"{SessionCookie.Name}={token}";
        if (orgCookie is not null)
        {
            cookie += $"; {OrgSelection.CookieName}={orgCookie}";
        }

        request.Headers.Add("Cookie", cookie);
        return request;
    }

    private static string SelectUrl(string? org, string returnTarget)
    {
        var query = $"return={Uri.EscapeDataString(returnTarget)}";
        return org is null ? $"/org/select?{query}" : $"/org/select?org={Uri.EscapeDataString(org)}&{query}";
    }

    private static string SetCookieFor(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith($"{name}=", StringComparison.Ordinal)) ?? string.Empty
            : string.Empty;

    [Fact]
    public async Task SelectingSetsCookieWithSecureAttributesAndRedirectsBack()
    {
        using var factory = Factory();
        using var client = NoRedirectClient(factory);

        using var response = await client.SendAsync(AuthGet(factory, SelectUrl("org-a", SoaPath)));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(SoaPath, response.Headers.Location!.OriginalString);

        var setCookie = SetCookieFor(response, OrgSelection.CookieName);
        Assert.Contains("freeboard-org=org-a", setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChoosingAllOrganisationsClearsCookie()
    {
        using var factory = Factory();
        using var client = NoRedirectClient(factory);

        using var response = await client.SendAsync(AuthGet(factory, SelectUrl(null, SoaPath), orgCookie: "org-a"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var setCookie = SetCookieFor(response, OrgSelection.CookieName);
        // A cleared cookie is emitted with an empty value and a past expiry.
        Assert.Contains("freeboard-org=;", setCookie, StringComparison.Ordinal);
        Assert.Contains("expires=", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StandardQueryReturnSurvivesSelectingAnOrg()
    {
        using var factory = Factory();
        using var client = NoRedirectClient(factory);

        var returnTarget = $"{SoaPath}?standard=std-a";
        using var response = await client.SendAsync(AuthGet(factory, SelectUrl("org-a", returnTarget)));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(returnTarget, response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task StandardQueryReturnSurvivesChoosingAll()
    {
        using var factory = Factory();
        using var client = NoRedirectClient(factory);

        var returnTarget = $"{SoaPath}?standard=std-a";
        using var response = await client.SendAsync(AuthGet(factory, SelectUrl(null, returnTarget)));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(returnTarget, response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task NonLocalReturnRedirectsToFallback()
    {
        using var factory = Factory();
        using var client = NoRedirectClient(factory);

        using var response = await client.SendAsync(AuthGet(factory, SelectUrl("org-a", "https://evil.example/steal")));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(SoaPath, response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task InaccessibleOrgDoesNotTakeEffect()
    {
        // org-a is present but NOT accessible. The endpoint still writes the view cookie by design -
        // fail-closed happens at read (Resolve), not at write - so this replays the cookie the endpoint
        // actually set and proves the endpoint->cookie->page flow resolves to All Organisations.
        using var factory = Factory(orgAccess: new EmptyOrgAccess());
        using var client = NoRedirectClient(factory);

        using var select = await client.SendAsync(AuthGet(factory, SelectUrl("org-a", SoaPath)));
        Assert.Equal(HttpStatusCode.Redirect, select.StatusCode);

        var selectedCookie = CookieValue(SetCookieFor(select, OrgSelection.CookieName));
        Assert.Equal("org-a", selectedCookie);

        using var page = await client.SendAsync(AuthGet(factory, $"{SoaPath}?standard=std-a", orgCookie: selectedCookie));
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("data-active-scope>All Organisations", html, StringComparison.Ordinal);
    }

    /// <summary>The value of a Set-Cookie header (the <c>name=value</c> segment before the attributes).</summary>
    private static string CookieValue(string setCookie)
    {
        var pair = setCookie.Split(';', 2)[0];
        var eq = pair.IndexOf('=', StringComparison.Ordinal);
        return eq < 0 ? string.Empty : pair[(eq + 1)..];
    }

    [Fact]
    public async Task AnonymousRedirectsToLoginAndSetsNoCookie()
    {
        using var factory = Factory();
        using var client = NoRedirectClient(factory);

        using var response = await client.GetAsync(SelectUrl("org-a", SoaPath));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location!.OriginalString);
        Assert.Empty(SetCookieFor(response, OrgSelection.CookieName));
    }

    [Fact]
    public async Task ServedInReadOnlyMode()
    {
        using var factory = Factory(readOnly: true);
        using var client = NoRedirectClient(factory);

        using var response = await client.SendAsync(AuthGet(factory, SelectUrl("org-a", SoaPath)));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("freeboard-org=org-a", SetCookieFor(response, OrgSelection.CookieName), StringComparison.Ordinal);
    }

    private sealed class EmptyOrgAccess : IOrgAccess
    {
        public IReadOnlySet<string> AccessibleOrgIds(
            ClaimsPrincipal user, IReadOnlyList<OrganisationRow> organisations)
            => new HashSet<string>(StringComparer.Ordinal);
    }
}
