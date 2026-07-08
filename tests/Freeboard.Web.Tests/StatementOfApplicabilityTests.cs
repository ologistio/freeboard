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

        var nodes = StatementOfApplicability.Resolve([Company, Department], scopes, [], [], "std");

        var dept = nodes.Single(n => n.Id == "company-dept");
        Assert.Equal("Out", dept.Disposition);
        Assert.Equal(SoaResolution.Explicit, dept.Resolution);
    }

    [Fact]
    public void ChildInheritsNearestAncestor()
    {
        var scopes = new[] { new ScopeRow("s1", "In at company", "company", "std", "In") };

        var nodes = StatementOfApplicability.Resolve([Company, Department, Team], scopes, [], [], "std");

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

        var nodes = StatementOfApplicability.Resolve([Company, Department, Team], scopes, [], [], "std");

        var team = nodes.Single(n => n.Id == "company-dept-team");
        Assert.Equal("Out", team.Disposition);
        Assert.Equal(SoaResolution.Inherited, team.Resolution);
    }

    [Fact]
    public void NoScopeOnPathDefaultsIn()
    {
        var nodes = StatementOfApplicability.Resolve([Company, Department], [], [], [], "std");

        foreach (var node in nodes)
        {
            Assert.Equal("In", node.Disposition);
            Assert.Equal(SoaResolution.Default, node.Resolution);
        }
    }

    [Fact]
    public void ScopeForAnotherStandardDoesNotLeak()
    {
        var scopes = new[] { new ScopeRow("s1", "In for other", "company", "other-std", "In") };

        var nodes = StatementOfApplicability.Resolve([Company, Department], scopes, [], [], "std");

        // A Scope for a different standard must not make THIS standard explicit or inherited; with no
        // Scope for "std" on the path, every node defaults In.
        Assert.All(nodes, n =>
        {
            Assert.Equal("In", n.Disposition);
            Assert.Equal(SoaResolution.Default, n.Resolution);
        });
    }

    [Fact]
    public void NodesOrderedById()
    {
        var unordered = new[] { Team, Company, Department };

        var nodes = StatementOfApplicability.Resolve(unordered, [], [], [], "std");

        Assert.Equal(["company", "company-dept", "company-dept-team"], nodes.Select(n => n.Id).ToArray());
    }

    private static readonly RequirementRow ReqA = new("req-a", "Requirement A", "std", "Theme", "S", null, "L", "https://example.com/a");
    private static readonly RequirementRow ReqB = new("req-b", "Requirement B", "std", "Theme", "S", null, "L", "https://example.com/b");

    [Fact]
    public void CompanyWideExclusionInheritedByDepartment()
    {
        var scopes = new[] { new ScopeRow("s1", "In at company", "company", "std", "In") };
        var requirementScopes = new[] { new RequirementScopeRow("rs1", "Exclude at company", "company", "req-a", "Out") };

        var nodes = StatementOfApplicability.Resolve([Company, Department], scopes, [ReqA], requirementScopes, "std");

        var company = nodes.Single(n => n.Id == "company");
        var companyReq = Assert.Single(company.Requirements);
        Assert.Equal("req-a", companyReq.Requirement);
        Assert.Equal("Out", companyReq.Disposition);
        Assert.Equal(SoaResolution.Explicit, companyReq.Resolution);

        var dept = nodes.Single(n => n.Id == "company-dept");
        var deptReq = Assert.Single(dept.Requirements);
        Assert.Equal("Out", deptReq.Disposition);
        Assert.Equal(SoaResolution.Inherited, deptReq.Resolution);
    }

    [Fact]
    public void DepartmentReincludeOverridesCompanyExclusion()
    {
        var scopes = new[] { new ScopeRow("s1", "In at company", "company", "std", "In") };
        var requirementScopes = new[]
        {
            new RequirementScopeRow("rs1", "Exclude at company", "company", "req-a", "Out"),
            new RequirementScopeRow("rs2", "Re-include at dept", "company-dept", "req-a", "In"),
        };

        var nodes = StatementOfApplicability.Resolve([Company, Department], scopes, [ReqA], requirementScopes, "std");

        var dept = nodes.Single(n => n.Id == "company-dept");
        var deptReq = Assert.Single(dept.Requirements);
        Assert.Equal("In", deptReq.Disposition);
        Assert.Equal(SoaResolution.Explicit, deptReq.Resolution);
    }

    [Fact]
    public void ExclusionIgnoredWhenStandardResolvesOut()
    {
        var scopes = new[] { new ScopeRow("s1", "Out at company", "company", "std", "Out") };
        var requirementScopes = new[] { new RequirementScopeRow("rs1", "Exclude", "company", "req-a", "Out") };

        var nodes = StatementOfApplicability.Resolve([Company, Department], scopes, [ReqA], requirementScopes, "std");

        // Standard Out dominates: requirement-scopes are not consulted, so no deviations listed.
        Assert.All(nodes, n => Assert.Empty(n.Requirements));
        Assert.Equal("Out", nodes.Single(n => n.Id == "company").Disposition);
    }

    [Fact]
    public void RequirementReincludeIgnoredWhenStandardResolvesOut()
    {
        var scopes = new[] { new ScopeRow("s1", "Out at company", "company", "std", "Out") };
        var requirementScopes = new[] { new RequirementScopeRow("rs1", "Re-include", "company", "req-a", "In") };

        var nodes = StatementOfApplicability.Resolve([Company, Department], scopes, [ReqA], requirementScopes, "std");

        // A requirement-level In cannot re-include a requirement whose standard resolves Out: the
        // standard Out dominates, so the requirement layer is not consulted and no deviations list.
        Assert.Equal("Out", nodes.Single(n => n.Id == "company").Disposition);
        Assert.All(nodes, n => Assert.Empty(n.Requirements));
    }

    [Fact]
    public void DefaultInNodeReportsRequirementDeviation()
    {
        // No standard scope anywhere, so the node defaults In; its requirement-scope Out is a reported
        // deviation.
        var requirementScopes = new[] { new RequirementScopeRow("rs1", "Exclude", "company", "req-a", "Out") };

        var nodes = StatementOfApplicability.Resolve([Company, Department], [], [ReqA], requirementScopes, "std");

        var company = nodes.Single(n => n.Id == "company");
        Assert.Equal("In", company.Disposition);
        Assert.Equal(SoaResolution.Default, company.Resolution);
        var companyReq = Assert.Single(company.Requirements);
        Assert.Equal("req-a", companyReq.Requirement);
        Assert.Equal("Out", companyReq.Disposition);
        Assert.Equal(SoaResolution.Explicit, companyReq.Resolution);
    }

    [Fact]
    public void DescendantInOverridesOptedOutAncestor()
    {
        var sibling = new OrganisationRow("company-other", "Other", "Department", "company");
        var scopes = new[]
        {
            new ScopeRow("s1", "Out at company", "company", "std", "Out"),
            new ScopeRow("s2", "In at dept", "company-dept", "std", "In"),
        };

        var nodes = StatementOfApplicability.Resolve([Company, Department, sibling], scopes, [], [], "std");

        var dept = nodes.Single(n => n.Id == "company-dept");
        Assert.Equal("In", dept.Disposition);
        Assert.Equal(SoaResolution.Explicit, dept.Resolution);

        var other = nodes.Single(n => n.Id == "company-other");
        Assert.Equal("Out", other.Disposition);
        Assert.Equal(SoaResolution.Inherited, other.Resolution);
    }

    [Fact]
    public void PerRequirementListOrderedByRequirementId()
    {
        var scopes = new[] { new ScopeRow("s1", "In at company", "company", "std", "In") };
        var requirementScopes = new[]
        {
            new RequirementScopeRow("rs2", "Exclude b", "company", "req-b", "Out"),
            new RequirementScopeRow("rs1", "Exclude a", "company", "req-a", "Out"),
        };

        var nodes = StatementOfApplicability.Resolve([Company], scopes, [ReqB, ReqA], requirementScopes, "std");

        var company = Assert.Single(nodes);
        Assert.Equal(["req-a", "req-b"], company.Requirements.Select(r => r.Requirement).ToArray());
    }

    [Fact]
    public void RequirementScopeOfAnotherStandardIsAbsent()
    {
        var scopes = new[] { new ScopeRow("s1", "In at company", "company", "std", "In") };
        // ReqOther belongs to another standard; its requirement-scope must not appear for "std".
        var reqOther = new RequirementRow("req-other", "Other", "other-std", "Theme", "S", null, "L", "https://example.com/o");
        var requirementScopes = new[] { new RequirementScopeRow("rs1", "Exclude other", "company", "req-other", "Out") };

        var nodes = StatementOfApplicability.Resolve([Company], scopes, [ReqA, reqOther], requirementScopes, "std");

        Assert.Empty(Assert.Single(nodes).Requirements);
    }

    [Fact]
    public void ChildReincludesStandardThenInheritsParentRequirementExclusion()
    {
        // Parent resolves the standard Out; child re-scopes the standard In. The parent carries a
        // requirement-scope Out for req-a. Under the child's own In standard, the child inherits that
        // requirement-scope Out (marked inherited), while the parent lists no per-requirement
        // exclusions (its standard is Out, so the requirement layer is not consulted).
        var scopes = new[]
        {
            new ScopeRow("s1", "Out at company", "company", "std", "Out"),
            new ScopeRow("s2", "In at dept", "company-dept", "std", "In"),
        };
        var requirementScopes = new[] { new RequirementScopeRow("rs1", "Exclude at company", "company", "req-a", "Out") };

        var nodes = StatementOfApplicability.Resolve([Company, Department], scopes, [ReqA], requirementScopes, "std");

        var company = nodes.Single(n => n.Id == "company");
        Assert.Equal("Out", company.Disposition);
        Assert.Empty(company.Requirements);

        var dept = nodes.Single(n => n.Id == "company-dept");
        Assert.Equal("In", dept.Disposition);
        var deptReq = Assert.Single(dept.Requirements);
        Assert.Equal("Out", deptReq.Disposition);
        Assert.Equal(SoaResolution.Inherited, deptReq.Resolution);
    }
}
