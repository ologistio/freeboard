using System.Security.Claims;
using System.Text.Json.Serialization;
using Freeboard.Api;
using Freeboard.Auth;
using Freeboard.Core.Authz;
using Freeboard.Persistence;

namespace Freeboard.Authz;

/// <summary>
/// Role-assignment management: org-scoped grants (behind <c>authz.assignment.write</c>) and system
/// <c>super-admin</c> grants (behind <c>system.admin</c>), all force-enforced. The last-super-admin
/// and last-direct-owner guards are enforced atomically in the write store. 403-vs-404: a caller who
/// cannot see the organisation at all gets 404 (existence non-disclosure); one who can see it but
/// lacks the assignment permission gets 403.
/// </summary>
public static class RoleAssignmentEndpoints
{
    public sealed record OrgGrantRequest(
        [property: JsonPropertyName("user_id")] string? UserId,
        [property: JsonPropertyName("role_key")] string? RoleKey);

    public sealed record SystemGrantRequest([property: JsonPropertyName("user_id")] string? UserId);

    public static void MapRoleAssignmentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(ApiRoutes.ApiRoutePrefix).RequireAuthorization();

        group.MapGet("/organisations/{orgId}/role-assignments", ListOrgAssignmentsAsync)
            .RequirePermission(AuthzActions.AuthzAssignmentWrite, OrgVisibilitySelector, alwaysEnforce: true);
        group.MapPut("/organisations/{orgId}/role-assignments", GrantOrgRoleAsync)
            .RequirePermission(AuthzActions.AuthzAssignmentWrite, OrgVisibilitySelector, alwaysEnforce: true);
        group.MapDelete("/organisations/{orgId}/role-assignments/{userId}/{roleKey}", RevokeOrgRoleAsync)
            .RequirePermission(AuthzActions.AuthzAssignmentWrite, OrgVisibilitySelector, alwaysEnforce: true);

        group.MapGet("/system-role-assignments", ListSystemAssignmentsAsync)
            .RequirePermission(AuthzActions.SystemAdmin, AuthzSelectors.System, alwaysEnforce: true);
        group.MapPut("/system-role-assignments", GrantSystemRoleAsync)
            .RequirePermission(AuthzActions.SystemAdmin, AuthzSelectors.System, alwaysEnforce: true);
        group.MapDelete("/system-role-assignments/{userId}", RevokeSystemRoleAsync)
            .RequirePermission(AuthzActions.SystemAdmin, AuthzSelectors.System, alwaysEnforce: true);
    }

    private static async Task<IResult> ListOrgAssignmentsAsync(string orgId, IAuthzStore store, CancellationToken ct)
    {
        var rows = await store.ListOrganisationAssignmentsAsync(orgId, ct);
        return Results.Ok(rows.Select(r => new
        {
            user_id = r.UserId, role_key = r.RoleKey, organisation_id = r.OrganisationId, created_at = r.CreatedAt,
        }));
    }

    private static async Task<IResult> GrantOrgRoleAsync(
        string orgId, OrgGrantRequest body, HttpContext ctx, IAuthzAdministrationStore admin, CancellationToken ct)
    {
        body ??= new OrgGrantRequest(null, null);
        if (string.IsNullOrWhiteSpace(body.UserId) || string.IsNullOrWhiteSpace(body.RoleKey))
        {
            return ValidationProblem("user_id and role_key are required.");
        }

        var result = await admin.AssignOrganisationRoleAsync(body.UserId, body.RoleKey, orgId, ct);
        if (result.IsOk)
        {
            await AuditAsync(admin, ctx, "authz.assignment.write", body.UserId, orgId, ct);
        }

        return MapAssign(result, StatusCodes.Status201Created);
    }

    private static async Task<IResult> RevokeOrgRoleAsync(
        string orgId, string userId, string roleKey, HttpContext ctx, IAuthzAdministrationStore admin, CancellationToken ct)
    {
        var actor = ctx.User.FindFirst(AuthClaims.UserId)?.Value ?? string.Empty;
        var result = await admin.RevokeOrganisationRoleAsync(userId, roleKey, orgId, actor, ct);
        if (result.IsOk)
        {
            await AuditAsync(admin, ctx, "authz.assignment.revoke", userId, orgId, ct);
        }

        return MapRevoke(result);
    }

    private static async Task<IResult> ListSystemAssignmentsAsync(IAuthzStore store, CancellationToken ct)
    {
        var rows = await store.ListSystemAssignmentsAsync(ct);
        return Results.Ok(rows.Select(r => new { user_id = r.UserId, role_key = r.RoleKey, created_at = r.CreatedAt }));
    }

    private static async Task<IResult> GrantSystemRoleAsync(
        SystemGrantRequest body, HttpContext ctx, IAuthzAdministrationStore admin, CancellationToken ct)
    {
        body ??= new SystemGrantRequest(null);
        if (string.IsNullOrWhiteSpace(body.UserId))
        {
            return ValidationProblem("user_id is required.");
        }

        var result = await admin.AssignSystemRoleAsync(body.UserId, AuthzRoles.SuperAdmin, ct);
        if (result.IsOk)
        {
            await AuditAsync(admin, ctx, "authz.system-assignment.write", body.UserId, null, ct);
        }

        return MapAssign(result, StatusCodes.Status201Created);
    }

    private static async Task<IResult> RevokeSystemRoleAsync(
        string userId, HttpContext ctx, IAuthzAdministrationStore admin, CancellationToken ct)
    {
        var result = await admin.RevokeSystemRoleAsync(userId, AuthzRoles.SuperAdmin, ct);
        if (result.IsOk)
        {
            await AuditAsync(admin, ctx, "authz.system-assignment.revoke", userId, null, ct);
        }

        return MapRevoke(result);
    }

    // ---- selectors ----

    private static async ValueTask<AuthzResource?> OrgVisibilitySelector(EndpointFilterInvocationContext context)
    {
        var orgId = (string)context.HttpContext.Request.RouteValues["orgId"]!;
        var resource = new AuthzResource("organisation", orgId, orgId, []);

        // Existence non-disclosure: if the caller cannot READ the org (mode-aware, so a Compat
        // zero-grant caller who sees everything still gets 403 not 404), hide it as a 404.
        var authorizer = context.HttpContext.RequestServices.GetRequiredService<IAuthorizer>();
        var canRead = await authorizer.AuthorizeAsync(
            context.HttpContext.User, AuthzActions.OrgRead, resource, alwaysEnforce: false, context.HttpContext.RequestAborted);
        return canRead.IsPermitted ? resource : null;
    }

    // ---- mapping + audit ----

    private static IResult MapAssign(AuthzWriteResult result, int createdStatus) => result.Status switch
    {
        AuthzWriteStatus.Ok => Results.StatusCode(createdStatus),
        AuthzWriteStatus.Conflict => Conflict(result.Error!),
        AuthzWriteStatus.Invalid => ValidationProblem(result.Error!),
        _ => Results.NotFound(),
    };

    private static IResult MapRevoke(AuthzWriteResult result) => result.Status switch
    {
        AuthzWriteStatus.Ok => Results.NoContent(),
        AuthzWriteStatus.NotFound => Results.NotFound(),
        AuthzWriteStatus.Conflict => Conflict(result.Error!),
        _ => ValidationProblem(result.Error ?? "Invalid request."),
    };

    private static IResult ValidationProblem(string detail) => Results.Problem(
        title: "Invalid role assignment", detail: detail, statusCode: StatusCodes.Status422UnprocessableEntity,
        type: "https://freeboard.io/problems/validation");

    private static IResult Conflict(string detail) => Results.Problem(
        title: "Conflicting role assignment", detail: detail, statusCode: StatusCodes.Status409Conflict,
        type: "https://freeboard.io/problems/conflict");

    private static Task AuditAsync(
        IAuthzAdministrationStore admin, HttpContext ctx, string action, string targetUserId, string? orgId, CancellationToken ct)
    {
        var actor = ctx.User.FindFirst(AuthClaims.UserId)?.Value;
        return AuthzMutationAudit.AppendAsync(
            admin, AuthzMutationAudit.Logger(ctx.RequestServices),
            new AuthzAuditEvent(action, actor, AuthzActions.AuthzAssignmentWrite, "user", targetUserId, orgId, "Permit", "role assignment mutation"),
            ct);
    }
}
