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
    IReadOnlySet<string> AccessibleOrgIds(ClaimsPrincipal user, IReadOnlyList<OrganisationRow> organisations);
}

/// <summary>
/// The v1 default: every authenticated user may access every organisation in the supplied list,
/// matching today's model where any authenticated user reads the whole compliance domain. The
/// <paramref name="user"/> is intentionally unused so a future membership model can narrow by user
/// without a signature change.
/// </summary>
public sealed class AllOrgAccess : IOrgAccess
{
    public IReadOnlySet<string> AccessibleOrgIds(
        ClaimsPrincipal user, IReadOnlyList<OrganisationRow> organisations)
        => organisations.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
}
