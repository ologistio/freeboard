using System.Security.Claims;
using Freeboard.Core.Authz;
using Freeboard.Persistence;
using Freeboard.Auth;
using Freeboard.Persistence.Auth;

namespace Freeboard.Authz;

/// <summary>
/// The app-facing decision seam. Builds the principal, loads facts once per request, resolves the
/// resource org's inclusive ancestry via the shared parent-walk, evaluates the pure engine, applies
/// the rollout mode, and audits. Fails closed: any exception resolves to <c>Deny</c>.
/// </summary>
public interface IAuthorizer
{
    /// <summary>
    /// Mode-aware decision. When <paramref name="alwaysEnforce"/> is false (a read), a mode-relaxed
    /// deny may be allowed through (Observe) or served by the zero-grant Compat fallback; when true (a
    /// mutating route or a privileged cross-user gate) a deny blocks in EVERY mode.
    /// </summary>
    ValueTask<AuthzDecision> AuthorizeAsync(
        ClaimsPrincipal user, string action, AuthzResource resource, bool alwaysEnforce,
        CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IAuthorizer"/>. Scoped: it holds per-request principal state via the cache.</summary>
public sealed class Authorizer(
    AuthzRequestCache cache,
    IAuthorizationEngine engine,
    AuthzRuntimeOptions options,
    IAuthzAdministrationStore auditStore,
    ILogger<Authorizer> logger) : IAuthorizer
{
    internal const string DeniedEventType = "authz.decision.denied";
    internal const string CompatReadEventType = "authz.compat.read";

    public async ValueTask<AuthzDecision> AuthorizeAsync(
        ClaimsPrincipal user, string action, AuthzResource resource, bool alwaysEnforce,
        CancellationToken cancellationToken = default)
    {
        AuthzPrincipal principal;
        AuthzDecision engineDecision;
        try
        {
            principal = await BuildPrincipalAsync(user, cancellationToken).ConfigureAwait(false);
            var resolved = await ResolveAncestryAsync(resource, cancellationToken).ConfigureAwait(false);
            engineDecision = engine.Evaluate(new AuthzRequest(principal, action, resolved));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed. The store/engine failure is the reliable log channel; do NOT attempt a
            // persistent audit (its dependency may be the very store that just failed).
            logger.LogWarning(ex, "Authz decision failed closed for action {Action}; denying.", action);
            return AuthzDecision.Deny("fail-closed: authorizer error");
        }

        if (engineDecision.IsPermitted)
        {
            logger.LogDebug("Authz permit: {Action} for {User} ({Reason}).",
                action, principal.UserId, engineDecision.Reason);
            return engineDecision;
        }

        // Engine denied. Mutating/always-enforce routes block in every mode.
        if (alwaysEnforce)
        {
            await AuditDeniedAsync(principal, action, resource, engineDecision, cancellationToken).ConfigureAwait(false);
            return engineDecision;
        }

        // Read path: the mode relaxation governs reads only.
        switch (options.Mode)
        {
            case AuthzMode.Observe:
                // Do not block; audit what Enforce would decide.
                await AuditDeniedAsync(principal, action, resource, engineDecision, cancellationToken).ConfigureAwait(false);
                return AuthzDecision.Permit("observe: would-be deny not enforced");

            case AuthzMode.Compat when IsZeroGrant(principal):
                // Zero-grant legacy bridge: full read, but every use is recorded (the Enforce flip closes it).
                await AuditAsync(CompatReadEventType, principal, action, resource, "Permit",
                    "compat zero-grant read fallback", cancellationToken).ConfigureAwait(false);
                return AuthzDecision.Permit("compat: zero-grant read fallback");

            default:
                await AuditDeniedAsync(principal, action, resource, engineDecision, cancellationToken).ConfigureAwait(false);
                return engineDecision;
        }
    }

    private async ValueTask<AuthzPrincipal> BuildPrincipalAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var userId = user.FindFirst(AuthClaims.UserId)?.Value;
        var facts = userId is not null && (user.Identity?.IsAuthenticated ?? false)
            ? await cache.LoadFactsAsync(userId, ct).ConfigureAwait(false)
            : AuthzPrincipalFacts.None;
        return ClaimsPrincipalAuthzPrincipalFactory.Build(user, facts);
    }

    private async ValueTask<AuthzResource> ResolveAncestryAsync(AuthzResource resource, CancellationToken ct)
    {
        if (resource.OrganisationId is null || resource.OrgAncestryInclusive.Count > 0)
        {
            return resource;
        }

        var organisations = await cache.GetOrganisationsAsync(ct).ConfigureAwait(false);
        var byId = organisations.ToDictionary(o => o.Id, StringComparer.Ordinal);
        var ancestry = Compliance.OrgAncestry.InclusiveAncestors(resource.OrganisationId, byId);
        return resource with { OrgAncestryInclusive = ancestry };
    }

    private static bool IsZeroGrant(AuthzPrincipal principal)
        => principal.SystemPermissions.Count == 0 && principal.OrgGrants.Count == 0;

    private ValueTask AuditDeniedAsync(
        AuthzPrincipal principal, string action, AuthzResource resource, AuthzDecision decision, CancellationToken ct)
        => AuditAsync(DeniedEventType, principal, action, resource, "Deny", decision.Reason, ct);

    private async ValueTask AuditAsync(
        string eventType, AuthzPrincipal principal, string action, AuthzResource resource,
        string effect, string reason, CancellationToken ct)
    {
        // ILogger ALWAYS logs (the reliable channel). The persistent write is BEST-EFFORT: on failure
        // it is skipped-and-logged and never turns a request into an error.
        var level = effect == "Deny" ? LogLevel.Warning : LogLevel.Information;
        logger.Log(level, "Authz {EventType}: {Action} on {ResourceType}/{ResourceId} (org {Org}) by {Actor} -> {Effect} ({Reason}).",
            eventType, action, resource.Type, resource.Id, resource.OrganisationId, principal.UserId, effect, reason);

        try
        {
            await auditStore.AppendAuditEventAsync(
                new AuthzAuditEvent(eventType, principal.UserId, action, resource.Type, resource.Id,
                    resource.OrganisationId, effect, reason),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Persisting the authz audit row failed; skipping (the decision itself is logged).");
        }
    }
}
