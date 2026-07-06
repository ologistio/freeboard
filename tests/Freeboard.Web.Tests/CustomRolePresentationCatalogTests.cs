using Freeboard.Core.Authz;
using Freeboard.Enterprise;

namespace Freeboard.Web.Tests;

/// <summary>
/// The EE presentation catalog is display metadata over the Core allow-list: it must cover exactly the
/// authorable keys and introduce none outside them, so it can never widen the enforced set.
/// </summary>
public sealed class CustomRolePresentationCatalogTests
{
    [Fact]
    public void CatalogCoversExactlyTheAuthorableKeys()
    {
        var catalogKeys = CustomRolePresentationCatalog.Options.Select(o => o.PermissionKey).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(AuthzCustomRoles.AuthorablePermissionKeys.ToHashSet(StringComparer.Ordinal), catalogKeys);
        // No duplicate entries.
        Assert.Equal(CustomRolePresentationCatalog.Options.Count, catalogKeys.Count);
    }
}
