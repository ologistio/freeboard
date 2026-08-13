using System.Net;
using Freeboard.Core.GitOps;
using Freeboard.Pages.Compliance;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// Server-rendered collector register page: requires an authenticated user (anonymous is redirected to
/// /login; admin is NOT required), renders each control with its evaluation rule and its attached
/// collectors (type, provider, vendor, frequency, threshold, and the typed config), is GET-only and
/// served in GitOps read-only mode, reads through the injected <see cref="IComplianceStore"/> (no MySQL),
/// keeps the ROW set global while narrowing the vendor id to the caller's accessible asset set, renders
/// an attestation body as HTML-encoded text (no stored-XSS), and never renders a quiz answer.
///
/// The old per-entry <c>data-config-key="&lt;key&gt;"</c> attribute is GONE and no test looks for it. It
/// existed to label an open, page-unknown key set; the merged block renders five named sections instead,
/// each with its own marker, so the attribute has nothing left to label. It is not one of the preserved
/// markers - this is a deliberate markup deletion, not an assertion that quietly went missing.
/// </summary>
public sealed class CollectorsPageTests
{
    private const string Path = "/settings/collectors";

    private static CollectorRow Collector(
        string id, string title, string control, string type,
        string? vendor = null, string? provider = null, string frequency = "daily", int? threshold = null,
        CollectorConfigView? config = null) =>
        new(id, title, control, vendor, type, provider, frequency, threshold, config ?? CollectorConfigView.Empty);

    private static FakeComplianceStore PopulatedStore() => new()
    {
        Assets = [TestAssets.Org("org-a"), TestAssets.Vendor("vendor-a", "org-a")],
        Controls =
        [
            new ControlRow("ctrl-a", "Control A", ["req-a"], "all"),
            new ControlRow("ctrl-b", "Control B", ["req-b"], null),
        ],
        Collectors =
        [
            Collector(
                "collector-a", "Endpoint MFA", "ctrl-a", "integration", vendor: "vendor-a", provider: "fleet",
                threshold: 100,
                config: new CollectorConfigView(
                    null, [], null, [], [new Check { SourceKey = "12", Name = "mfa-enforced", Severity = "Hard" }])),
            Collector(
                "attest-manual", "Firewall attestation", "ctrl-a", "manual", frequency: "annual",
                config: new CollectorConfigView(
                    "Confirm review.",
                    [new AttestationField { Id = "reviewed", Label = "Ruleset reviewed?", Type = "boolean" }],
                    null, [], [])),
            Collector(
                "attest-training", "Phishing awareness", "ctrl-a", "training", frequency: "annual",
                config: new CollectorConfigView(
                    null, [], 80, [new QuizItemView("q1", "What should you do?", ["Open it", "Report it"])], [])),
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
        // Every control renders, including one with no collectors at all.
        Assert.Contains("data-control-id=\"ctrl-a\"", html, StringComparison.Ordinal);
        Assert.Contains("data-control-id=\"ctrl-b\"", html, StringComparison.Ordinal);

        var section = html[html.IndexOf("data-control-id=\"ctrl-a\"", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("</section>", StringComparison.Ordinal)];
        Assert.Contains("all", section, StringComparison.Ordinal);
        Assert.Contains("data-collector-id=\"collector-a\"", section, StringComparison.Ordinal);
        Assert.Contains("integration", section, StringComparison.Ordinal);
        Assert.Contains("fleet", section, StringComparison.Ordinal);
        Assert.Contains("vendor-a", section, StringComparison.Ordinal);
        Assert.Contains("daily", section, StringComparison.Ordinal);
        Assert.Contains("100%", section, StringComparison.Ordinal);
    }

    // The merge's point: a former template renders in the SAME per-collector block as a data source.
    [Fact]
    public async Task AttestationAndTrainingRenderInTheSameBlockAsADataSource()
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        var html = await response.Content.ReadAsStringAsync();

        var section = html[html.IndexOf("data-control-id=\"ctrl-a\"", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("</section>", StringComparison.Ordinal)];
        Assert.Contains("data-collector-id=\"attest-manual\"", section, StringComparison.Ordinal);
        Assert.Contains("data-collector-id=\"attest-training\"", section, StringComparison.Ordinal);
        Assert.Contains("Confirm review.", section, StringComparison.Ordinal);
        Assert.Contains("Ruleset reviewed?", section, StringComparison.Ordinal);
        Assert.Contains("80%", section, StringComparison.Ordinal);
        Assert.Contains("What should you do?", section, StringComparison.Ordinal);
        Assert.Contains("Report it", section, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IntegrationCollectorChecksRender()
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("data-check-name=\"mfa-enforced\"", html, StringComparison.Ordinal);
        Assert.Contains("Hard", html, StringComparison.Ordinal);
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
    public async Task BodyWithScriptRendersHtmlEncoded()
    {
        var store = new FakeComplianceStore
        {
            Controls = [new ControlRow("ctrl-a", "Control A", ["req-a"], "all")],
            Collectors =
            [
                Collector(
                    "attest-xss", "XSS", "ctrl-a", "manual", frequency: "annual",
                    config: new CollectorConfigView("<script>alert(1)</script>", [], null, [], [])),
            ],
        };
        using var factory = Factory(store);
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        var html = await response.Content.ReadAsStringAsync();

        // The body markup is HTML-encoded, not a live tag.
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuizAnswerNeverRenders()
    {
        // The sentinel is not in the fixture and structurally cannot be: QuizItemView has no answer
        // member. It is belt-and-braces against a future view type that gains one - a distinctive value
        // to search for that no prompt, option, field, or body would ever contain. The real redaction
        // test is at the store boundary in MySqlIntegrationTests, which persists an answer and then
        // proves the read model cannot hold it. A generic "answer" word match is avoided here because a
        // future label or body could legitimately contain it.
        const string answerSentinel = "SECRET_ANSWER_SENTINEL";
        var store = new FakeComplianceStore
        {
            Controls = [new ControlRow("ctrl-a", "Control A", ["req-a"], "all")],
            Collectors =
            [
                Collector(
                    "attest-training", "Phishing awareness", "ctrl-a", "training", frequency: "annual",
                    config: new CollectorConfigView(
                        null, [], 80, [new QuizItemView("q1", "Pick the safe action", ["alpha", "bravo"])], [])),
            ],
        };
        using var factory = Factory(store);
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("data-answer", html, StringComparison.Ordinal);
        Assert.DoesNotContain(answerSentinel, html, StringComparison.Ordinal);
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
    public async Task EmptyStoreRendersEmptyState()
    {
        using var factory = Factory(new FakeComplianceStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-empty", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZeroGrantEnforceCallerSeesEveryControlAndCollector()
    {
        // The register does not narrow the ROW set by accessible organisation, so a zero-grant Enforce
        // caller still sees every control and collector.
        using var factory = new AuthWebFactory { Compliance = PopulatedStore(), AuthzMode = "Enforce", Authz = new FakeAuthzStore() };
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path, AuthWebFactory.MakeUser("u1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-control-id=\"ctrl-a\"", html, StringComparison.Ordinal);
        Assert.Contains("data-control-id=\"ctrl-b\"", html, StringComparison.Ordinal);
        Assert.Contains("data-collector-id=\"collector-a\"", html, StringComparison.Ordinal);
        Assert.Contains("data-collector-id=\"attest-training\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/settings/evidence-collectors")]
    [InlineData("/settings/attestation-templates")]
    public async Task RetiredRoutesAreUnmapped(string retired)
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, retired);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void ConstructorTakesComplianceStoreRequestCacheAndAssetAccess()
    {
        var ctor = Assert.Single(typeof(CollectorsModel).GetConstructors());
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        // The assets come from the cache's shared asset read; the store still serves this page's own rows.
        Assert.Equal(
            [typeof(IComplianceStore), typeof(Freeboard.Authz.AuthzRequestCache), typeof(IAssetAccess)],
            paramTypes);
    }

    [Fact]
    public async Task UnreadableVendorRendersExactlyAsAnUnsetOne()
    {
        // vendor-a is owned by org-x, which the reader cannot reach. The collector ROW still renders;
        // its vendor id must not, and the row must be indistinguishable from one with no vendor.
        var store = PopulatedStore();
        store.Assets = [TestAssets.Org("org-a"), TestAssets.Org("org-x"), TestAssets.Vendor("vendor-a", "org-x")];
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a");
        using var factory = new AuthWebFactory { Compliance = store, AuthzMode = "Enforce", Authz = authz };
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path, AuthWebFactory.MakeUser("u1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-collector-id=\"collector-a\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("vendor-a", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadableVendorStillRenders()
    {
        var authz = new FakeAuthzStore().GrantComplianceReader("u1", "org-a");
        using var factory = new AuthWebFactory { Compliance = PopulatedStore(), AuthzMode = "Enforce", Authz = authz };
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path, AuthWebFactory.MakeUser("u1"));

        Assert.Contains("vendor-a", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
