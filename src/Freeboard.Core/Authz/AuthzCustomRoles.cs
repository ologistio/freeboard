namespace Freeboard.Core.Authz;

/// <summary>
/// The MIT security floor for author-defined custom roles, enforced by the write store independent of
/// any entitlement. A custom role key carries the reserved <see cref="CustomRoleKeyPrefix"/> (no seeded
/// key contains a colon, so the namespace cannot collide) followed by a bounded lowercase-ASCII slug
/// that fits <c>authz_roles.role_key VARCHAR(64)</c> and is a safe URL segment. A custom role may only
/// grant keys in <see cref="AuthorablePermissionKeys"/>; the privileged keys (<c>system.admin</c>,
/// <c>user.manage</c>, <c>authz.assignment.write</c>) are absent, so this single positive allow-list
/// also rejects them.
/// </summary>
public static class AuthzCustomRoles
{
    public const string CustomRoleKeyPrefix = "custom:";

    /// <summary>The maximum <c>role_key</c> length, matching the column width.</summary>
    public const int MaxRoleKeyLength = 64;

    /// <summary>The org-tree read/write permission keys a custom role may compose.</summary>
    public static readonly IReadOnlySet<string> AuthorablePermissionKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        AuthzActions.OrgRead,
        AuthzActions.OrgWrite,
        AuthzActions.ComplianceRead,
        AuthzActions.ComplianceScopeWrite,
        AuthzActions.ComplianceRequirementScopeWrite,
    };

    /// <summary>
    /// True when <paramref name="roleKey"/> is a well-formed custom key: the reserved prefix, then a
    /// slug of lowercase ASCII letters, digits, and single interior hyphens, with the whole key at most
    /// <see cref="MaxRoleKeyLength"/> characters.
    /// </summary>
    public static bool IsAuthorableRoleKey(string? roleKey)
    {
        if (string.IsNullOrEmpty(roleKey)
            || roleKey.Length > MaxRoleKeyLength
            || !roleKey.StartsWith(CustomRoleKeyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var slug = roleKey.AsSpan(CustomRoleKeyPrefix.Length);
        return IsSlug(slug);
    }

    private static bool IsSlug(ReadOnlySpan<char> slug)
    {
        if (slug.IsEmpty || slug[0] == '-' || slug[^1] == '-')
        {
            return false;
        }

        for (var i = 0; i < slug.Length; i++)
        {
            var c = slug[i];
            var ok = c is (>= 'a' and <= 'z') or (>= '0' and <= '9')
                || (c == '-' && slug[i - 1] != '-');
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
