using Freeboard.Auth;
using Freeboard.Persistence.Auth;
using Freeboard.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Freeboard.Pages.Admin;

/// <summary>
/// Admin user-management list + create form, with per-user enable/disable/reset-password POST
/// handlers. Backed by the same store/flow layer the JSON user-admin endpoints use (not an HTTP call):
/// create and reset-password go through the shared <see cref="AuthFlows"/> helpers; enable, disable,
/// and list call the stores directly, matching the endpoint.
///
/// Authentication is the /admin folder authorize policy (page challenge scheme); the admin-role gate
/// is enforced in-page at the top of every handler via <see cref="AdminGuard"/>. The page carries no
/// limited-session-allowed marker, so a force-reset-limited session is funnelled to
/// /account/complete-reset before any handler runs.
/// </summary>
public sealed class UsersModel(
    IUserStore users,
    ISessionStore sessions,
    IPasswordCredentialStore credentials,
    IPasswordHasher hasher,
    IPasswordResetStore resets,
    TempPasswordDisplayStore tempPasswords,
    IServiceProvider serviceProvider) : PageModel
{
    public IReadOnlyList<UserRow> Users { get; private set; } = [];

    /// <summary>True when an email transport is configured, so the invite handoff is offered.</summary>
    public bool EmailConfigured { get; private set; }

    /// <summary>Set after a successful invite send; drives the in-page confirmation panel.</summary>
    public string? InvitedEmail { get; private set; }

    /// <summary>Set when the row was created but the invite could not be provisioned.</summary>
    public string? InviteFailedEmail { get; private set; }

    /// <summary>A transient notice (e.g. a stale-id not-found) rendered above the list.</summary>
    public string? Notice { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (AdminGuard.Check(User) is { } denied)
        {
            return denied;
        }

        await LoadAsync(ct).ConfigureAwait(false);
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(
        string? email, string? name, string? global_role, string? handoff, CancellationToken ct)
    {
        if (AdminGuard.Check(User) is { } denied)
        {
            return denied;
        }

        var mode = string.Equals(handoff, "invite", StringComparison.Ordinal)
            ? AuthFlows.CreateUserHandoff.EmailInvite
            : AuthFlows.CreateUserHandoff.TemporaryPassword;

        var result = await AuthFlows.CreateUserAsync(
            email, name, global_role, mode, users, credentials, hasher, resets, serviceProvider, ct)
            .ConfigureAwait(false);

        switch (result)
        {
            case AuthFlows.CreateUserResult.Success success:
                var target = tempPasswords.StashAndRedirectTarget(Response, success.TemporaryPassword);
                return Redirect(target);
            case AuthFlows.CreateUserResult.Invited invited:
                InvitedEmail = invited.User.Email;
                break;
            case AuthFlows.CreateUserResult.InviteSendFailed failed:
                InviteFailedEmail = failed.User.Email;
                break;
            case AuthFlows.CreateUserResult.Invalid invalid:
                foreach (var (field, messages) in invalid.Errors)
                {
                    foreach (var message in messages)
                    {
                        ModelState.AddModelError(field, message);
                    }
                }

                break;
            case AuthFlows.CreateUserResult.DuplicateEmail:
                ModelState.AddModelError("email", "A user with this email already exists.");
                break;
        }

        await LoadAsync(ct).ConfigureAwait(false);
        return Page();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(string id, CancellationToken ct)
    {
        if (AdminGuard.Check(User) is { } denied)
        {
            return denied;
        }

        var result = await AuthFlows.ResetUserPasswordAsync(id, users, credentials, hasher, serviceProvider, ct)
            .ConfigureAwait(false);

        if (result is AuthFlows.ResetUserPasswordResult.Success success)
        {
            var target = tempPasswords.StashAndRedirectTarget(Response, success.TemporaryPassword);
            return Redirect(target);
        }

        Notice = "That user no longer exists.";
        await LoadAsync(ct).ConfigureAwait(false);
        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync(string id, CancellationToken ct)
    {
        if (AdminGuard.Check(User) is { } denied)
        {
            return denied;
        }

        if (await users.GetByIdAsync(id, ct).ConfigureAwait(false) is null)
        {
            Notice = "That user no longer exists.";
        }
        else
        {
            await users.SetEnabledAsync(id, false, ct).ConfigureAwait(false);
            // Disabling revokes the user's sessions so an in-flight token cannot keep acting.
            await sessions.DeleteAllForUserAsync(id, ct).ConfigureAwait(false);
        }

        await LoadAsync(ct).ConfigureAwait(false);
        return Page();
    }

    public async Task<IActionResult> OnPostEnableAsync(string id, CancellationToken ct)
    {
        if (AdminGuard.Check(User) is { } denied)
        {
            return denied;
        }

        if (await users.GetByIdAsync(id, ct).ConfigureAwait(false) is null)
        {
            Notice = "That user no longer exists.";
        }
        else
        {
            await users.SetEnabledAsync(id, true, ct).ConfigureAwait(false);
        }

        await LoadAsync(ct).ConfigureAwait(false);
        return Page();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Users = await users.ListAsync(ct).ConfigureAwait(false);
        EmailConfigured = serviceProvider.GetService<AuthEmailService>() is not null;
    }
}
