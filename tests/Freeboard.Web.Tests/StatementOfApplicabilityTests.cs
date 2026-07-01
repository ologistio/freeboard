using Freeboard.Compliance;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

public sealed class StatementOfApplicabilityTests
{
    private static readonly OrganisationRow Company = new("company", "Company", "Company", null);
    private static readonly OrganisationRow Department = new("company-dept", "Department", "Department", "company");
    private static readonly OrganisationRow Team = new("company-dept-team", "Team", "Department", "company-dept");

    [Fact]
    public void ExplicitDispositionWinsOverAncestors()
    {
        var scopes = new[]
        {
            new ScopeRow("s1", "In at company", "company", "std", "In"),
            new ScopeRow("s2", "Out at dept", "company-dept", "std", "Out"),
        };

        var nodes = StatementOfApplicability.Resolve([Company, Department], scopes, "std");

        var dept = nodes.Single(n => n.Id == "company-dept");
        Assert.Equal("Out", dept.Disposition);
        Assert.Equal(SoaResolution.Explicit, dept.Resolution);
    }

    [Fact]
    public void ChildInheritsNearestAncestor()
    {
        var scopes = new[] { new ScopeRow("s1", "In at company", "company", "std", "In") };

        var nodes = StatementOfApplicability.Resolve([Company, Department, Team], scopes, "std");

        var dept = nodes.Single(n => n.Id == "company-dept");
        Assert.Equal("In", dept.Disposition);
        Assert.Equal(SoaResolution.Inherited, dept.Resolution);

        var team = nodes.Single(n => n.Id == "company-dept-team");
        Assert.Equal("In", team.Disposition);
        Assert.Equal(SoaResolution.Inherited, team.Resolution);
    }

    [Fact]
    public void NearestAncestorWinsOverFartherAncestor()
    {
        var scopes = new[]
        {
            new ScopeRow("s1", "In at company", "company", "std", "In"),
            new ScopeRow("s2", "Out at dept", "company-dept", "std", "Out"),
        };

        var nodes = StatementOfApplicability.Resolve([Company, Department, Team], scopes, "std");

        var team = nodes.Single(n => n.Id == "company-dept-team");
        Assert.Equal("Out", team.Disposition);
        Assert.Equal(SoaResolution.Inherited, team.Resolution);
    }

    [Fact]
    public void NoAncestorDispositionIsUndeterminedNotOut()
    {
        var nodes = StatementOfApplicability.Resolve([Company, Department], [], "std");

        foreach (var node in nodes)
        {
            Assert.Null(node.Disposition);
            Assert.Equal(SoaResolution.Undetermined, node.Resolution);
        }
    }

    [Fact]
    public void ScopeForAnotherStandardDoesNotLeak()
    {
        var scopes = new[] { new ScopeRow("s1", "In for other", "company", "other-std", "In") };

        var nodes = StatementOfApplicability.Resolve([Company, Department], scopes, "std");

        Assert.All(nodes, n => Assert.Equal(SoaResolution.Undetermined, n.Resolution));
    }

    [Fact]
    public void NodesOrderedById()
    {
        var unordered = new[] { Team, Company, Department };

        var nodes = StatementOfApplicability.Resolve(unordered, [], "std");

        Assert.Equal(["company", "company-dept", "company-dept-team"], nodes.Select(n => n.Id).ToArray());
    }
}
