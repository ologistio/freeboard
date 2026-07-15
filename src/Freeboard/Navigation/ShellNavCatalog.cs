using Freeboard.Core.Enterprise;

namespace Freeboard.Navigation;

/// <summary>
/// The authorization gate a nav item requires beyond authentication. The resolver maps each value to
/// the matching <see cref="Freeboard.Authz.AuthzViewHelpers"/> check; a failing gate drops the item.
/// </summary>
public enum ShellNavAccess
{
    /// <summary>Any authenticated user sees the item.</summary>
    Authenticated,

    /// <summary>Requires reaching the admin surface (<c>user.manage</c> or <c>system.admin</c>).</summary>
    CanReachAdmin,

    /// <summary>Requires <c>system.admin</c>, the permission the custom-role surface enforces.</summary>
    CanAdministerSystem,

    /// <summary>Requires <c>authz.assignment.write</c> in any org, or <c>system.admin</c> - the role-assignments page's per-org write permission.</summary>
    CanReachRoleAssignments,
}

/// <summary>
/// One rail destination: a stable key, a display label (sentence case, W1), its route, the group it
/// belongs to (null for the group-less top set), the authorization gate it requires, and an optional
/// enterprise entitlement. Pure declarative data - the per-request evaluation lives in
/// <see cref="ShellNavResolver"/>.
/// </summary>
public sealed record ShellNavItem(
    string Key,
    string Label,
    string Route,
    string? Group,
    ShellNavAccess Access = ShellNavAccess.Authenticated,
    EnterpriseEntitlement? Entitlement = null);

/// <summary>A nav group: a shared-noun label (null for the top group-less set) and its ordered items.</summary>
public sealed record ShellNavGroup(string? Label, IReadOnlyList<ShellNavItem> Items);

/// <summary>
/// The single declarative source for the rail, the breadcrumb group links, and the future command-
/// palette index (so they cannot disagree - N2). Every destination sits under exactly one group and
/// appears once. Configuration and administration pages live under <c>/settings</c> (N4 at the route
/// level). Role Assignments is gated on <c>authz.assignment.write</c> in any org (or <c>system.admin</c>),
/// mirroring the per-org write permission the page force-enforces.
/// </summary>
public static class ShellNavCatalog
{
    public static IReadOnlyList<ShellNavGroup> Groups { get; } =
    [
        new ShellNavGroup(null,
        [
            new ShellNavItem("home", "Home", "/home", null),
        ]),
        new ShellNavGroup("Comply",
        [
            new ShellNavItem("soa", "Statement of applicability", "/compliance/statement-of-applicability", "Comply"),
        ]),
        new ShellNavGroup("Risk",
        [
            new ShellNavItem("vendors", "Vendors", "/compliance/vendors", "Risk"),
        ]),
        new ShellNavGroup("Platform",
        [
            new ShellNavItem("evidence-collectors", "Evidence collectors", "/settings/evidence-collectors", "Platform"),
            new ShellNavItem("integration-connections", "Integration connections", "/settings/integration-connections", "Platform"),
            new ShellNavItem("attestation-templates", "Attestation templates", "/settings/attestation-templates", "Platform"),
            new ShellNavItem("users", "Users", "/settings/users", "Platform", ShellNavAccess.CanReachAdmin),
            new ShellNavItem(
                "custom-roles", "Custom roles", "/settings/custom-roles", "Platform",
                ShellNavAccess.CanAdministerSystem, EnterpriseEntitlement.CustomPolicies),
            new ShellNavItem(
                "role-assignments", "Role assignments", "/settings/role-assignments", "Platform",
                ShellNavAccess.CanReachRoleAssignments),
        ]),
    ];

    /// <summary>Every catalog item, flattened in rail order.</summary>
    public static IReadOnlyList<ShellNavItem> Items { get; } = Groups.SelectMany(g => g.Items).ToList();
}
