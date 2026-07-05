namespace Freeboard.Core.Authz;

/// <summary>
/// The seeded role keys and their scope. Roles are DATA (the <c>authz_*</c> tables); these constants
/// are the compile-time references the guards, backfills, and creator-grant use (e.g. the last
/// super-admin guard keys on <see cref="SuperAdmin"/>, org-create grants <see cref="OrgOwner"/>).
/// </summary>
public static class AuthzRoles
{
    public const string SuperAdmin = "super-admin";
    public const string OrgOwner = "org-owner";
    public const string ComplianceManager = "compliance-manager";
    public const string ComplianceReader = "compliance-reader";

    /// <summary>The role scope values persisted in <c>authz_roles.scope</c>.</summary>
    public const string ScopeSystem = "system";
    public const string ScopeOrganisation = "organisation";
}
