using System.Globalization;
using System.Net;
using Freeboard.Pages.Compliance;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace Freeboard.Web.Tests;

/// <summary>
/// Server-rendered vendor register page: requires an authenticated user (an anonymous browser is
/// redirected to /login; the admin role is NOT required), lists each vendor the caller may see with its
/// scopes, shows the justification for every Out exception, is GET-only and served in GitOps read-only
/// mode, reads through the injected <see cref="IComplianceStore"/> (no MySQL), and narrows to the
/// vendors in the caller's accessible asset set - which admits a vendor exactly when its owner resolves
/// into the caller's organisation union (a vendor with a hidden owner is hidden with its vendor-scope
/// justifications).
/// </summary>
public sealed class VendorsPageTests
{
    private const string Path = "/compliance/vendors";

    private static FakeComplianceStore PopulatedStore() => new()
    {
        Assets =
        [
            TestAssets.Org("org-a", title: "Org A"),
            TestAssets.Vendor("vendor-a", "org-a", title: "Vendor A"),
            TestAssets.Vendor("vendor-b", "org-a", title: "Vendor B"),
        ],
        Scopes =
        [
            new ScopeRow("vs-a", "Except req-a", "vendor-a", null, "req-a", null, "Out", "Supports MFA but not SSO."),
            new ScopeRow("vs-b", "Include ctrl-a", "vendor-a", null, null, "ctrl-a", "In", null),
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
    public async Task RendersTierAndDataClassesAsNeutralTags()
    {
        var store = new FakeComplianceStore
        {
            Assets =
            [
                TestAssets.Org("org-a", title: "Org A"),
                TestAssets.Vendor("vendor-a", "org-a", title: "Vendor A", tier: "Critical", dataClasses: ["pii", "payment-card"]),
            ],
        };
        using var factory = Factory(store);
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        var row = DirectoryRow(await response.Content.ReadAsStringAsync(), "vendor-a");

        // Neutral tone only: S3 keeps red for failing and overdue, and the same rule rules out amber.
        Assert.Contains("<span class=\"fb-tag\">Critical</span>", row, StringComparison.Ordinal);
        Assert.Contains("<span class=\"fb-tag\">PII</span>", row, StringComparison.Ordinal);
        Assert.Contains("<span class=\"fb-tag\">Payment card</span>", row, StringComparison.Ordinal);
        Assert.DoesNotContain("fb-tag--", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UntrackedTierAndDataClassesRenderAsExplicitEmpties()
    {
        // O2/S6: an absent facet says so rather than defaulting to a plausible value.
        var store = new FakeComplianceStore
        {
            Assets = [TestAssets.Org("org-a", title: "Org A"), TestAssets.Vendor("vendor-a", "org-a", title: "Vendor A")],
        };
        using var factory = Factory(store);
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        var row = DirectoryRow(await response.Content.ReadAsStringAsync(), "vendor-a");

        Assert.Equal(2, CountOccurrences(row, "<span class=\"fb-tdsub\">Not tracked</span>"));
        Assert.DoesNotContain("fb-tag", row, StringComparison.Ordinal);
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
        // The register narrows by the accessible asset set. Under strict Enforce with no grants that set
        // is empty, so no vendor renders and no Out justification leaks.
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
            Assets =
            [
                TestAssets.Org("org-a", title: "Org A"),
                TestAssets.Org("org-b", title: "Org B"),
                TestAssets.Vendor("vendor-a", "org-a", title: "Vendor A"),
                TestAssets.Vendor("vendor-b", "org-b", title: "Vendor B"),
            ],
            Scopes =
            [
                new ScopeRow("vs-a", "Except req-a", "vendor-a", null, "req-a", null, "Out", "Visible justification."),
                new ScopeRow("vs-b", "Except req-b", "vendor-b", null, "req-b", null, "Out", "Hidden justification."),
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
    public void ConstructorTakesTheStoreTheRequestCacheTheClockAndBothOptions()
    {
        var ctor = Assert.Single(typeof(VendorsModel).GetConstructors());
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToHashSet();

        Assert.Equal(
            new HashSet<Type>
            {
                typeof(IComplianceStore),
                typeof(Freeboard.Authz.AuthzRequestCache),
                typeof(IAssetAccess),
                typeof(TimeProvider),
                typeof(IOptions<Freeboard.Compliance.AssuranceOptions>),
                typeof(IOptions<Freeboard.GitOps.GitOpsOptions>),
            },
            paramTypes);
    }

    [Fact]
    public async Task DirectoryAndScopeTabsCarryTheirCounts()
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        var html = await response.Content.ReadAsStringAsync();

        // Two vendors in the directory tab, two scope rules in the scope-rules tab.
        Assert.Contains("id=\"vt-directory\"", html, StringComparison.Ordinal);
        Assert.Contains("Directory<span class=\"n\">2</span>", html, StringComparison.Ordinal);
        Assert.Contains("Scope rules<span class=\"n\">2</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitOpsModeSaysGitOwnsTheRegister()
    {
        using var factory = Factory(PopulatedStore(), readOnly: true);
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Git owns this register", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoAddVendorAffordanceWhileGitIsTheOnlyWritePath(bool readOnly)
    {
        using var factory = Factory(PopulatedStore(), readOnly);
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Add vendor", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyRegisterExplainsWhatWouldAppear()
    {
        using var factory = Factory(new FakeComplianceStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("data-empty", html, StringComparison.Ordinal);
        Assert.Contains("No vendors are registered.", html, StringComparison.Ordinal);
    }

    // Every assurance case runs against a fixed date and a fixed window: a fixture dated against the wall
    // clock changes state as the wall clock moves.
    private static readonly DateOnly Today = new(2026, 3, 1);

    private static VendorAssuranceRow Assurance(string vendorId, string standardId, DateOnly expires, int? warnDays = null) =>
        new(vendorId, standardId, expires, warnDays);

    private static AuthWebFactory AssuranceFactory(FakeComplianceStore store, int warnWindowDays = 90) => new()
    {
        Compliance = store,
        Clock = new FixedClock(Today),
        Settings = new Dictionary<string, string?>
        {
            ["Freeboard:Assurance:WarnWindowDays"] = warnWindowDays.ToString(CultureInfo.InvariantCulture),
        },
    };

    [Fact]
    public async Task RendersOneTonedStampPerAssurance()
    {
        var store = new FakeComplianceStore
        {
            Assets = [TestAssets.Org("org-a"), TestAssets.Vendor("vendor-a", "org-a", title: "Vendor A")],
            Standards =
            [
                new StandardRow("std-soc2", "SOC 2", null, null, null, null),
                new StandardRow("std-iso", "ISO 27001", null, null, null, null),
                new StandardRow("std-pci", "PCI DSS", null, null, null, null),
            ],
            Assurances =
            [
                Assurance("vendor-a", "std-soc2", Today.AddDays(365)),
                Assurance("vendor-a", "std-iso", Today.AddDays(5)),
                Assurance("vendor-a", "std-pci", Today.AddDays(-3)),
            ],
        };
        using var factory = AssuranceFactory(store);
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        var row = DirectoryRow(await response.Content.ReadAsStringAsync(), "vendor-a");

        // Valid takes the untinted base, not the pass green: a certification is a fact on file, not a
        // Freeboard verdict. The word differs per state, so the state survives with colour removed (S2).
        Assert.Contains("<span class=\"fb-stamp\">SOC 2", row, StringComparison.Ordinal);
        Assert.Contains("expires Mar 1", row, StringComparison.Ordinal);
        Assert.Contains("<span class=\"fb-stamp warn\">ISO 27001", row, StringComparison.Ordinal);
        Assert.Contains("expires in 5 days", row, StringComparison.Ordinal);
        Assert.Contains("<span class=\"fb-stamp fail\">PCI DSS", row, StringComparison.Ordinal);
        Assert.Contains("expired 3 days ago", row, StringComparison.Ordinal);
        Assert.DoesNotContain("None on file", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVendorWithNoAssuranceReadsAsAnExplicitEmptyWithNoStamp()
    {
        var store = new FakeComplianceStore
        {
            Assets = [TestAssets.Org("org-a"), TestAssets.Vendor("vendor-a", "org-a")],
        };
        using var factory = AssuranceFactory(store);
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        var row = DirectoryRow(await response.Content.ReadAsStringAsync(), "vendor-a");

        Assert.Contains("<span class=\"fb-tdsub\">None on file</span>", row, StringComparison.Ordinal);
        Assert.DoesNotContain("fb-stamp", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LapsingAssurancesTurnTheNoticeAmberAndNameTheCount()
    {
        var store = new FakeComplianceStore
        {
            Assets =
            [
                TestAssets.Org("org-a"),
                TestAssets.Vendor("vendor-a", "org-a"),
                TestAssets.Vendor("vendor-b", "org-a"),
                TestAssets.Vendor("vendor-c", "org-a"),
            ],
            Assurances =
            [
                // vendor-a counts once however many of its certifications lapse; vendor-c's is valid.
                Assurance("vendor-a", "std-soc2", Today.AddDays(5)),
                Assurance("vendor-a", "std-iso", Today.AddDays(-1)),
                Assurance("vendor-b", "std-soc2", Today.AddDays(5)),
                Assurance("vendor-c", "std-soc2", Today.AddDays(365)),
            ],
        };
        using var factory = AssuranceFactory(store);
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("notice-warning", html, StringComparison.Ordinal);
        Assert.Contains("data-lapsing-count=\"2\"", html, StringComparison.Ordinal);
        Assert.Contains("2 vendors have a lapsing certification.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredOnlyVendorTurnsTheNoticeAmber()
    {
        // An expiring-only fixture cannot satisfy this case: an expired certificate must raise the notice
        // exactly as an expiring one does.
        var store = new FakeComplianceStore
        {
            Assets = [TestAssets.Org("org-a"), TestAssets.Vendor("vendor-a", "org-a")],
            Assurances = [Assurance("vendor-a", "std-soc2", Today.AddDays(-10))],
        };
        using var factory = AssuranceFactory(store);
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("notice-warning", html, StringComparison.Ordinal);
        Assert.Contains("data-lapsing-count=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("1 vendor has a lapsing certification.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingLapsingLeavesTheNoticeNeutral()
    {
        var store = new FakeComplianceStore
        {
            Assets = [TestAssets.Org("org-a"), TestAssets.Vendor("vendor-a", "org-a")],
            Assurances = [Assurance("vendor-a", "std-soc2", Today.AddDays(365))],
        };
        using var factory = AssuranceFactory(store);
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("notice-warning", html, StringComparison.Ordinal);
        Assert.DoesNotContain("lapsing certification", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HiddenVendorsLapseIsNotCounted()
    {
        // A lapsing vendor outside the accessible set leaves the notice neutral and out of the count.
        var store = new FakeComplianceStore
        {
            Assets =
            [
                TestAssets.Org("org-a"),
                TestAssets.Org("org-b"),
                TestAssets.Vendor("vendor-a", "org-a"),
                TestAssets.Vendor("vendor-b", "org-b"),
            ],
            Assurances = [Assurance("vendor-b", "std-soc2", Today.AddDays(5))],
        };
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a");
        using var factory = new AuthWebFactory
        {
            Compliance = store,
            AuthzMode = "Enforce",
            Authz = authz,
            Clock = new FixedClock(Today),
        };
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path, AuthWebFactory.MakeUser("u1"));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("data-vendor-id=\"vendor-a\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-vendor-id=\"vendor-b\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("notice-warning", html, StringComparison.Ordinal);
        Assert.DoesNotContain("lapsing certification", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEnormousConfiguredWindowRendersRatherThanThrowing()
    {
        // The window arrives unvalidated, and a day-number difference cannot overflow where AddDays would.
        var store = new FakeComplianceStore
        {
            Assets = [TestAssets.Org("org-a"), TestAssets.Vendor("vendor-a", "org-a")],
            Assurances =
            [
                Assurance("vendor-a", "std-soc2", Today.AddDays(500)),
                Assurance("vendor-a", "std-iso", Today.AddDays(500), warnDays: int.MaxValue),
            ],
        };
        using var factory = AssuranceFactory(store, warnWindowDays: int.MaxValue);
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = DirectoryRow(await response.Content.ReadAsStringAsync(), "vendor-a");
        Assert.Equal(2, CountOccurrences(row, "fb-stamp warn"));
    }

    private sealed class FixedClock(DateOnly today) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }

    /// <summary>One vendor's Directory row, so a per-row assertion cannot pass on another row's markup.</summary>
    private static string DirectoryRow(string html, string vendorId)
    {
        var row = html[html.IndexOf($"data-vendor-id=\"{vendorId}\"", StringComparison.Ordinal)..];
        return row[..row.IndexOf("</tr>", StringComparison.Ordinal)];
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
