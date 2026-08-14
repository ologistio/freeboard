using System.Data.Common;
using System.Security.Claims;
using Freeboard.Api;
using Freeboard.Auth;
using Freeboard.Authz;
using Freeboard.Core.Authz;
using Freeboard.Persistence;

namespace Freeboard.Compliance;

/// <summary>
/// App-managed write endpoints for organisations, scope dispositions, and requirement-scope
/// dispositions. Each route is gated by <c>RequirePermission(..., alwaysEnforce: true)</c> so a deny
/// blocks in every rollout mode (the admin gate they replace blocked in every mode too). Because a PUT
/// upsert can MOVE a row across organisations, a PUT on an existing scope/requirement-scope also
/// authorizes the STORED owning org in-handler; an organisation reparent additionally authorizes the
/// new parent. Every resource carrying an organisation id is built through
/// <see cref="AuthzRequestCache"/>, so a gate is anchored on organisation
/// ancestry alone and a grant beyond a non-organisation link is refused here rather than reaching the
/// store to be answered as a 404 or a no-op. Deliberately NOT marked as auth endpoints, so the GitOps
/// read-only middleware 409s them in read-only mode.
/// </summary>
public static class ComplianceWriteEndpoints
{
    // MySQL raises SQLSTATE 23000 for integrity-constraint violations (duplicate/unique key).
    private const string IntegrityConstraintSqlState = "23000";

    // The DELETE authz selector resolves and authorizes the row's current owning organisation, then stashes
    // it here so the handler passes that exact owner to the store delete. Reusing the authorized value (not
    // re-reading) is what closes the cross-org-move TOCTOU: the store deletes only a row still owned by the
    // org the caller was authorized against.
    private const string AuthorizedOwnerItemKey = "compliance.authorizedOwner";

    public sealed record OrganisationInput(string Id, string Title, string Kind, string? Parent);

    public sealed record ScopeInput(string Id, string Title, string Subject, string Standard, string Disposition, string? Justification);

    public sealed record RequirementScopeInput(string Id, string Title, string Subject, string Requirement, string Disposition, string? Justification);

    public static void MapComplianceWriteEndpoints(this WebApplication app)
    {
        // RequireAuthorization keeps the 401 for an anonymous caller (the read-only middleware still
        // 409s these in read-only mode, since they are not marked auth endpoints); the per-route
        // RequirePermission filter then adds the org-scoped permission gate (403).
        var writes = app.MapGroup(ApiRoutes.ApiRoutePrefix).RequireAuthorization();

        writes.MapPut("/organisations/{id}", UpsertOrganisationAsync)
            .RequirePermission(AuthzActions.OrgWrite, OrganisationPutSelector, alwaysEnforce: true);

        writes.MapDelete("/organisations/{id}",
                (string id, IComplianceWriteStore store, CancellationToken ct) =>
                    RunAsync(() => store.DeleteOrganisationAsync(id, ct)))
            .RequirePermission(AuthzActions.OrgWrite, RouteOrgSelector("organisation"), alwaysEnforce: true);

        writes.MapPut("/scopes/{id}", UpsertScopeAsync)
            .RequirePermission(AuthzActions.ComplianceScopeWrite, BodyOrgSelector<ScopeInput>("scope", i => i.Subject), alwaysEnforce: true);

        writes.MapDelete("/scopes/{id}",
                (string id, HttpContext http, IComplianceWriteStore store, CancellationToken ct) =>
                    RunAsync(() => store.DeleteScopeAsync(id, AuthorizedOwner(http), ct)))
            .RequirePermission(AuthzActions.ComplianceScopeWrite, StoredScopeOrgSelector, alwaysEnforce: true);

        writes.MapPut("/requirement-scopes/{id}", UpsertRequirementScopeAsync)
            .RequirePermission(AuthzActions.ComplianceRequirementScopeWrite, BodyOrgSelector<RequirementScopeInput>("requirement_scope", i => i.Subject), alwaysEnforce: true);

        writes.MapDelete("/requirement-scopes/{id}",
                (string id, HttpContext http, IComplianceWriteStore store, CancellationToken ct) =>
                    RunAsync(() => store.DeleteRequirementScopeAsync(id, AuthorizedOwner(http), ct)))
            .RequirePermission(AuthzActions.ComplianceRequirementScopeWrite, StoredRequirementScopeOrgSelector, alwaysEnforce: true);
    }

    private static async Task<IResult> UpsertOrganisationAsync(
        string id, OrganisationInput input, IComplianceWriteStore store,
        IAuthorizer authorizer, AuthzRequestCache cache, IAuthzFactProvider facts, IAuthzAdministrationStore authzAdmin,
        ClaimsPrincipal user, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        // Organisation-typed only: an asset of another type is not an organisation to update, and
        // treating one as an existing row here would turn a create into an update and lower the bar
        // from system.admin to org.write on that asset's parent. Read through the cache: the gate that
        // just ran resolved this request's ancestry from the same snapshot.
        var existing = (await cache.GetAssetsAsync(ct))
            .FirstOrDefault(a => a.IsOrganisation && string.Equals(a.Id, id, StringComparison.Ordinal));

        // Reparenting an existing org is a structural change to BOTH the current parent's and the new
        // parent's child set, so it authorizes org.write on the current (stored) parent AND the new
        // parent. A null parent means root: creating/moving to root - and detaching a root - requires
        // system.admin, so the check must run even when input.Parent is null (do not skip it). The
        // filter already authorized the base org.write on the org itself (for a plain update by an
        // owner of the org or an ancestor); these two checks add the parent-side authorization a
        // reparent needs so an owner of a child cannot detach it or promote it to a root.
        if (existing is not null
            && !string.Equals(existing.Parent, input.Parent, StringComparison.Ordinal)
            && (!await AuthorizeParentAsync(authorizer, cache, user, existing.Parent, ct)
                || !await AuthorizeParentAsync(authorizer, cache, user, input.Parent, ct)))
        {
            return Forbidden();
        }

        // Pass the current parent the reparent authorization bound to so the write locks the org row and
        // rejects a concurrent reparent (the current parent is re-checked under the write lock, closing
        // the TOCTOU). expectExisting distinguishes an update from a create, since a null parent is a root.
        var result = await RunAsync(() => store.UpsertOrganisationAsync(
            id, input.Title, input.Kind, input.Parent, existing is not null, existing?.Parent, ct));

        // On a successful CREATE, grant the creator org-owner on the new org unless it is a super-admin.
        if (existing is null && result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status204NoContent })
        {
            await GrantCreatorOwnerAsync(facts, authzAdmin, user, id, loggerFactory, ct);
        }

        return result;
    }

    private static async Task<IResult> UpsertScopeAsync(
        string id, ScopeInput input, IComplianceWriteStore store,
        IAuthorizer authorizer, AuthzRequestCache cache, ClaimsPrincipal user, CancellationToken ct)
    {
        // Filter the stored-owner lookup to this route's target column AND a Company/Department subject: a
        // same-id row of a different target kind, or a Vendor/Machine subject row (GitOps-write-only),
        // yields no stored owner, so the handler takes the new/absent-row path and the store's global-id
        // branch resolves it to 404 - never a 403 against, or a silent authorization from, that row's org.
        var stored = await StoredOrgSubjectAsync(cache, id, s => s.Standard is not null, ct);
        if (stored is not null
            && !string.Equals(stored.OrganisationId, input.Subject, StringComparison.Ordinal)
            && !await AuthorizeOrgAsync(
                authorizer, cache, user, AuthzActions.ComplianceScopeWrite, stored.OrganisationId, ct,
                stored.Snapshot))
        {
            // The row currently belongs to an org the caller cannot write; moving it is a cross-org move.
            return Forbidden();
        }

        // Pass the authorized stored owner so the write locks the row and rejects a concurrent cross-org
        // move (the current owner is re-checked under the write lock, closing the TOCTOU).
        return await RunAsync(() => store.UpsertScopeDispositionAsync(
            id, input.Title, input.Subject, input.Standard, input.Disposition, input.Justification,
            stored?.OrganisationId, ct));
    }

    private static async Task<IResult> UpsertRequirementScopeAsync(
        string id, RequirementScopeInput input, IComplianceWriteStore store,
        IAuthorizer authorizer, AuthzRequestCache cache, ClaimsPrincipal user, CancellationToken ct)
    {
        var stored = await StoredOrgSubjectAsync(cache, id, s => s.Requirement is not null, ct);
        if (stored is not null
            && !string.Equals(stored.OrganisationId, input.Subject, StringComparison.Ordinal)
            && !await AuthorizeOrgAsync(
                authorizer, cache, user, AuthzActions.ComplianceRequirementScopeWrite, stored.OrganisationId, ct,
                stored.Snapshot))
        {
            return Forbidden();
        }

        return await RunAsync(() => store.UpsertRequirementScopeDispositionAsync(
            id, input.Title, input.Subject, input.Requirement, input.Disposition, input.Justification,
            stored?.OrganisationId, ct));
    }

    #region selectors
    private static async ValueTask<AuthzResource?> OrganisationPutSelector(EndpointFilterInvocationContext context)
    {
        var id = (string)context.HttpContext.Request.RouteValues["id"]!;
        var input = context.Arguments.OfType<OrganisationInput>().First();
        var ct = context.HttpContext.RequestAborted;
        var cache = Cache(context);

        // Organisation-typed only: fed an unfiltered asset list, a PUT on a machine id would flip from
        // create to update and authorize org.write on that id instead of the create-root system.admin.
        var exists = (await cache.GetAssetsAsync(ct))
            .Any(a => a.IsOrganisation && string.Equals(a.Id, id, StringComparison.Ordinal));

        // Update: authorize the org itself (its ancestry includes the current parent). Create-child:
        // authorize the parent. Create-root: authorize the new id, on which no grant exists, so only a
        // super-admin passes.
        var orgForAuth = exists ? id : input.Parent ?? id;
        return await cache.OrganisationResourceAsync("organisation", id, orgForAuth, ct);
    }

    private static AuthzResourceSelector RouteOrgSelector(string type)
        => async context =>
        {
            var id = (string)context.HttpContext.Request.RouteValues["id"]!;
            return await Cache(context).OrganisationResourceAsync(type, id, id, context.HttpContext.RequestAborted);
        };

    private static AuthzResourceSelector BodyOrgSelector<TInput>(string type, Func<TInput, string> orgOf)
        => async context =>
        {
            var input = context.Arguments.OfType<TInput>().First();
            return await Cache(context)
                .OrganisationResourceAsync(type, null, orgOf(input), context.HttpContext.RequestAborted);
        };

    // Both DELETE authz selectors read the one scope list filtered to their route's own target column AND a
    // subject that resolves to a Company/Department asset, so a wrong-kind id or a Vendor/Machine subject
    // row (GitOps-write-only) yields no stored owner: the DELETE is then not authorized against another
    // kind's or subject's owning org (null selector -> 404), and the store delete resolves the same row to
    // 404.
    private static async ValueTask<AuthzResource?> StoredScopeOrgSelector(EndpointFilterInvocationContext context)
        => await StoredScopeSelectorAsync(context, "scope", s => s.Standard is not null);

    private static async ValueTask<AuthzResource?> StoredRequirementScopeOrgSelector(EndpointFilterInvocationContext context)
        => await StoredScopeSelectorAsync(context, "requirement_scope", s => s.Requirement is not null);

    private static async ValueTask<AuthzResource?> StoredScopeSelectorAsync(
        EndpointFilterInvocationContext context, string type, Func<ScopeRow, bool> targetKind)
    {
        var id = (string)context.HttpContext.Request.RouteValues["id"]!;
        var ct = context.HttpContext.RequestAborted;
        var cache = Cache(context);
        var stored = await StoredOrgSubjectAsync(cache, id, targetKind, ct);
        if (stored is null)
        {
            return null;
        }

        context.HttpContext.Items[AuthorizedOwnerItemKey] = stored.OrganisationId;
        return cache.OrganisationResource(type, id, stored.OrganisationId, stored.Snapshot);
    }

    private static AuthzRequestCache Cache(EndpointFilterInvocationContext context)
        => context.HttpContext.RequestServices.GetRequiredService<AuthzRequestCache>();

    // The owning organisation the DELETE selector authorized, stashed for the handler. The selector always
    // sets it before the handler runs (a null-owner row 404s in the selector), so a missing item is a broken
    // authz handoff, not a valid delete. Fail closed: throw rather than hand the store a null owner, since
    // the store's owner match is the authz binding and an unset owner would delete a row the caller was never
    // authorized against.
    private static string AuthorizedOwner(HttpContext http) =>
        http.Items.TryGetValue(AuthorizedOwnerItemKey, out var owner) && owner is string org
            ? org
            : throw new InvalidOperationException(
                "DELETE handler reached without an authorized owner; the authz selector must stash it.");

    /// <summary>
    /// The organisation a stored scope row belongs to, and the snapshot it was found in. The caller gates
    /// on that same snapshot, so the row and the ancestry the gate walks come from one repeatable-read
    /// state of the domain.
    /// </summary>
    private sealed record StoredOrgSubject(string OrganisationId, ComplianceSnapshot Snapshot);

    /// <summary>
    /// The stored owning organisation of a scope row: the row of this id and target kind whose subject
    /// resolves to an organisation ASSET. App-managed scope writes target Company/Department subjects
    /// only; a Vendor/Machine or unresolved subject is GitOps-write-only, so such a row yields no owner
    /// and the caller takes its new/absent-row path.
    ///
    /// The scopes and the assets are read together. Resolving the subject against a separately read asset
    /// list would let an import commit between the two and pair the stored row with owner edges that never
    /// held with it. This is the one gate whose organisation is knowable only from a stored row, so it is
    /// also the one that reads a payload table.
    /// </summary>
    private static async Task<StoredOrgSubject?> StoredOrgSubjectAsync(
        AuthzRequestCache cache, string id, Func<ScopeRow, bool> targetKind, CancellationToken ct)
    {
        var snapshot = await cache.GetSnapshotAsync(
            ComplianceReadSet.Assets | ComplianceReadSet.Scopes, ct);

        var subject = snapshot.Scopes
            .FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal) && targetKind(s))?.Subject;
        if (subject is null)
        {
            return null;
        }

        var asset = snapshot.Assets
            .FirstOrDefault(a => string.Equals(a.Id, subject, StringComparison.Ordinal));
        return asset is not null && asset.IsOrganisation ? new StoredOrgSubject(subject, snapshot) : null;
    }

    #endregion

    #region helpers
    /// <summary>
    /// Authorizes <paramref name="action"/> on <paramref name="orgId"/>. <paramref name="snapshot"/> is the
    /// snapshot the organisation was derived from, when it came from a stored row: the ancestry is then
    /// walked over that snapshot's assets rather than over a separately read list. A caller whose
    /// organisation comes from the route or the body passes none and anchors on the request's shared
    /// assets-only read.
    /// </summary>
    private static async Task<bool> AuthorizeOrgAsync(
        IAuthorizer authorizer, AuthzRequestCache cache, ClaimsPrincipal user, string action, string orgId,
        CancellationToken ct, ComplianceSnapshot? snapshot = null)
    {
        var resource = snapshot is null
            ? await cache.OrganisationResourceAsync("organisation", orgId, orgId, ct)
            : cache.OrganisationResource("organisation", orgId, orgId, snapshot);
        var decision = await authorizer.AuthorizeAsync(user, action, resource, alwaysEnforce: true, ct);
        return decision.IsPermitted;
    }

    /// <summary>
    /// Authorizes the parent side of an organisation structural change: a null parent is the root, which
    /// requires <c>system.admin</c>; a non-null parent requires <c>org.write</c> on that parent.
    /// </summary>
    private static Task<bool> AuthorizeParentAsync(
        IAuthorizer authorizer, AuthzRequestCache cache, ClaimsPrincipal user, string? parent, CancellationToken ct)
        => parent is null
            ? AuthorizeSystemAdminAsync(authorizer, user, ct)
            : AuthorizeOrgAsync(authorizer, cache, user, AuthzActions.OrgWrite, parent, ct);

    private static async Task<bool> AuthorizeSystemAdminAsync(
        IAuthorizer authorizer, ClaimsPrincipal user, CancellationToken ct)
    {
        var decision = await authorizer.AuthorizeAsync(
            user, AuthzActions.SystemAdmin, new AuthzResource("system", null, null, []), alwaysEnforce: true, ct);
        return decision.IsPermitted;
    }

    private static async Task GrantCreatorOwnerAsync(
        IAuthzFactProvider facts, IAuthzAdministrationStore authzAdmin, ClaimsPrincipal user, string orgId,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var userId = user.FindFirst(AuthClaims.UserId)?.Value;
        if (userId is null)
        {
            return;
        }

        var loaded = await facts.LoadFactsAsync(userId, ct);
        if (loaded.SystemPermissions.Contains(AuthzActions.SystemAdmin))
        {
            return; // super-admin already reaches everything; no per-org grant needed.
        }

        await authzAdmin.AssignOrganisationRoleAsync(userId, AuthzRoles.OrgOwner, orgId, ct);
        await AuthzMutationAudit.AppendAsync(
            authzAdmin, loggerFactory.CreateLogger(AuthzMutationAudit.LoggerCategory),
            new AuthzAuditEvent(
                "authz.assignment.write", userId, AuthzActions.AuthzAssignmentWrite, "organisation", userId, orgId,
                "Permit", "org create grants creator org-owner"),
            ct);
    }

    private static async Task<IResult> RunAsync(Func<Task<WriteResult>> write)
    {
        try
        {
            var result = await write();
            if (result.Ok)
            {
                return Results.NoContent();
            }

            if (result.IsNotFound)
            {
                return Results.NotFound();
            }

            return result.IsConflict ? Conflict() : Invalid(result.Error!);
        }
        catch (DbException ex) when (ex.SqlState == IntegrityConstraintSqlState)
        {
            return Conflict();
        }
        catch (Exception ex) when (ComplianceEndpoints.IsStoreFailure(ex))
        {
            return Unreachable();
        }
    }

    private static IResult Invalid(string detail) => Results.Problem(
        title: "Invalid compliance write",
        detail: detail,
        statusCode: StatusCodes.Status422UnprocessableEntity,
        type: "https://freeboard.dev/problems/validation");

    private static IResult Conflict() => Results.Problem(
        title: "Conflicting compliance write",
        detail: "The write conflicts with an existing record.",
        statusCode: StatusCodes.Status409Conflict,
        type: "https://freeboard.dev/problems/conflict");

    private static IResult Forbidden() => Results.Problem(
        title: "Forbidden",
        detail: "You do not have permission to perform this action.",
        statusCode: StatusCodes.Status403Forbidden,
        type: "https://freeboard.dev/problems/forbidden");

    private static IResult Unreachable() => Results.Problem(
        title: "Compliance store unreachable",
        detail: "The compliance store could not be reached. Check the database connection.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    #endregion
}
