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
/// Server-rendered two-step designer for a single custom role, used for both create (no route slug)
/// and edit (route slug names the role). Step 1 collects the fields; step 2 shows the permission tree
/// to verify before committing. Gating mirrors the list page: <c>CustomPolicies</c> entitlement
/// (<see cref="NotFoundResult"/> when off) then <c>system.admin</c>. Writes go through the shared
/// administration store, which re-enforces the security floor and writes the audit row.
/// </summary>
public sealed class CustomRoleDesignerModel(
    IAuthzStore store, IAuthzAdministrationStore admin, AuthzPageGuard pageGuard,
    IEnterpriseEntitlements entitlements) : PageModel
{
    /// <summary>The slug after <c>custom:</c> when editing; null when creating. Route-bound only.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Slug { get; set; }

    /// <summary>The create-mode slug text (composed with the <c>custom:</c> prefix). Unused when editing.</summary>
    [BindProperty]
    public string? KeyInput { get; set; }

    [BindProperty]
    public string? Title { get; set; }

    [BindProperty]
    public string? Description { get; set; }

    [BindProperty]
    public string[] SelectedPermissions { get; set; } = [];

    /// <summary>Which step to render (1 = design, 2 = verify). Set by the handlers, never bound.</summary>
    public int Step { get; private set; } = 1;

    public bool IsEdit => !string.IsNullOrEmpty(Slug);

    public string RoleKey => AuthzCustomRoles.CustomRoleKeyPrefix + (IsEdit ? Slug : (KeyInput ?? string.Empty).Trim());

    public IReadOnlyList<CustomRolePermissionOption> PermissionOptions => CustomRolePresentationCatalog.Options;

    [TempData]
    public string? Notice { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } denied)
        {
            return denied;
        }

        if (IsEdit)
        {
            if (await store.GetRoleAsync(RoleKey, ct) is not { } role)
            {
                return NotFound();
            }

            Title = role.Role.Title;
            Description = role.Role.Description;
            SelectedPermissions = role.PermissionKeys.ToArray();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostContinueAsync(CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } denied)
        {
            return denied;
        }

        SelectedPermissions = SelectedPermissions.Distinct(StringComparer.Ordinal).ToArray();
        if (!Validate())
        {
            return Page();
        }

        Step = 2;
        return Page();
    }

    public async Task<IActionResult> OnPostBackAsync(CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } denied)
        {
            return denied;
        }

        Step = 1;
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        if (await GuardAsync(ct) is { } denied)
        {
            return denied;
        }

        SelectedPermissions = SelectedPermissions.Distinct(StringComparer.Ordinal).ToArray();
        if (!Validate())
        {
            return Page();
        }

        var title = Title ?? string.Empty;
        var description = Description ?? string.Empty;
        var result = IsEdit
            ? await admin.UpdateCustomRoleAsync(RoleKey, title, description, SelectedPermissions, Actor(), ct)
            : await admin.CreateCustomRoleAsync(RoleKey, title, description, SelectedPermissions, Actor(), ct);

        if (!result.IsOk)
        {
            // A store-level rejection (e.g. duplicate key) is not a field error, so return to the
            // design step with the message rather than showing the verify tree over stale data.
            Step = 1;
            ModelState.AddModelError(string.Empty, result.Error ?? "The role could not be saved.");
            return Page();
        }

        Notice = IsEdit ? "Role updated." : "Role created.";
        return RedirectToPage("/Admin/CustomRoles");
    }

    /// <summary>
    /// Mirrors the API's field validation (<c>CustomRoleEndpoints.Validate</c>) so the page gives
    /// field-level errors before reaching the store. The store re-checks the same floor regardless.
    /// </summary>
    private bool Validate()
    {
        if (!IsEdit && !AuthzCustomRoles.IsAuthorableRoleKey(RoleKey))
        {
            ModelState.AddModelError(string.Empty,
                "Role key must be a bounded lowercase-hyphenated slug (letters, digits, single interior hyphens).");
        }

        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 190)
        {
            ModelState.AddModelError(string.Empty, "Name is required and must be at most 190 characters.");
        }

        if ((Description ?? string.Empty).Length > 512)
        {
            ModelState.AddModelError(string.Empty, "Description must be at most 512 characters.");
        }

        if (SelectedPermissions.Any(k => !AuthzCustomRoles.AuthorablePermissionKeys.Contains(k)))
        {
            ModelState.AddModelError(string.Empty, "A selected permission is not authorable.");
        }

        return ModelState.IsValid;
    }

    private async Task<IActionResult?> GuardAsync(CancellationToken ct)
    {
        if (!entitlements.IsEntitled(EnterpriseEntitlement.CustomPolicies))
        {
            return NotFound();
        }

        return await pageGuard.CheckAsync(User, AuthzActions.SystemAdmin, new AuthzResource("system", null, null, []), ct);
    }

    private string Actor() => User.FindFirst(AuthClaims.UserId)?.Value ?? string.Empty;
}
