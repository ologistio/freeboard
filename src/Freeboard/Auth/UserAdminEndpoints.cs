using System.Text.Json.Serialization;
using Freeboard.Api;
using Freeboard.Persistence.Auth;

namespace Freeboard.Auth;

/// <summary>
/// Admin user-management endpoints. All under
/// <see cref="ApiRoutes.ApiRoutePrefix"/>, all behind the admin authorization policy
/// (non-admin -> 403), all tagged with <see cref="AuthEndpoint"/>. Credential handoff is
/// in-band: create / reset-password return a one-time temp password ONCE; only its Argon2id
/// hash is stored, never the plaintext, and no email is sent.
/// </summary>
public static class UserAdminEndpoints
{
    public static void MapUserAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup(ApiRoutes.ApiRoutePrefix).RequireAuthorization(GlobalRoles.AdminPolicy);

        admin.MapPost("/users", CreateUserAsync).MarkAuthEndpoint();
        admin.MapGet("/users", ListUsersAsync).MarkAuthEndpoint();
        admin.MapGet("/users/{id}", GetUserAsync).MarkAuthEndpoint();
        admin.MapPost("/users/{id}/disable", DisableUserAsync).MarkAuthEndpoint();
        admin.MapPost("/users/{id}/enable", EnableUserAsync).MarkAuthEndpoint();
        admin.MapPost("/users/{id}/reset-password", ResetUserPasswordAsync).MarkAuthEndpoint();
    }

    public sealed record CreateUserRequest(
        string? Email,
        string? Name,
        [property: JsonPropertyName("global_role")] string? GlobalRole);

    private static async Task<IResult> CreateUserAsync(
        CreateUserRequest body,
        IUserStore users,
        IPasswordCredentialStore credentials,
        IPasswordHasher hasher,
        IPasswordResetStore resets,
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
        string id, IUserStore users, ISessionStore sessions, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (user is null)
        {
            return Results.NotFound();
        }

        await users.SetEnabledAsync(id, false, ct).ConfigureAwait(false);
        // Disabling revokes the user's sessions so an in-flight token cannot keep acting.
        await sessions.DeleteAllForUserAsync(id, ct).ConfigureAwait(false);
        return Results.Ok(new { enabled = false });
    }

    private static async Task<IResult> EnableUserAsync(string id, IUserStore users, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (user is null)
        {
            return Results.NotFound();
        }

        await users.SetEnabledAsync(id, true, ct).ConfigureAwait(false);
        return Results.Ok(new { enabled = true });
    }

    private static async Task<IResult> ResetUserPasswordAsync(
        string id,
        IUserStore users,
        IPasswordCredentialStore credentials,
        IPasswordHasher hasher,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var result = await AuthFlows.ResetUserPasswordAsync(id, users, credentials, hasher, sp, ct)
            .ConfigureAwait(false);

        return result switch
        {
            AuthFlows.ResetUserPasswordResult.Success success
                => Results.Ok(new { temporary_password = success.TemporaryPassword }),
            _ => Results.NotFound(),
        };
    }
}
