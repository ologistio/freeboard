using Freeboard.Persistence;

namespace Freeboard.Authz;

/// <summary>
/// The single best-effort append for privilege-and-exposure MUTATION audit rows (role-assignment
/// writes, user-admin create/disable/enable/reset, cross-user session actions, and the org-create
/// creator-owner grant), shared by the minimal-API endpoints and the admin Razor pages so the two
/// cannot drift. ILogger is the reliable channel; the persistent write is best-effort - a failure is
/// logged at warning and never turns a succeeded mutation into an error.
/// </summary>
public static class AuthzMutationAudit
{
    /// <summary>The logger category used for mutation-audit append failures.</summary>
    public const string LoggerCategory = "Freeboard.Authz.MutationAudit";

    public static ILogger Logger(IServiceProvider services)
        => services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);

    public static async Task AppendAsync(
        IAuthzAdministrationStore store, ILogger logger, AuthzAuditEvent auditEvent, CancellationToken ct)
    {
        try
        {
            await store.AppendAuditEventAsync(auditEvent, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Persisting the authz mutation audit row failed; skipping (best-effort).");
        }
    }
}
