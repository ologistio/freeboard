using Freeboard.Compliance;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

/// <summary>
/// The shared inclusive-ancestry walk. RBAC correctness depends on its cycle guard: a malformed
/// parent map (self-parent or a loop) must terminate rather than spin, and the returned chain must
/// run start -> ... -> root so a grant on any ancestor can be matched.
/// </summary>
public sealed class OrgAncestryTests
{
    private static IReadOnlyDictionary<string, OrganisationRow> Map(params OrganisationRow[] rows)
        => rows.ToDictionary(r => r.Id, StringComparer.Ordinal);

    private static OrganisationRow Org(string id, string? parent)
        => new(id, id, "Company", parent);

    [Fact]
    public void ReturnsChainFromStartToRoot()
    {
        var byId = Map(Org("root", null), Org("mid", "root"), Org("leaf", "mid"));

        var chain = OrgAncestry.InclusiveAncestors("leaf", byId);

        Assert.Equal(new[] { "leaf", "mid", "root" }, chain);
    }

    [Fact]
    public void UnknownStartIdYieldsJustThatId()
    {
        var byId = Map(Org("root", null));

        var chain = OrgAncestry.InclusiveAncestors("ghost", byId);

        Assert.Equal(new[] { "ghost" }, chain);
    }

    [Fact]
    public void DanglingParentIdIsIncludedThenChainStops()
    {
        // leaf's parent id is not a real node: the walk appends the id (it cannot be resolved
        // further) and then terminates. Config validation rejects unknown parents separately.
        var byId = Map(Org("leaf", "absent"));

        var chain = OrgAncestry.InclusiveAncestors("leaf", byId);

        Assert.Equal(new[] { "leaf", "absent" }, chain);
    }

    [Fact]
    public void SelfParentTerminatesWithoutRepeating()
    {
        var byId = Map(Org("a", "a"));

        var chain = OrgAncestry.InclusiveAncestors("a", byId);

        Assert.Equal(new[] { "a" }, chain);
    }

    [Fact]
    public void TwoNodeCycleTerminatesWithEachNodeOnce()
    {
        var byId = Map(Org("a", "b"), Org("b", "a"));

        var chain = OrgAncestry.InclusiveAncestors("a", byId);

        Assert.Equal(new[] { "a", "b" }, chain);
    }
}
