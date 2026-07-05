namespace Freeboard.Core.Authz;

/// <summary>
/// The action-identifier catalog: the compile-time contract between an endpoint call site
/// (<c>RequirePermission(AuthzActions.ComplianceScopeWrite, ...)</c>) and the engine. These are the
/// permission KEYS; which role carries which key is operational data (the <c>authz_*</c> tables), not
/// code. <see cref="SystemAdmin"/> is the break-glass permit-all key.
/// </summary>
public static class AuthzActions
{
    public const string SystemAdmin = "system.admin";
    public const string AuthzAssignmentWrite = "authz.assignment.write";
    public const string OrgRead = "org.read";
    public const string OrgWrite = "org.write";
    public const string ComplianceRead = "compliance.read";
    public const string ComplianceScopeWrite = "compliance.scope.write";
    public const string ComplianceRequirementScopeWrite = "compliance.requirement-scope.write";
    public const string UserManage = "user.manage";
}
