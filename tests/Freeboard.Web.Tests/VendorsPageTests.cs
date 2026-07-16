using System.Net;
using Freeboard.Pages.Compliance;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// Server-rendered vendor register page: requires an authenticated user (an anonymous browser is
/// redirected to /login; the admin role is NOT required), lists each vendor the caller may see with its
/// scopes, shows the justification for every Out exception, is GET-only and served in GitOps read-only
/// mode, reads through the injected <see cref="IComplianceStore"/> (no MySQL), and narrows to vendors
/// whose owner is in the caller's accessible-org set (a vendor with a hidden owner is hidden with its
/// vendor-scope justifications).
/// </summary>
public sealed class VendorsPageTests
{
    private const string Path = "/compliance/vendors";

    private static FakeComplianceStore PopulatedStore() => new()
    {
        Organisations = [new OrganisationRow("org-a", "Org A", "Company", null)],
        Vendors =
        [
            new VendorRow("vendor-a", "Vendor A", "org-a"),
            new VendorRow("vendor-b", "Vendor B", "org-a"),
        ],
        VendorScopes =
        [
            new VendorScopeRow("vs-a", "Except req-a", "vendor-a", "req-a", null, "Out", "Supports MFA but not SSO."),
            new VendorScopeRow("vs-b", "Include ctrl-a", "vendor-a", null, "ctrl-a", "In", null),
        ],
    };

    private static AuthWebFactory Factory(FakeComplianceStore store, bool readOnly = false)
        => new() { Compliance = store, ReadOnly = readOnly };

    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> GetAuthenticatedAsync(
        AuthWebFactory factory, HttpClient client, string relativeUrl, UserRow? user = null)
    {
        var token = factory.SeedSession(user ?? AuthWebFactory.MakeUser("vendors1"));
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
    public async Task RendersVendorsAndTheirExceptions()
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-vendor-id=\"vendor-a\"", html, StringComparison.Ordinal);
        Assert.Contains("data-vendor-id=\"vendor-b\"", html, StringComparison.Ordinal);
        Assert.Contains("Vendor A", html, StringComparison.Ordinal);
        Assert.Contains("data-scope-id=\"vs-a\"", html, StringComparison.Ordinal);
        Assert.Contains("data-target=\"req-a\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryOutExceptionShowsItsJustification()
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        var html = await response.Content.ReadAsStringAsync();

        // The Out scope renders its justification text next to the disposition.
        var scopeRow = html[html.IndexOf("data-scope-id=\"vs-a\"", StringComparison.Ordinal)..];
        scopeRow = scopeRow[..scopeRow.IndexOf("</tr>", StringComparison.Ordinal)];
        Assert.Contains("Out", scopeRow, StringComparison.Ordinal);
        Assert.Contains("Supports MFA but not SSO.", scopeRow, StringComparison.Ordinal);
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
        Assert.DoesNotContain("data-vendor-id", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnerExcludedEnforceCallerSeesNoVendorOrJustification()
    {
        // The register narrows by vendor owner. Under strict Enforce with no grants the accessible-org
        // set is empty, so no vendor renders and no Out justification leaks.
        using var factory = new AuthWebFactory { Compliance = PopulatedStore(), AuthzMode = "Enforce", Authz = new FakeAuthzStore() };
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path, AuthWebFactory.MakeUser("u1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("data-vendor-id", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Supports MFA but not SSO.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NarrowsToVendorsWhoseOwnerTheCallerCanAccess()
    {
        // Two vendors owned by different orgs; the reader is granted on org-a only. Under Enforce the
        // page renders vendor-a and its justification, but neither vendor-b nor vendor-b's justification.
        var store = new FakeComplianceStore
        {
            Organisations =
            [
                new OrganisationRow("org-a", "Org A", "Company", null),
                new OrganisationRow("org-b", "Org B", "Company", null),
            ],
            Vendors =
            [
                new VendorRow("vendor-a", "Vendor A", "org-a"),
                new VendorRow("vendor-b", "Vendor B", "org-b"),
            ],
            VendorScopes =
            [
                new VendorScopeRow("vs-a", "Except req-a", "vendor-a", "req-a", null, "Out", "Visible justification."),
                new VendorScopeRow("vs-b", "Except req-b", "vendor-b", "req-b", null, "Out", "Hidden justification."),
            ],
        };
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a");
        using var factory = new AuthWebFactory { Compliance = store, AuthzMode = "Enforce", Authz = authz };
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path, AuthWebFactory.MakeUser("u1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-vendor-id=\"vendor-a\"", html, StringComparison.Ordinal);
        Assert.Contains("Visible justification.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-vendor-id=\"vendor-b\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden justification.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorTakesComplianceStoreAndOrgAccess()
    {
        var ctor = Assert.Single(typeof(VendorsModel).GetConstructors());
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToHashSet();

        Assert.Equal(new HashSet<Type> { typeof(IComplianceStore), typeof(IOrgAccess) }, paramTypes);
    }
}
