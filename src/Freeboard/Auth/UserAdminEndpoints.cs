using System.Text.Json.Serialization;
using Freeboard.Api;
using Freeboard.Persistence.Auth;
using MySqlConnector;

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
        IServiceProvider sp,
        CancellationToken ct)
    {
        // A missing JSON body binds as null; treat it as an empty request so the field-level
        // validation below produces the same 422 instead of dereferencing null.
        body ??= new CreateUserRequest(null, null, null);

        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(body.Email))
        {
            errors["email"] = ["An email is required."];
        }

        if (string.IsNullOrWhiteSpace(body.Name))
        {
            errors["name"] = ["A name is required."];
        }

        var role = body.GlobalRole ?? GlobalRoles.Member;
        if (!GlobalRoles.IsValid(role))
        {
            errors["global_role"] = ["Unknown role."];
        }

        if (errors.Count > 0)
        {
            return ApiResponses.ValidationProblem(errors);
        }

        // Pre-check gives a clean 422 in the common case; the DB unique index is still the
        // authoritative guard under a concurrent-create race (caught below).
        if (await users.GetByEmailAsync(body.Email!, ct).ConfigureAwait(false) is not null)
        {
            return ApiResponses.ValidationProblem("email", "A user with this email already exists.");
        }

        UserRow user;
        try
        {
            user = await users.CreateAsync(new NewUser(body.Email!, body.Name!, role), ct).ConfigureAwait(false);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            return ApiResponses.ValidationProblem("email", "A user with this email already exists.");
        }

        // A random ONE-TIME temp password; store only its hash; force a reset on first login.
        var tempPassword = TempPassword.Generate();
        await credentials.SetAsync(
            user.Id, hasher.Hash(tempPassword), CurrentSecretVersion(sp), ct).ConfigureAwait(false);
        await users.SetForcePasswordResetAsync(user.Id, true, ct).ConfigureAwait(false);

        var created = user with { ForcePasswordReset = true };
        return Results.Json(
            new { user = ApiResponses.UserObject(created), temporary_password = tempPassword },
            statusCode: StatusCodes.Status201Created);
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
        var user = await users.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (user is null)
        {
            return Results.NotFound();
        }

        // One transaction: set the new hash, bump the credential epoch, force a password reset,
        // AND revoke ALL the user's sessions.
        var tempPassword = TempPassword.Generate();
        await credentials.UpdateHashAndRevokeSessionsAsync(
            id, hasher.Hash(tempPassword), CurrentSecretVersion(sp),
            keepSessionId: null, setForcePasswordReset: true, upgradeKeptSessionToFull: false, ct)
            .ConfigureAwait(false);

        return Results.Ok(new { temporary_password = tempPassword });
    }

    private static int CurrentSecretVersion(IServiceProvider sp)
        => sp.GetRequiredService<AuthCryptoOptions>().CurrentPasswordSecretVersion;
}
