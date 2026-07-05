using System.Security.Claims;
using Freeboard.Persistence;

namespace Freeboard.Web;

/// <summary>
/// The single accessibility seam. A pure function of the already-loaded organisation list that
/// returns the subset of ids the user may access, so selection and scoping fail closed. It performs
/// NO store read of its own - the caller passes the list it already loaded. This is the one place a
/// future per-organisation access model narrows the returned subset.
/// </summary>
public interface IOrgAccess
{
    /// <summary>
    /// The subset of the supplied organisations the user may access. Async because the authz-backed
    /// default reads the principal's grants; the accessible set is memoized per request alongside the
    /// fact load.
    /// </summary>
    ValueTask<IReadOnlySet<string>> AccessibleOrgIdsAsync(
        ClaimsPrincipal user,
        IReadOnlyList<OrganisationRow> organisations,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Grants every authenticated user access to every supplied organisation. Retained as a unit-test
/// double for the selection/resolver logic; the app default is <c>AuthzOrgAccess</c> (see Program.cs).
/// </summary>
public sealed class AllOrgAccess : IOrgAccess
{
    public ValueTask<IReadOnlySet<string>> AccessibleOrgIdsAsync(
        ClaimsPrincipal user,
        IReadOnlyList<OrganisationRow> organisations,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlySet<string>>(
            organisations.Select(o => o.Id).ToHashSet(StringComparer.Ordinal));
}
