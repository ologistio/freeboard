namespace Freeboard.Auth;

/// <summary>
/// The coarse global roles. Fine-grained RBAC is a non-goal; a single admin role gates
/// the user-management endpoints. Stored verbatim in <c>users.global_role</c>.
/// </summary>
public static class GlobalRoles
{
    public const string Admin = "admin";
    public const string Member = "member";

    /// <summary>The known roles a created user may be assigned.</summary>
    public static bool IsValid(string? role) => role is Admin or Member;
}
