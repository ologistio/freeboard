using System.Text.Json.Serialization;
using Freeboard.Api;
using Freeboard.Auth;
using Freeboard.Core.Authz;
using Freeboard.Core.Enterprise;
using Freeboard.Entitlements;
using Freeboard.Persistence;

namespace Freeboard.Authz;

/// <summary>
/// The custom-role designer API: super-admin CRUD over author-defined organisation-scoped roles. The
/// group is gated by <c>RequireEntitlement(CustomPolicies)</c> (404 when the feature is off, ahead of
/// the permission gate so an unentitled super-admin sees absence not denial) then
/// <c>RequirePermission(system.admin)</c>, force-enforced. Routes carry NO <c>AuthEndpoint</c> marker,
/// so GitOps read-only blocks their mutations with 409: minting policy belongs in the git repository.
/// The audit row is written by the store inside the mutation transaction, not here.
/// </summary>
public static class CustomRoleEndpoints
{
    public sealed record CustomRoleRequest(
        [property: JsonPropertyName("role_key")] string? RoleKey,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("permission_keys")] IReadOnlyList<string>? PermissionKeys);

    public static void MapCustomRoleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(ApiRoutes.ApiRoutePrefix).RequireAuthorization();
        group.RequireEntitlement(EnterpriseEntitlement.CustomPolicies);
        group.RequirePermission(AuthzActions.SystemAdmin, AuthzSelectors.System, alwaysEnforce: true);

        group.MapGet("/custom-roles", ListAsync);
        group.MapGet("/custom-roles/{roleKey}", GetAsync);
        group.MapPost("/custom-roles", CreateAsync);
        group.MapPut("/custom-roles/{roleKey}", UpdateAsync);
        group.MapDelete("/custom-roles/{roleKey}", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(IAuthzStore store, CancellationToken ct)
    {
        var rows = await store.ListCustomRolesAsync(ct);
        return Results.Ok(rows.Select(Summary));
    }

    private static async Task<IResult> GetAsync(string roleKey, IAuthzStore store, CancellationToken ct)
    {
        var role = await store.GetRoleAsync(roleKey, ct);
        return role is null ? Results.NotFound() : Results.Ok(Detail(role));
    }

    private static object Detail(RoleWithPermissions role) => new
    {
        role_key = role.Role.RoleKey,
        title = role.Role.Title,
        description = role.Role.Description,
        scope = role.Role.Scope,
        is_system = role.Role.IsSystem,
        permission_keys = role.PermissionKeys,
        created_at = role.Role.CreatedAt,
        updated_at = role.Role.UpdatedAt,
    };

    private static async Task<IResult> CreateAsync(
        CustomRoleRequest body, HttpContext ctx, IAuthzAdministrationStore admin, CancellationToken ct)
    {
        body ??= new CustomRoleRequest(null, null, null, null);
        var permissionKeys = DistinctKeys(body.PermissionKeys);
        if (Validate(body.RoleKey, body.Title, body.Description, permissionKeys, requireKey: true) is { } error)
        {
            return ValidationProblem(error);
        }

        var result = await admin.CreateCustomRoleAsync(
            body.RoleKey!, body.Title!, body.Description ?? string.Empty, permissionKeys, Actor(ctx), ct);
        return Map(result, StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateAsync(
        string roleKey, CustomRoleRequest body, HttpContext ctx, IAuthzStore store,
        IAuthzAdministrationStore admin, CancellationToken ct)
    {
        // role_key is immutable: the route value wins and any body role_key is ignored.
        body ??= new CustomRoleRequest(null, null, null, null);
        var permissionKeys = DistinctKeys(body.PermissionKeys);
        if (Validate(roleKey, body.Title, body.Description, permissionKeys, requireKey: false) is { } error)
        {
            return ValidationProblem(error);
        }

        var result = await admin.UpdateCustomRoleAsync(
            roleKey, body.Title!, body.Description ?? string.Empty, permissionKeys, Actor(ctx), ct);
        if (!result.IsOk)
        {
            return Map(result, StatusCodes.Status200OK);
        }

        // Return the current state so the caller sees the applied title, description, and permission set.
        var updated = await store.GetRoleAsync(roleKey, ct);
        return updated is null ? Results.NotFound() : Results.Ok(Detail(updated));
    }

    private static async Task<IResult> DeleteAsync(
        string roleKey, HttpContext ctx, IAuthzAdministrationStore admin, CancellationToken ct)
    {
        var result = await admin.DeleteCustomRoleAsync(roleKey, Actor(ctx), ct);
        return Map(result, StatusCodes.Status204NoContent);
    }

    private static object Summary(CustomRoleRow r) => new
    {
        role_key = r.RoleKey,
        title = r.Title,
        description = r.Description,
        scope = r.Scope,
        is_system = r.IsSystem,
        created_at = r.CreatedAt,
        updated_at = r.UpdatedAt,
    };

    private static string Actor(HttpContext ctx) => ctx.User.FindFirst(AuthClaims.UserId)?.Value ?? string.Empty;

    // Collapse exact duplicate permission keys: a role either has a permission or not. The store
    // dedupes too, so a duplicate never reaches the (role_key, permission_key) primary key as a fault.
    private static IReadOnlyList<string> DistinctKeys(IReadOnlyList<string>? permissionKeys)
        => permissionKeys is null ? [] : permissionKeys.Distinct(StringComparer.Ordinal).ToArray();

    // Fail-fast validation mirroring the store's floor (which re-validates as defence in depth). The
    // permission-key allow-list is the security check; the store enforces the same set.
    private static string? Validate(
        string? roleKey, string? title, string? description, IReadOnlyList<string> permissionKeys, bool requireKey)
    {
        if (requireKey && !AuthzCustomRoles.IsAuthorableRoleKey(roleKey))
        {
            return "role_key must be a reserved 'custom:' key with a bounded lowercase-hyphenated slug.";
        }

        if (string.IsNullOrWhiteSpace(title) || title.Length > 190)
        {
            return "title is required and must be at most 190 characters.";
        }

        if (description is { Length: > 512 })
        {
            return "description must be at most 512 characters.";
        }

        var bad = permissionKeys.FirstOrDefault(k => !AuthzCustomRoles.AuthorablePermissionKeys.Contains(k));
        if (bad is not null)
        {
            return $"permission_keys contains a key that is not authorable: '{bad}'.";
        }

        return null;
    }

    private static IResult Map(AuthzWriteResult result, int okStatus) => result.Status switch
    {
        AuthzWriteStatus.Ok => Results.StatusCode(okStatus),
        AuthzWriteStatus.NotFound => Results.NotFound(),
        AuthzWriteStatus.Conflict => Conflict(result.Error!),
        _ => ValidationProblem(result.Error!),
    };

    private static IResult ValidationProblem(string detail) => Results.Problem(
        title: "Invalid custom role", detail: detail, statusCode: StatusCodes.Status422UnprocessableEntity,
        type: "https://freeboard.dev/problems/validation");

    private static IResult Conflict(string detail) => Results.Problem(
        title: "Conflicting custom role", detail: detail, statusCode: StatusCodes.Status409Conflict,
        type: "https://freeboard.dev/problems/conflict");
}
