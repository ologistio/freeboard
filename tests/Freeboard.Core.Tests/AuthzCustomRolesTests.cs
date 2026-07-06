using Freeboard.Core.Authz;

namespace Freeboard.Core.Tests;

public sealed class AuthzCustomRolesTests
{
    [Fact]
    public void AuthorableKeysAreExactlyTheFiveOrgTreeKeys()
    {
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                AuthzActions.OrgRead,
                AuthzActions.OrgWrite,
                AuthzActions.ComplianceRead,
                AuthzActions.ComplianceScopeWrite,
                AuthzActions.ComplianceRequirementScopeWrite,
            },
            AuthzCustomRoles.AuthorablePermissionKeys.ToHashSet(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(AuthzActions.SystemAdmin)]
    [InlineData(AuthzActions.UserManage)]
    [InlineData(AuthzActions.AuthzAssignmentWrite)]
    public void PrivilegedKeysAreNotAuthorable(string key)
        => Assert.DoesNotContain(key, AuthzCustomRoles.AuthorablePermissionKeys);

    [Theory]
    [InlineData("custom:auditor")]
    [InlineData("custom:read-only-auditor")]
    [InlineData("custom:tier-2")]
    public void AcceptsValidCustomKey(string key)
        => Assert.True(AuthzCustomRoles.IsAuthorableRoleKey(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auditor")] // no prefix
    [InlineData("org-owner")] // seeded key, no prefix
    [InlineData("custom:")] // empty slug
    [InlineData("custom:-lead")] // leading hyphen
    [InlineData("custom:lead-")] // trailing hyphen
    [InlineData("custom:a--b")] // double hyphen
    [InlineData("custom:Auditor")] // uppercase
    [InlineData("custom:audit_role")] // underscore
    [InlineData("custom:audit:role")] // extra colon
    public void RejectsMalformedKey(string? key)
        => Assert.False(AuthzCustomRoles.IsAuthorableRoleKey(key));

    [Fact]
    public void RejectsOverLengthKey()
    {
        var key = "custom:" + new string('a', AuthzCustomRoles.MaxRoleKeyLength);
        Assert.True(key.Length > AuthzCustomRoles.MaxRoleKeyLength);
        Assert.False(AuthzCustomRoles.IsAuthorableRoleKey(key));
    }
}
