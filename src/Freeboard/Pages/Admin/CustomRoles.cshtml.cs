using Freeboard.Auth;
using Freeboard.Authz;
using Freeboard.Core.Authz;
using Freeboard.Core.Enterprise;
using Freeboard.Enterprise;
using Freeboard.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Admin;

/// <summary>
/// Server-rendered list of custom roles, each expandable to its permission tree, with Edit linking to
/// <see cref="CustomRoleDesignerModel"/> and an inline Delete. Each handler gates on the
/// <c>CustomPolicies</c> entitlement (<see cref="NotFoundResult"/> when off, so the surface is absent
/// not forbidden) then <see cref="AuthzPageGuard"/> for <c>system.admin</c>. Delete goes through the
/// same store the API uses, which enforces the security floor and writes the audit row.
/// </summary>
public sealed class CustomRolesModel(
    IAuthzStore store, IAuthzAdministrationStore admin, AuthzPageGuard pageGuard,
    IEnterpriseEntitlements entitlements) : PageModel
{
    public IReadOnlyList<RoleWithPermissions> Roles { get; private set; } = [];

    public IReadOnlyList<CustomRolePermissionOption> PermissionOptions => CustomRolePresentationCatalog.Options;

    // TempData so the message set before RedirectToPage survives the redirect to the GET that renders it.
    [TempData]
    public string? Notice { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } denied)
        {
            return denied;
        }

        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(string roleKey, CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } denied)
        {
            return denied;
        }

        var result = await admin.DeleteCustomRoleAsync(roleKey, Actor(), ct);
        Notice = result.IsOk ? "Role deleted." : result.Error;
        return RedirectToPage();
    }

    private async Task<IActionResult?> GuardAsync(CancellationToken ct)
    {
        if (!entitlements.IsEntitled(EnterpriseEntitlement.CustomPolicies))
        {
            return NotFound();
        }

        return await pageGuard.CheckAsync(User, AuthzActions.SystemAdmin, new AuthzResource("system", null, null, []), ct);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var rows = await store.ListCustomRolesAsync(ct);
        var details = new List<RoleWithPermissions>(rows.Count);
        foreach (var row in rows)
        {
            if (await store.GetRoleAsync(row.RoleKey, ct) is not { } detail)
            {
                continue;
            }

            details.Add(detail);
        }

        Roles = details;
    }

    private string Actor() => User.FindFirst(AuthClaims.UserId)?.Value ?? string.Empty;
}
