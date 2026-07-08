using Freeboard.Auth;
using Freeboard.Authz;
using Freeboard.Core.Authz;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Freeboard.Web.Tests;

/// <summary>
/// The route-metadata guard: every mutating filter-gated compliance/authz/user-admin route carries a
/// permission requirement AND <c>alwaysEnforce: true</c> (the alwaysEnforce half is load-bearing: a
/// mutating route wired false would be silently mode-relaxed). This metadata test - not runtime "fail
/// closed" - is what stops an ungated or mis-wired filter from shipping. The dual-purpose session
/// routes carry their declared cross-user permission metadata too; the in-handler check itself is
/// guaranteed by the behavioral tests, not here.
/// </summary>
public sealed class RouteAuthzMetadataTests
{
    private static IReadOnlyList<RouteEndpoint> Endpoints()
    {
        var factory = new AuthWebFactory();
        _ = factory.CreateClient(); // force the host + endpoints to build
        var sources = factory.Services.GetServices<EndpointDataSource>();
        return sources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();
    }

    private static bool IsMutating(RouteEndpoint e)
    {
        var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
        return methods.Any(m => m is "POST" or "PUT" or "DELETE" or "PATCH");
    }

    [Theory]
    [InlineData("PUT", "organisations/{id}", AuthzActions.OrgWrite)]
    [InlineData("DELETE", "organisations/{id}", AuthzActions.OrgWrite)]
    [InlineData("PUT", "scopes/{id}", AuthzActions.ComplianceScopeWrite)]
    [InlineData("DELETE", "scopes/{id}", AuthzActions.ComplianceScopeWrite)]
    [InlineData("PUT", "requirement-scopes/{id}", AuthzActions.ComplianceRequirementScopeWrite)]
    [InlineData("DELETE", "requirement-scopes/{id}", AuthzActions.ComplianceRequirementScopeWrite)]
    [InlineData("POST", "users", AuthzActions.UserManage)]
    [InlineData("POST", "users/{id}/disable", AuthzActions.UserManage)]
    [InlineData("PUT", "organisations/{orgId}/role-assignments", AuthzActions.AuthzAssignmentWrite)]
    [InlineData("PUT", "system-role-assignments", AuthzActions.SystemAdmin)]
    [InlineData("POST", "custom-roles", AuthzActions.SystemAdmin)]
    [InlineData("PUT", "custom-roles/{roleKey}", AuthzActions.SystemAdmin)]
    [InlineData("DELETE", "custom-roles/{roleKey}", AuthzActions.SystemAdmin)]
    [InlineData("POST", "evidence-collectors/{id}/credentials", AuthzActions.SystemAdmin)]
    [InlineData("DELETE", "evidence-collectors/{id}/credentials/{credId}", AuthzActions.SystemAdmin)]
    public void MutatingRouteCarriesPermissionAndAlwaysEnforce(string method, string patternSuffix, string action)
    {
        var endpoint = Endpoints().FirstOrDefault(e =>
            e.RoutePattern.RawText!.EndsWith(patternSuffix, StringComparison.Ordinal)
            && (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) ?? false));

        Assert.NotNull(endpoint);
        var permission = endpoint!.Metadata.GetMetadata<AuthzPermissionMetadata>();
        Assert.NotNull(permission);
        Assert.Equal(action, permission!.Action);
        Assert.True(permission.AlwaysEnforce, $"{method} {patternSuffix} must force-enforce.");
    }

    [Theory]
    [InlineData("GET", "auth/sessions/{id}")]
    [InlineData("DELETE", "auth/sessions/{id}")]
    [InlineData("GET", "users/{id}/sessions")]
    [InlineData("DELETE", "users/{id}/sessions")]
    public void CrossUserSessionRouteCarriesUserManageMetadata(string method, string patternSuffix)
    {
        var endpoint = Endpoints().FirstOrDefault(e =>
            e.RoutePattern.RawText!.EndsWith(patternSuffix, StringComparison.Ordinal)
            && (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) ?? false));

        Assert.NotNull(endpoint);
        var permission = endpoint!.Metadata.GetMetadata<AuthzPermissionMetadata>();
        Assert.NotNull(permission);
        Assert.Equal(AuthzActions.UserManage, permission!.Action);
        Assert.True(permission.AlwaysEnforce);
    }

    [Fact]
    public void EveryMutatingGatedRouteForceEnforces()
    {
        // A gated mutating route wired alwaysEnforce:false would be silently mode-relaxed in
        // Observe/Compat; assert none exists.
        var offenders = Endpoints()
            .Where(IsMutating)
            .Where(e => e.Metadata.GetMetadata<AuthzPermissionMetadata>() is { AlwaysEnforce: false })
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        Assert.True(offenders.Count == 0, "Mode-relaxable mutating routes: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Universal guard: EVERY mutating minimal-API route under the <c>/api/v1/freeboard</c> surface must
    /// EITHER carry permission + <c>alwaysEnforce:true</c> metadata OR be on the small allowlist of
    /// legitimately-ungated self-service/setup routes (login, logout, password, account, MFA, sudo,
    /// first-admin setup). A new mutating route added with no filter and not on the allowlist fails
    /// here, so a forgotten gate is caught rather than shipping open. The dual-purpose session routes
    /// are NOT allowlisted; they pass only via their declared cross-user permission metadata.
    /// </summary>
    [Fact]
    public void EveryMutatingApiRouteIsGatedOrAllowlisted()
    {
        const string prefix = "api/v1/freeboard/";

        // Self-service, step-up, and first-admin setup routes are gated by session/sudo state, not by a
        // permission. Kept deliberately small and family-scoped.
        static bool Allowlisted(string path) =>
            path is "auth/login" or "auth/logout" or "account/password" or "setup"
            || path.StartsWith("auth/password/", StringComparison.Ordinal)
            || path.StartsWith("auth/mfa/", StringComparison.Ordinal)
            || path.StartsWith("auth/sudo", StringComparison.Ordinal);

        var offenders = new List<string>();
        foreach (var endpoint in Endpoints().Where(IsMutating))
        {
            var raw = endpoint.RoutePattern.RawText ?? string.Empty;
            var idx = raw.IndexOf(prefix, StringComparison.Ordinal);
            if (idx < 0)
            {
                continue; // not an API-prefixed minimal-API route (e.g. a Razor page handler).
            }

            var path = raw[(idx + prefix.Length)..];
            var forceEnforced = endpoint.Metadata.GetMetadata<AuthzPermissionMetadata>() is { AlwaysEnforce: true };
            if (!forceEnforced && !Allowlisted(path) && !IsIngestGated(endpoint))
            {
                offenders.Add(path);
            }
        }

        Assert.True(offenders.Count == 0, "Ungated mutating API routes: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The ingest route is gated by the collector scheme + named ingest policy, not by a permission
    /// filter, so it passes the universal guard by carrying the <see cref="Freeboard.Api.IngestEndpoint"/>
    /// marker AND being bound to the ingest policy (an IAuthorizeData whose Policy is the ingest policy
    /// name). A route missing either half is NOT recognised as gated - so an accidentally-ungated ingest
    /// route still fails the build.
    /// </summary>
    private static bool IsIngestGated(RouteEndpoint endpoint)
    {
        var hasMarker = endpoint.Metadata.GetMetadata<Freeboard.Api.IngestEndpoint>() is not null;
        var boundToPolicy = endpoint.Metadata.OfType<IAuthorizeData>()
            .Any(a => string.Equals(
                a.Policy, CollectorBearerAuthenticationHandler.IngestPolicyName, StringComparison.Ordinal));
        return hasMarker && boundToPolicy;
    }

    [Fact]
    public async Task EvidenceIngestRouteCarriesMarkerAndBindsCollectorScheme()
    {
        var factory = new AuthWebFactory();
        _ = factory.CreateClient();

        var ingest = Endpoints().FirstOrDefault(e =>
            (e.RoutePattern.RawText?.EndsWith("evidence", StringComparison.Ordinal) ?? false)
            && (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("POST") ?? false));
        Assert.NotNull(ingest);

        // The marker (read by the read-only middleware and by the universal guard above).
        Assert.NotNull(ingest!.Metadata.GetMetadata<Freeboard.Api.IngestEndpoint>());

        // The route is bound to the ingest policy, and that policy binds the collector scheme - so the
        // route cannot silently fall back to the session scheme or drop its gate.
        Assert.Contains(ingest.Metadata.OfType<IAuthorizeData>(), a => string.Equals(
            a.Policy, CollectorBearerAuthenticationHandler.IngestPolicyName, StringComparison.Ordinal));

        var provider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await provider.GetPolicyAsync(CollectorBearerAuthenticationHandler.IngestPolicyName);
        Assert.NotNull(policy);
        Assert.Contains(CollectorBearerAuthenticationHandler.SchemeName, policy!.AuthenticationSchemes);
    }
}
