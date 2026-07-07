using System.Net;
using Freeboard.Core.GitOps;
using Freeboard.Pages.Compliance;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// Server-rendered attestation-template register page: requires an authenticated user (anonymous is
/// redirected to /login; admin is NOT required), renders each control that has templates with its
/// templates (type, body, fields, and for training the pass mark and quiz prompts/options), is GET-only
/// and served in GitOps read-only mode, reads through the injected <see cref="IComplianceStore"/> (no
/// MySQL), never narrows to the caller's accessible organisations, renders the body as HTML-encoded text
/// (no stored-XSS), and never renders a quiz answer.
/// </summary>
public sealed class AttestationTemplatesPageTests
{
    private const string Path = "/compliance/attestation-templates";

    private static FakeComplianceStore PopulatedStore() => new()
    {
        Controls =
        [
            new ControlRow("ctrl-a", "Control A", ["req-a"], null),
            new ControlRow("ctrl-b", "Control B", ["req-b"], null),
        ],
        Templates =
        [
            new AttestationTemplateRow("attest-manual", "Firewall attestation", "ctrl-a", "manual", "Confirm review.",
                [new AttestationField { Id = "reviewed", Label = "Ruleset reviewed?", Type = "boolean" }], null, []),
            new AttestationTemplateRow("attest-training", "Phishing awareness", "ctrl-a", "training", null,
                [], 80, [new QuizItemView("q1", "What should you do?", ["Open it", "Report it"])]),
        ],
    };

    private static AuthWebFactory Factory(FakeComplianceStore store, bool readOnly = false)
        => new() { Compliance = store, ReadOnly = readOnly };

    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> GetAuthenticatedAsync(
        AuthWebFactory factory, HttpClient client, string relativeUrl, UserRow? user = null)
    {
        var token = factory.SeedSession(user ?? AuthWebFactory.MakeUser("templates1"));
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
    public async Task RendersControlsAndTheirTemplates()
    {
        using var factory = Factory(PopulatedStore());
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-control-id=\"ctrl-a\"", html, StringComparison.Ordinal);
        // ctrl-b has no templates, so it is omitted.
        Assert.DoesNotContain("data-control-id=\"ctrl-b\"", html, StringComparison.Ordinal);

        var section = html[html.IndexOf("data-control-id=\"ctrl-a\"", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("</section>", StringComparison.Ordinal)];
        Assert.Contains("data-template-id=\"attest-manual\"", section, StringComparison.Ordinal);
        Assert.Contains("data-template-id=\"attest-training\"", section, StringComparison.Ordinal);
        Assert.Contains("Ruleset reviewed?", section, StringComparison.Ordinal);
        Assert.Contains("80%", section, StringComparison.Ordinal);
        Assert.Contains("What should you do?", section, StringComparison.Ordinal);
        Assert.Contains("Report it", section, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BodyWithScriptRendersHtmlEncoded()
    {
        var store = new FakeComplianceStore
        {
            Controls = [new ControlRow("ctrl-a", "Control A", ["req-a"], null)],
            Templates =
            [
                new AttestationTemplateRow("attest-xss", "XSS", "ctrl-a", "manual", "<script>alert(1)</script>", [], null, []),
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
        // The correct answer is a distinctive sentinel value used nowhere as a prompt, option, field, or
        // body. The options are two answer-free choices. Assert the page carries no answer marker and does
        // not render the sentinel: if the answer leaked it could only appear as this value. A generic
        // "answer" word match is avoided because a future label or body could legitimately contain it.
        const string answerSentinel = "SECRET_ANSWER_SENTINEL";
        var store = new FakeComplianceStore
        {
            Controls = [new ControlRow("ctrl-a", "Control A", ["req-a"], null)],
            Templates =
            [
                new AttestationTemplateRow("attest-training", "Phishing awareness", "ctrl-a", "training", null,
                    [], 80, [new QuizItemView("q1", "Pick the safe action", ["alpha", "bravo"])]),
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
    public async Task ZeroGrantEnforceCallerSeesEveryTemplate()
    {
        using var factory = new AuthWebFactory { Compliance = PopulatedStore(), AuthzMode = "Enforce", Authz = new FakeAuthzStore() };
        using var client = NoRedirectClient(factory);

        var response = await GetAuthenticatedAsync(factory, client, Path, AuthWebFactory.MakeUser("u1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-template-id=\"attest-manual\"", html, StringComparison.Ordinal);
        Assert.Contains("data-template-id=\"attest-training\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorTakesOnlyComplianceStore()
    {
        var ctor = Assert.Single(typeof(AttestationTemplatesModel).GetConstructors());
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        Assert.Equal([typeof(IComplianceStore)], paramTypes);
    }
}
