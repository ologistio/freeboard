using System.Security.Claims;
using Freeboard.Core.Authz;
using Freeboard.Persistence;
using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Freeboard.Web;

namespace Freeboard.Authz;

/// <summary>
/// The authz-backed <see cref="IOrgAccess"/> default. Resolves the accessible organisation set by the
/// rollout mode: Observe never narrows (full set for every caller); Compat narrows a grant-holder to
/// its read-subtree union and gives a zero-grant caller the audited full read fallback; Enforce is
/// strict (subtree union, empty for a zero-grant caller). A super-admin gets all organisations in
/// every mode.
/// </summary>
public sealed class AuthzOrgAccess(
    AuthzRequestCache cache,
    AuthzRuntimeOptions options,
    IAuthzAdministrationStore auditStore,
    ILogger<AuthzOrgAccess> logger) : IOrgAccess
{
    public async ValueTask<IReadOnlySet<string>> AccessibleOrgIdsAsync(
        ClaimsPrincipal user, IReadOnlyList<OrganisationRow> organisations, CancellationToken cancellationToken = default)
    {
        var all = organisations.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);

        var userId = user.FindFirst(AuthClaims.UserId)?.Value;
        var facts = userId is not null && (user.Identity?.IsAuthenticated ?? false)
            ? await cache.LoadFactsAsync(userId, cancellationToken).ConfigureAwait(false)
            : AuthzPrincipalFacts.None;

        // Super-admin sees everything in every mode.
        if (facts.SystemPermissions.Contains(AuthzActions.SystemAdmin))
        {
            return all;
        }

        // Observe: reads are not narrowed, so behaviour is unchanged while decisions are observed. Log
        // the would-be Enforce narrowing so operators can see what Enforce would restrict before flipping.
        // Log-only (not a persisted row per read) to avoid flooding the audit table - consistent with
        // ordinary reads being ILogger-only.
        if (options.Mode == AuthzMode.Observe)
        {
            LogObserveReadNarrowing(userId, facts, organisations, all);
            return all;
        }

        var zeroGrant = facts.SystemPermissions.Count == 0 && facts.OrgGrants.Count == 0;
        if (options.Mode == AuthzMode.Compat && zeroGrant)
        {
            await AuditCompatFallbackAsync(userId, cancellationToken).ConfigureAwait(false);
            return all;
        }

        // Grant-holder (Compat or Enforce) and a zero-grant Enforce caller: the read-subtree union,
        // which is empty when the caller holds no read grant.
        return ReadSubtreeUnion(facts, organisations);
    }

    private void LogObserveReadNarrowing(
        string? userId, AuthzPrincipalFacts facts, IReadOnlyList<OrganisationRow> organisations, IReadOnlySet<string> all)
    {
        var wouldBe = ReadSubtreeUnion(facts, organisations);
        if (wouldBe.Count < all.Count)
        {
            logger.LogInformation(
                "Authz observe: read for {Actor} would narrow from {Full} to {Restricted} organisations under Enforce.",
                userId, all.Count, wouldBe.Count);
        }
    }

    private static IReadOnlySet<string> ReadSubtreeUnion(
        AuthzPrincipalFacts facts, IReadOnlyList<OrganisationRow> organisations)
    {
        var readRoots = facts.OrgGrants
            .Where(g => string.Equals(g.PermissionKey, AuthzActions.ComplianceRead, StringComparison.Ordinal))
            .Select(g => g.OrganisationId)
            .ToHashSet(StringComparer.Ordinal);

        var accessible = new HashSet<string>(StringComparer.Ordinal);
        if (readRoots.Count == 0)
        {
            return accessible;
        }

        var childrenByParent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var organisation in organisations.Where(o => o.Parent is not null))
        {
            if (!childrenByParent.TryGetValue(organisation.Parent!, out var children))
            {
                children = [];
                childrenByParent[organisation.Parent!] = children;
            }

            children.Add(organisation.Id);
        }

        var stack = new Stack<string>(readRoots);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!accessible.Add(id))
            {
                continue;
            }

            if (childrenByParent.TryGetValue(id, out var children))
            {
                foreach (var child in children)
                {
                    stack.Push(child);
                }
            }
        }

        // A granted root that is not in the supplied list contributes nothing renderable.
        accessible.IntersectWith(organisations.Select(o => o.Id));
        return accessible;
    }

    private async ValueTask AuditCompatFallbackAsync(string? userId, CancellationToken ct)
    {
        logger.LogInformation(
            "Authz {EventType}: zero-grant read fallback for {Actor} under Compat.",
            Authorizer.CompatReadEventType, userId);
        if (userId is null)
        {
            return;
        }

        try
        {
            await auditStore.AppendAuditEventAsync(
                new AuthzAuditEvent(Authorizer.CompatReadEventType, userId, AuthzActions.ComplianceRead,
                    "organisation", null, null, "Permit", "compat zero-grant read fallback"),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Persisting the compat read-fallback audit row failed; skipping.");
        }
    }
}
