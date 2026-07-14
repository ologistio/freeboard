using System.Net;
using System.Text.RegularExpressions;
using Freeboard.Auth;
using Freeboard.Core.Authz;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Freeboard.Web.Tests;

/// <summary>
/// Guards the IA route move against a stale in-app link: it renders a page, extracts a real
/// <c>href</c> from the returned HTML, and follows exactly that href - so a link left pointing at an
/// old <c>/admin</c> or <c>/compliance</c> path (which no longer exists after the clean-break move)
/// would fail here rather than ship silently. Driving the rendered link, not a hand-built URL, is what
/// makes this catch a stale href.
/// </summary>
public sealed class ShellRouteReachabilityTests
{
    private static HttpClient NoRedirectClient(AuthWebFactory factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task CustomRolesEditLinkResolvesAtItsNewSettingsRoute()
    {
        using var factory = new AuthWebFactory { CustomPoliciesEntitled = true };
        var token = factory.SeedSession(AuthWebFactory.MakeUser("admin1", role: "admin"));
        using var client = NoRedirectClient(factory);
        factory.AuthzAdmin.SeedCustomRole("custom:auditor", AuthzActions.OrgRead);

        var listHtml = await GetHtmlAsync(client, token, "/settings/custom-roles");

        // Pull the Edit link the list actually rendered, then follow that exact href.
        var href = Regex.Match(listHtml, @"href=""(/settings/custom-roles/designer/[^""]+)""").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(href), "no custom-role Edit link was rendered");

        using var request = new HttpRequestMessage(HttpMethod.Get, href);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var editHtml = await response.Content.ReadAsStringAsync();
        Assert.Contains("Edit custom role", editHtml, StringComparison.Ordinal);
    }

    private static async Task<string> GetHtmlAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={token}");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}
