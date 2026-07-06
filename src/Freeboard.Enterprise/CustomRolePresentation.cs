using Freeboard.Core.Authz;

namespace Freeboard.Enterprise;

/// <summary>One authorable permission key rendered for the custom-role designer.</summary>
public sealed record CustomRolePermissionOption(string PermissionKey, string Label, string Description, string Group);

/// <summary>
/// Presentation metadata (label, description, group) for the custom-role designer, one entry per
/// authorable permission key. This is display only: the enforced set is <see
/// cref="AuthzCustomRoles.AuthorablePermissionKeys"/> in Core, which the write store checks. The
/// catalog is a strict subset of that allow-list and can never widen it, so a paid presentation layer
/// cannot escalate privilege.
/// </summary>
public static class CustomRolePresentationCatalog
{
    public static IReadOnlyList<CustomRolePermissionOption> Options { get; } =
    [
        new(AuthzActions.OrgRead, "Read organisations", "View the organisation tree.", "Organisations"),
        new(AuthzActions.OrgWrite, "Manage organisations", "Create, update, and delete organisations.", "Organisations"),
        new(AuthzActions.ComplianceRead, "Read compliance", "View compliance scoping.", "Compliance"),
        new(AuthzActions.ComplianceScopeWrite, "Write standard scope", "Set standard-level scope dispositions.", "Compliance"),
        new(AuthzActions.ComplianceRequirementScopeWrite, "Write requirement scope", "Set requirement-level scope dispositions.", "Compliance"),
    ];
}
