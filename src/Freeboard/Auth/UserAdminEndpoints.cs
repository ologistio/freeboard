using System.Text.Json.Serialization;
using Freeboard.Api;
using Freeboard.Authz;
using Freeboard.Core.Authz;
using Freeboard.Persistence;
using Freeboard.Persistence.Auth;

namespace Freeboard.Auth;

/// <summary>
/// Admin user-management endpoints. All under <see cref="ApiRoutes.ApiRoutePrefix"/>, gated by
/// <c>RequirePermission(user.manage, alwaysEnforce: true)</c> (super-admin-only, reachable only via
/// <c>system.admin</c>; the legacy admin claim grants nothing), all tagged with
/// <see cref="AuthEndpoint"/>. Every mutation writes a best-effort audit row. Credential handoff is
/// in-band: create / reset-password return a one-time temp password ONCE.
/// </summary>
public static class UserAdminEndpoints
{
    public static void MapUserAdminEndpoints(this WebApplication app)
    {
        // RequireAuthorization keeps the 401 for an anonymous caller; RequirePermission adds the
        // super-admin gate (403 for an authenticated non-super-admin). user.manage is target-
        // independent, so a single selector over the optional route id gates every route.
        var admin = app.MapGroup(ApiRoutes.ApiRoutePrefix)
            .RequireAuthorization()
            .RequirePermission(AuthzActions.UserManage, UserResourceSelector, alwaysEnforce: true);

        admin.MapPost("/users", CreateUserAsync).MarkAuthEndpoint();
        admin.MapGet("/users", ListUsersAsync).MarkAuthEndpoint();
        admin.MapGet("/users/{id}", GetUserAsync).MarkAuthEndpoint();
        admin.MapPost("/users/{id}/disable", DisableUserAsync).MarkAuthEndpoint();
        admin.MapPost("/users/{id}/enable", EnableUserAsync).MarkAuthEndpoint();
        admin.MapPost("/users/{id}/reset-password", ResetUserPasswordAsync).MarkAuthEndpoint();
    }

    private static ValueTask<AuthzResource?> UserResourceSelector(EndpointFilterInvocationContext context)
    {
        var id = context.HttpContext.Request.RouteValues.TryGetValue("id", out var raw) ? raw as string : null;
        return ValueTask.FromResult<AuthzResource?>(AuthzResource.ForUser(id));
    }

    public sealed record CreateUserRequest(
        string? Email,
        string? Name,
        [property: JsonPropertyName("global_role")] string? GlobalRole);

    private static async Task<IResult> CreateUserAsync(
        CreateUserRequest body,
        HttpContext ctx,
        IUserStore users,
        IPasswordCredentialStore credentials,
        IPasswordHasher hasher,
        IPasswordResetStore resets,
        IAuthzAdministrationStore authzAdmin,
        IServiceProvider sp,
        CancellationToken ct)
    {
        // A missing JSON body binds as null; treat it as an empty request so the field-level
        // validation produces the same 422 instead of dereferencing null.
        body ??= new CreateUserRequest(null, null, null);

        // The JSON API always requests the temp-password handoff, so its contract is unchanged: the
        // invite arms (Invited / InviteSendFailed) are never returned here.
        var result = await AuthFlows.CreateUserAsync(
            body.Email, body.Name, body.GlobalRole, AuthFlows.CreateUserHandoff.TemporaryPassword,
            users, credentials, hasher, resets, sp, ct).ConfigureAwait(false);

        if (result is AuthFlows.CreateUserResult.Success created)
        {
            await AuditUserAdminAsync(authzAdmin, ctx, "user.admin.create", created.User.Id, ct).ConfigureAwait(false);
        }

        return result switch
        {
            AuthFlows.CreateUserResult.Success success => Results.Json(
                new { user = ApiResponses.UserObject(success.User), temporary_password = success.TemporaryPassword },
                statusCode: StatusCodes.Status201Created),
            AuthFlows.CreateUserResult.Invalid invalid => ApiResponses.ValidationProblem(invalid.Errors),
            AuthFlows.CreateUserResult.DuplicateEmail => ApiResponses.ValidationProblem(
                "email", "A user with this email already exists."),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> ListUsersAsync(IUserStore users, CancellationToken ct)
    {
        var rows = await users.ListAsync(ct).ConfigureAwait(false);
        return Results.Ok(rows.Select(ApiResponses.UserObject));
    }

    private static async Task<IResult> GetUserAsync(string id, IUserStore users, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(id, ct).ConfigureAwait(false);
        return user is null ? Results.NotFound() : Results.Ok(ApiResponses.UserObject(user));
    }

    private static async Task<IResult> DisableUserAsync(
        string id, HttpContext ctx, IUserStore users, ISessionStore sessions,
        IAuthzAdministrationStore authzAdmin, CancellationToken ct)
    {
        // The last-usable-super-admin guard runs atomically in the store; the API self-disable gap is
        // closed here too (not only in the admin page).
        var outcome = await users.TryDisableUserAsync(id, ct).ConfigureAwait(false);
        switch (outcome)
        {
            case DisableUserOutcome.NotFound:
                return Results.NotFound();
            case DisableUserOutcome.LastSuperAdmin:
                return Results.Problem(
                    title: "Cannot disable the last super-admin",
                    detail: "At least one usable super-admin must remain.",
                    statusCode: StatusCodes.Status409Conflict,
                    type: "https://freeboard.io/problems/conflict");
            default:
                // Disabling revokes the user's sessions so an in-flight token cannot keep acting.
                await sessions.DeleteAllForUserAsync(id, ct).ConfigureAwait(false);
                await AuditUserAdminAsync(authzAdmin, ctx, "user.admin.disable", id, ct).ConfigureAwait(false);
                return Results.Ok(new { enabled = false });
        }
    }

    private static async Task<IResult> EnableUserAsync(
        string id, HttpContext ctx, IUserStore users, IAuthzAdministrationStore authzAdmin, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (user is null)
        {
            return Results.NotFound();
        }

        await users.SetEnabledAsync(id, true, ct).ConfigureAwait(false);
        await AuditUserAdminAsync(authzAdmin, ctx, "user.admin.enable", id, ct).ConfigureAwait(false);
        return Results.Ok(new { enabled = true });
    }

    private static async Task<IResult> ResetUserPasswordAsync(
        string id,
        HttpContext ctx,
        IUserStore users,
        IPasswordCredentialStore credentials,
        IPasswordHasher hasher,
        IAuthzAdministrationStore authzAdmin,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var result = await AuthFlows.ResetUserPasswordAsync(id, users, credentials, hasher, sp, ct)
            .ConfigureAwait(false);

        if (result is AuthFlows.ResetUserPasswordResult.Success)
        {
            await AuditUserAdminAsync(authzAdmin, ctx, "user.admin.reset", id, ct).ConfigureAwait(false);
        }

        return result switch
        {
            AuthFlows.ResetUserPasswordResult.Success success
                => Results.Ok(new { temporary_password = success.TemporaryPassword }),
            _ => Results.NotFound(),
        };
    }

    private static Task AuditUserAdminAsync(
        IAuthzAdministrationStore authzAdmin, HttpContext ctx, string action, string targetUserId, CancellationToken ct)
    {
        var actor = ctx.User.FindFirst(AuthClaims.UserId)?.Value;
        return AuthzMutationAudit.AppendAsync(
            authzAdmin, AuthzMutationAudit.Logger(ctx.RequestServices),
            new AuthzAuditEvent(action, actor, AuthzActions.UserManage, "user", targetUserId, null, "Permit", "user-admin mutation"),
            ct);
    }
}
