using Freeboard.Compliance;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// Unit tests for the pure <see cref="OrgScope.InScopeIds"/> subtree helper: a root selects its whole
/// subtree, a leaf selects only itself, null is the accessible set, an unknown id is empty, a cyclic
/// parent link terminates, and a restricted accessible set bounds both "All" and a selected subtree.
/// </summary>
public sealed class OrgScopeTests
{
    // company -> dept -> team, plus a sibling company. Ordered arbitrarily to exercise the walk.
    private static IReadOnlyList<OrganisationRow> Tree() =>
    [
        new OrganisationRow("company", "Company", "Company", null),
        new OrganisationRow("dept", "Department", "Department", "company"),
        new OrganisationRow("team", "Team", "Department", "dept"),
        new OrganisationRow("other", "Other Company", "Company", null),
    ];

    private static IReadOnlySet<string> All(IReadOnlyList<OrganisationRow> orgs) =>
        orgs.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void RootSelectsWholeSubtree()
    {
        var orgs = Tree();
        var scope = OrgScope.InScopeIds(orgs, All(orgs), "company");
        Assert.Equal(new HashSet<string> { "company", "dept", "team" }, scope);
    }

    [Fact]
    public void LeafSelectsOnlyItself()
    {
        var orgs = Tree();
        var scope = OrgScope.InScopeIds(orgs, All(orgs), "team");
        Assert.Equal(new HashSet<string> { "team" }, scope);
    }

    [Fact]
    public void NullSelectsAccessibleSet()
    {
        var orgs = Tree();
        var scope = OrgScope.InScopeIds(orgs, All(orgs), null);
        Assert.Equal(All(orgs), scope);
    }

    [Fact]
    public void UnknownIdYieldsEmpty()
    {
        var orgs = Tree();
        var scope = OrgScope.InScopeIds(orgs, All(orgs), "missing");
        Assert.Empty(scope);
    }

    [Fact]
    public void CyclicParentLinksTerminate()
    {
        // a <-> b cycle: each is the other's parent. Both are reachable only from within the cycle.
        IReadOnlyList<OrganisationRow> orgs =
        [
            new OrganisationRow("a", "A", "Company", "b"),
            new OrganisationRow("b", "B", "Company", "a"),
        ];
        var scope = OrgScope.InScopeIds(orgs, All(orgs), "a");
        Assert.Equal(new HashSet<string> { "a", "b" }, scope);
    }

    [Fact]
    public void RestrictedAccessibleSetBoundsNullSelection()
    {
        var orgs = Tree();
        IReadOnlySet<string> accessible = new HashSet<string>(StringComparer.Ordinal) { "company", "dept" };
        var scope = OrgScope.InScopeIds(orgs, accessible, null);
        Assert.Equal(accessible, scope);
        Assert.DoesNotContain("team", scope);
        Assert.DoesNotContain("other", scope);
    }

    [Fact]
    public void RestrictedAccessibleSetBoundsSelectedSubtree()
    {
        var orgs = Tree();
        // team is accessible-excluded, so the company subtree drops it.
        IReadOnlySet<string> accessible = new HashSet<string>(StringComparer.Ordinal) { "company", "dept" };
        var scope = OrgScope.InScopeIds(orgs, accessible, "company");
        Assert.Equal(new HashSet<string> { "company", "dept" }, scope);
    }
}
