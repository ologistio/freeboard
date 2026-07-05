using Freeboard.Authz;
using Freeboard.Core.Authz;
using Freeboard.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Admin;

/// <summary>
/// Server-rendered management of an organisation's role assignments. The handler gates in-page on
/// <see cref="AuthzPageGuard"/> for <c>authz.assignment.write</c> on the target org (pipeline policies
/// do not run for page handlers). Grant/revoke go through the same guarded write store the API uses,
/// so the last-super-admin and last-direct-owner guards apply.
/// </summary>
public sealed class RoleAssignmentsModel(
    IAuthzStore store, IAuthzAdministrationStore admin, AuthzPageGuard pageGuard,
    ILogger<RoleAssignmentsModel> logger) : PageModel
{
    public string? OrgId { get; private set; }

    public IReadOnlyList<OrganisationRoleAssignmentRow> Assignments { get; private set; } = [];

    public string? Notice { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? orgId, CancellationToken ct)
    {
        OrgId = orgId;
        if (string.IsNullOrEmpty(orgId))
        {
            return Page();
        }

        if (await GuardAsync(orgId, ct) is { } denied)
        {
            return denied;
        }

        Assignments = await store.ListOrganisationAssignmentsAsync(orgId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostGrantAsync(string orgId, string userId, string roleKey, CancellationToken ct)
    {
        if (await GuardAsync(orgId, ct) is { } denied)
        {
            return denied;
        }

        var result = await admin.AssignOrganisationRoleAsync(userId, roleKey, orgId, ct);
        if (result.IsOk)
        {
            await AuditAsync("authz.assignment.write", userId, orgId, ct);
        }

        Notice = result.IsOk ? "Role granted." : result.Error;
        return RedirectToPage(new { orgId });
    }

    public async Task<IActionResult> OnPostRevokeAsync(string orgId, string userId, string roleKey, CancellationToken ct)
    {
        if (await GuardAsync(orgId, ct) is { } denied)
        {
            return denied;
        }

        var actor = User.FindFirst(Freeboard.Auth.AuthClaims.UserId)?.Value ?? string.Empty;
        var result = await admin.RevokeOrganisationRoleAsync(userId, roleKey, orgId, actor, ct);
        if (result.IsOk)
        {
            await AuditAsync("authz.assignment.revoke", userId, orgId, ct);
        }

        Notice = result.IsOk ? "Role revoked." : result.Error;
        return RedirectToPage(new { orgId });
    }

    private Task<IActionResult?> GuardAsync(string orgId, CancellationToken ct)
        => pageGuard.CheckAsync(User, AuthzActions.AuthzAssignmentWrite, new AuthzResource("organisation", orgId, orgId, []), ct);

    private Task AuditAsync(string action, string targetUserId, string orgId, CancellationToken ct)
        => AuthzMutationAudit.AppendAsync(
            admin, logger,
            new AuthzAuditEvent(action, User.FindFirst(Freeboard.Auth.AuthClaims.UserId)?.Value, AuthzActions.AuthzAssignmentWrite, "user", targetUserId, orgId, "Permit", "role assignment mutation"),
            ct);
}
