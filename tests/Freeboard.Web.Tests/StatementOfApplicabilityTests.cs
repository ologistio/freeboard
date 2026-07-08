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

    private static ControlRow Ctrl(string id, string[] mapsTo, string? evaluation = null) =>
        new(id, "Control " + id, mapsTo, evaluation);

    private static EvidenceCollectorRow Coll(string id, string control, string? vendor = null, string type = "integration", string frequency = "daily") =>
        new(id, "Collector " + id, control, vendor, type, frequency, null, new Dictionary<string, string>());

    private static AttestationTemplateRow Tmpl(string id, string control, string type = "manual") =>
        new(id, "Template " + id, control, type, null, [], null, []);

    [Fact]
    public void DrilldownEnumeratesEveryRequirementTaggedInOrOutAndExcludedIsLeaf()
    {
        var scopes = new[] { new ScopeRow("s1", "In at company", "company", "std", "In") };
        // req-a is excluded explicitly; req-b has no requirement-scope so it defaults In.
        var requirementScopes = new[] { new RequirementScopeRow("rs1", "Exclude a", "company", "req-a", "Out") };
        // A control maps to each requirement, so the excluded requirement's leaf behaviour is provable.
        var controls = new[] { Ctrl("ctrl-a", ["req-a"]), Ctrl("ctrl-b", ["req-b"]) };

        var nodes = StatementOfApplicability.ResolveDrilldown(
            [Company, Department], scopes, [ReqA, ReqB], requirementScopes, controls, [], [], [], "std");

        // The node lists every requirement of the standard (In and Out), ordered by id, not only the deviation.
        var company = nodes.Single(n => n.Id == "company");
        Assert.Equal(["req-a", "req-b"], company.Requirements.Select(r => r.Id).ToArray());

        var reqA = company.Requirements.Single(r => r.Id == "req-a");
        Assert.Equal("Out", reqA.Disposition);
        Assert.Equal(SoaResolution.Explicit, reqA.Resolution);
        // An excluded (Out) requirement is a leaf: no controls even though ctrl-a maps to it.
        Assert.Empty(reqA.Controls);

        var reqB = company.Requirements.Single(r => r.Id == "req-b");
        Assert.Equal("In", reqB.Disposition);
        Assert.Equal(SoaResolution.Default, reqB.Resolution);
        // An In requirement still carries its mapped controls.
        Assert.Equal("ctrl-b", Assert.Single(reqB.Controls).Id);

        // The department inherits the company's req-a exclusion, and it is still a leaf.
        var deptReqA = nodes.Single(n => n.Id == "company-dept").Requirements.Single(r => r.Id == "req-a");
        Assert.Equal("Out", deptReqA.Disposition);
        Assert.Equal(SoaResolution.Inherited, deptReqA.Resolution);
        Assert.Empty(deptReqA.Controls);
    }

    [Fact]
    public void DrilldownStandardOutYieldsNoRequirementChildren()
    {
        var scopes = new[] { new ScopeRow("s1", "Out at company", "company", "std", "Out") };

        var nodes = StatementOfApplicability.ResolveDrilldown(
            [Company], scopes, [ReqA, ReqB], [], [], [], [], [], "std");

        Assert.Empty(Assert.Single(nodes).Requirements);
    }

    [Fact]
    public void DrilldownAttachesControlsByMapsToAndChecksByControl()
    {
        var controls = new[]
        {
            Ctrl("ctrl-a", ["req-a"], evaluation: "all"),
            Ctrl("ctrl-b", ["req-b"]),
        };
        var collectors = new[] { Coll("coll-a", "ctrl-a", vendor: "vendor-x") };
        var templates = new[] { Tmpl("tmpl-a", "ctrl-a") };
        var vendors = new[] { new VendorRow("vendor-x", "Vendor X") };

        var nodes = StatementOfApplicability.ResolveDrilldown(
            [Company], [], [ReqA, ReqB], [], controls, collectors, templates, vendors, "std");

        var company = Assert.Single(nodes);
        var reqA = company.Requirements.Single(r => r.Id == "req-a");
        var control = Assert.Single(reqA.Controls);
        Assert.Equal("ctrl-a", control.Id);
        Assert.Equal("all", control.Evaluation);

        // Both check kinds present and tagged; collector carries type/frequency and its vendor by title,
        // attestation does not.
        Assert.Equal(["coll-a", "tmpl-a"], control.Checks.Select(c => c.Id).ToArray());
        var coll = control.Checks[0];
        Assert.Equal(SoaCheckKind.Collector, coll.Kind);
        Assert.Equal("integration", coll.Type);
        Assert.Equal("daily", coll.Frequency);
        Assert.Equal("Vendor X", coll.Vendor);
        var tmpl = control.Checks[1];
        Assert.Equal(SoaCheckKind.Attestation, tmpl.Kind);
        Assert.Null(tmpl.Frequency);
        Assert.Null(tmpl.Vendor);

        // req-b maps only to ctrl-b, which has no checks.
        var reqB = company.Requirements.Single(r => r.Id == "req-b");
        Assert.Equal("ctrl-b", Assert.Single(reqB.Controls).Id);
        Assert.Empty(reqB.Controls[0].Checks);
    }

    [Fact]
    public void DrilldownOrdersControlsByIdAndChecksByKindThenId()
    {
        var controls = new[]
        {
            Ctrl("ctrl-b", ["req-a"]),
            Ctrl("ctrl-a", ["req-a"]),
        };
        // Two collectors and two templates on ctrl-a, seeded out of order to prove the sort.
        var collectors = new[] { Coll("coll-b", "ctrl-a"), Coll("coll-a", "ctrl-a") };
        var templates = new[] { Tmpl("tmpl-b", "ctrl-a"), Tmpl("tmpl-a", "ctrl-a") };

        var nodes = StatementOfApplicability.ResolveDrilldown(
            [Company], [], [ReqA], [], controls, collectors, templates, [], "std");

        var reqA = Assert.Single(Assert.Single(nodes).Requirements);
        Assert.Equal(["ctrl-a", "ctrl-b"], reqA.Controls.Select(c => c.Id).ToArray());

        var ctrlA = reqA.Controls.Single(c => c.Id == "ctrl-a");
        // Collectors (by id) before attestations (by id).
        Assert.Equal(["coll-a", "coll-b", "tmpl-a", "tmpl-b"], ctrlA.Checks.Select(c => c.Id).ToArray());
    }

    [Fact]
    public void DrilldownRequirementWithNoMappedControlHasEmptyControls()
    {
        var nodes = StatementOfApplicability.ResolveDrilldown(
            [Company], [], [ReqA], [], [], [], [], [], "std");

        var reqA = Assert.Single(Assert.Single(nodes).Requirements);
        Assert.Empty(reqA.Controls);
    }

    [Fact]
    public void DrilldownVendorIsMetadataAndFallsBackToIdWhenUnknown()
    {
        var controls = new[] { Ctrl("ctrl-a", ["req-a"]) };
        var collectors = new[] { Coll("coll-a", "ctrl-a", vendor: "vendor-x") };

        // No matching vendor row, so the display falls back to the raw id.
        var nodes = StatementOfApplicability.ResolveDrilldown(
            [Company], [], [ReqA], [], controls, collectors, [], [], "std");

        // The collector's vendor is carried as metadata; the requirement still resolves In (default).
        var reqA = Assert.Single(Assert.Single(nodes).Requirements);
        Assert.Equal("In", reqA.Disposition);
        Assert.Equal(SoaResolution.Default, reqA.Resolution);
        Assert.Equal("vendor-x", reqA.Controls[0].Checks[0].Vendor);
    }
}
