using Freeboard.Compliance;
using Freeboard.Persistence;

namespace Freeboard.Web.Tests;

public sealed class StatementOfApplicabilityTests
{
    private static readonly AssetNode Company = TestAssets.Org("company", title: "Company");
    private static readonly AssetNode Department = TestAssets.Org("company-dept", "company", "Department", "Department");
    private static readonly AssetNode Team = TestAssets.Org("company-dept-team", "company-dept", "Department", "Team");

    // A standard-target scope in the unified shape (standard set, requirement/control null).
    private static ScopeRow Std(string id, string title, string subject, string standard, string disposition) =>
        new(id, title, subject, standard, null, null, disposition, null);

    // A requirement-target scope in the unified shape (requirement set, standard/control null).
    private static ScopeRow Req(string id, string title, string subject, string requirement, string disposition) =>
        new(id, title, subject, null, requirement, null, disposition, disposition == "Out" ? "reason" : null);

    private static IReadOnlySet<string> Accessible(params string[] ids) => ids.ToHashSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> NoneAccessible = Accessible();

    [Fact]
    public void AssetLeafDispositionWinsOverAncestors()
    {
        var scopes = new[]
        {
            Std("s1", "In at company", "company", "std", "In"),
            Std("s2", "Out at dept", "company-dept", "std", "Out"),
        };

        var nodes = StatementOfApplicability.Resolve([Company, Department], scopes, [], "std");

        var dept = nodes.Single(n => n.Id == "company-dept");
        Assert.Equal("Out", dept.Disposition);
        Assert.Equal(SoaResolution.Asset, dept.Resolution);
    }

    [Fact]
    public void ChildInheritsNearestAncestor()
    {
        var scopes = new[] { Std("s1", "In at company", "company", "std", "In") };

        var nodes = StatementOfApplicability.Resolve([Company, Department, Team], scopes, [], "std");

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
            Std("s1", "In at company", "company", "std", "In"),
            Std("s2", "Out at dept", "company-dept", "std", "Out"),
        };

        var nodes = StatementOfApplicability.Resolve([Company, Department, Team], scopes, [], "std");

        var team = nodes.Single(n => n.Id == "company-dept-team");
        Assert.Equal("Out", team.Disposition);
        Assert.Equal(SoaResolution.Inherited, team.Resolution);
    }

    [Fact]
    public void NoScopeOnPathDefaultsIn()
    {
        var nodes = StatementOfApplicability.Resolve([Company, Department], [], [], "std");

        foreach (var node in nodes)
        {
            Assert.Equal("In", node.Disposition);
            Assert.Equal(SoaResolution.Default, node.Resolution);
        }
    }

    [Fact]
    public void ScopeForAnotherStandardDoesNotLeak()
    {
        var scopes = new[] { Std("s1", "In for other", "company", "other-std", "In") };

        var nodes = StatementOfApplicability.Resolve([Company, Department], scopes, [], "std");

        // A Scope for a different standard must not resolve THIS standard as asset or inherited; with no
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

        var nodes = StatementOfApplicability.Resolve(unordered, [], [], "std");

        Assert.Equal(["company", "company-dept", "company-dept-team"], nodes.Select(n => n.Id).ToArray());
    }

    private static readonly RequirementRow ReqA = new("req-a", "Requirement A", "std", "Theme", "S", null, "L", "https://example.com/a");
    private static readonly RequirementRow ReqB = new("req-b", "Requirement B", "std", "Theme", "S", null, "L", "https://example.com/b");

    [Fact]
    public void CompanyWideExclusionInheritedByDepartment()
    {
        var scopes = new[]
        {
            Std("s1", "In at company", "company", "std", "In"),
            Req("rs1", "Exclude at company", "company", "req-a", "Out"),
        };

        var nodes = StatementOfApplicability.Resolve([Company, Department], scopes, [ReqA], "std");

        var company = nodes.Single(n => n.Id == "company");
        var companyReq = Assert.Single(company.Requirements);
        Assert.Equal("req-a", companyReq.Requirement);
        Assert.Equal("Out", companyReq.Disposition);
        Assert.Equal(SoaResolution.Asset, companyReq.Resolution);

        var dept = nodes.Single(n => n.Id == "company-dept");
        var deptReq = Assert.Single(dept.Requirements);
        Assert.Equal("Out", deptReq.Disposition);
        Assert.Equal(SoaResolution.Inherited, deptReq.Resolution);
    }

    [Fact]
    public void DepartmentReincludeOverridesCompanyExclusion()
    {
        var scopes = new[]
        {
            Std("s1", "In at company", "company", "std", "In"),
            Req("rs1", "Exclude at company", "company", "req-a", "Out"),
            Req("rs2", "Re-include at dept", "company-dept", "req-a", "In"),
        };

        var nodes = StatementOfApplicability.Resolve([Company, Department], scopes, [ReqA], "std");

        var dept = nodes.Single(n => n.Id == "company-dept");
        var deptReq = Assert.Single(dept.Requirements);
        Assert.Equal("In", deptReq.Disposition);
        Assert.Equal(SoaResolution.Asset, deptReq.Resolution);
    }

    [Fact]
    public void ExclusionIgnoredWhenStandardResolvesOut()
    {
        var scopes = new[]
        {
            Std("s1", "Out at company", "company", "std", "Out"),
            Req("rs1", "Exclude", "company", "req-a", "Out"),
        };

        var nodes = StatementOfApplicability.Resolve([Company, Department], scopes, [ReqA], "std");

        // Standard Out dominates: requirement-target scopes are not consulted, so no deviations listed.
        Assert.All(nodes, n => Assert.Empty(n.Requirements));
        Assert.Equal("Out", nodes.Single(n => n.Id == "company").Disposition);
    }

    [Fact]
    public void RequirementReincludeIgnoredWhenStandardResolvesOut()
    {
        var scopes = new[]
        {
            Std("s1", "Out at company", "company", "std", "Out"),
            Req("rs1", "Re-include", "company", "req-a", "In"),
        };

        var nodes = StatementOfApplicability.Resolve([Company, Department], scopes, [ReqA], "std");

        // A requirement-level In cannot re-include a requirement whose standard resolves Out: the
        // standard Out dominates, so the requirement layer is not consulted and no deviations list.
        Assert.Equal("Out", nodes.Single(n => n.Id == "company").Disposition);
        Assert.All(nodes, n => Assert.Empty(n.Requirements));
    }

    [Fact]
    public void DefaultInNodeReportsRequirementDeviation()
    {
        // No standard scope anywhere, so the node defaults In; its requirement-target scope Out is a
        // reported deviation.
        var scopes = new[] { Req("rs1", "Exclude", "company", "req-a", "Out") };

        var nodes = StatementOfApplicability.Resolve([Company, Department], scopes, [ReqA], "std");

        var company = nodes.Single(n => n.Id == "company");
        Assert.Equal("In", company.Disposition);
        Assert.Equal(SoaResolution.Default, company.Resolution);
        var companyReq = Assert.Single(company.Requirements);
        Assert.Equal("req-a", companyReq.Requirement);
        Assert.Equal("Out", companyReq.Disposition);
        Assert.Equal(SoaResolution.Asset, companyReq.Resolution);
    }

    [Fact]
    public void DescendantInOverridesOptedOutAncestor()
    {
        var sibling = TestAssets.Org("company-other", "company", "Department", "Other");
        var scopes = new[]
        {
            Std("s1", "Out at company", "company", "std", "Out"),
            Std("s2", "In at dept", "company-dept", "std", "In"),
        };

        var nodes = StatementOfApplicability.Resolve([Company, Department, sibling], scopes, [], "std");

        var dept = nodes.Single(n => n.Id == "company-dept");
        Assert.Equal("In", dept.Disposition);
        Assert.Equal(SoaResolution.Asset, dept.Resolution);

        var other = nodes.Single(n => n.Id == "company-other");
        Assert.Equal("Out", other.Disposition);
        Assert.Equal(SoaResolution.Inherited, other.Resolution);
    }

    [Fact]
    public void PerRequirementListOrderedByRequirementId()
    {
        var scopes = new[]
        {
            Std("s1", "In at company", "company", "std", "In"),
            Req("rs2", "Exclude b", "company", "req-b", "Out"),
            Req("rs1", "Exclude a", "company", "req-a", "Out"),
        };

        var nodes = StatementOfApplicability.Resolve([Company], scopes, [ReqB, ReqA], "std");

        var company = Assert.Single(nodes);
        Assert.Equal(["req-a", "req-b"], company.Requirements.Select(r => r.Requirement).ToArray());
    }

    [Fact]
    public void RequirementScopeOfAnotherStandardIsAbsent()
    {
        // ReqOther belongs to another standard; its requirement-target scope must not appear for "std".
        var reqOther = new RequirementRow("req-other", "Other", "other-std", "Theme", "S", null, "L", "https://example.com/o");
        var scopes = new[]
        {
            Std("s1", "In at company", "company", "std", "In"),
            Req("rs1", "Exclude other", "company", "req-other", "Out"),
        };

        var nodes = StatementOfApplicability.Resolve([Company], scopes, [ReqA, reqOther], "std");

        Assert.Empty(Assert.Single(nodes).Requirements);
    }

    [Fact]
    public void ChildReincludesStandardThenInheritsParentRequirementExclusion()
    {
        // Parent resolves the standard Out; child re-scopes the standard In. The parent carries a
        // requirement-target scope Out for req-a. Under the child's own In standard, the child inherits
        // that requirement-target Out (marked inherited), while the parent lists no per-requirement
        // exclusions (its standard is Out, so the requirement layer is not consulted).
        var scopes = new[]
        {
            Std("s1", "Out at company", "company", "std", "Out"),
            Std("s2", "In at dept", "company-dept", "std", "In"),
            Req("rs1", "Exclude at company", "company", "req-a", "Out"),
        };

        var nodes = StatementOfApplicability.Resolve([Company, Department], scopes, [ReqA], "std");

        var company = nodes.Single(n => n.Id == "company");
        Assert.Equal("Out", company.Disposition);
        Assert.Empty(company.Requirements);

        var dept = nodes.Single(n => n.Id == "company-dept");
        Assert.Equal("In", dept.Disposition);
        var deptReq = Assert.Single(dept.Requirements);
        Assert.Equal("Out", deptReq.Disposition);
        Assert.Equal(SoaResolution.Inherited, deptReq.Resolution);
    }

    // The node set is every organisation unconditionally, plus every other asset whose inclusive parent
    // chain reaches one. A mixed declared/discovered machine tree exercises both arms at once.
    private static readonly AssetNode DeclaredMachine = TestAssets.Machine("m-declared", "company-dept");
    private static readonly AssetNode DiscoveredMachine =
        TestAssets.Machine("m-discovered", "company-dept", source: "discovered", state: "Seen");

    private static IReadOnlyList<AssetNode> MixedTree() =>
        [Company, Department, DeclaredMachine, DiscoveredMachine];

    [Fact]
    public void MachinesResolveAssetInheritedAndDefaultOverAMixedTree()
    {
        // company: In at itself (asset). dept and the discovered machine inherit it. The declared
        // machine carries its own leaf scope, which wins over the inherited value.
        var scopes = new[]
        {
            Std("s1", "In at company", "company", "std", "In"),
            Std("s2", "Out at the declared machine", "m-declared", "std", "Out"),
        };

        var nodes = StatementOfApplicability.Resolve(MixedTree(), scopes, [], "std");

        Assert.Equal(
            ["company", "company-dept", "m-declared", "m-discovered"], nodes.Select(n => n.Id).ToArray());
        Assert.Equal(("In", SoaResolution.Asset), Disposition(nodes, "company"));
        Assert.Equal(("In", SoaResolution.Inherited), Disposition(nodes, "company-dept"));
        Assert.Equal(("Out", SoaResolution.Asset), Disposition(nodes, "m-declared"));
        Assert.Equal(("In", SoaResolution.Inherited), Disposition(nodes, "m-discovered"));
    }

    [Fact]
    public void EveryNodeOfAScopelessMixedTreeDefaultsIn()
    {
        var nodes = StatementOfApplicability.Resolve(MixedTree(), [], [], "std");

        Assert.All(nodes, n => Assert.Equal(("In", SoaResolution.Default), (n.Disposition, n.Resolution)));
        Assert.Equal(4, nodes.Count);
    }

    [Fact]
    public void MachineCarriesItsTypeAndParentOntoTheNode()
    {
        var node = StatementOfApplicability.Resolve(MixedTree(), [], [], "std").Single(n => n.Id == "m-discovered");

        Assert.Equal("Machine", node.Kind);
        Assert.Equal("company-dept", node.Parent);
    }

    [Fact]
    public void RequirementLayerIsSuppressedUnderAnOutStandardAndOverridableAtAMachine()
    {
        var scopes = new[]
        {
            Std("s1", "Out at company", "company", "std", "Out"),
            Std("s2", "In at the declared machine", "m-declared", "std", "In"),
            Req("rs1", "Exclude req-a at company", "company", "req-a", "Out"),
            Req("rs2", "Re-include req-a at the machine", "m-declared", "req-a", "In"),
        };

        var nodes = StatementOfApplicability.Resolve(MixedTree(), scopes, [ReqA], "std");

        // Every Out node lists no requirement deviations at all: the layer is not consulted.
        Assert.Empty(nodes.Single(n => n.Id == "company").Requirements);
        Assert.Empty(nodes.Single(n => n.Id == "company-dept").Requirements);
        Assert.Empty(nodes.Single(n => n.Id == "m-discovered").Requirements);

        // The machine re-scopes the standard In, so its own requirement leaf resolves there.
        var machineReq = Assert.Single(nodes.Single(n => n.Id == "m-declared").Requirements);
        Assert.Equal("req-a", machineReq.Requirement);
        Assert.Equal("In", machineReq.Disposition);
        Assert.Equal(SoaResolution.Asset, machineReq.Resolution);
    }

    [Fact]
    public void AVendorIsNotANode()
    {
        // A vendor is neither organisation-typed nor parent-carrying, so it satisfies neither arm and
        // falls out with no vendor-specific branch.
        IReadOnlyList<AssetNode> assets = [Company, TestAssets.Vendor("vendor-x", "company")];

        Assert.Equal(["company"], StatementOfApplicability.Resolve(assets, [], [], "std").Select(n => n.Id).ToArray());
    }

    [Fact]
    public void AnUnrootedOrDanglingParentMachineIsNotANode()
    {
        IReadOnlyList<AssetNode> assets =
        [
            Company,
            TestAssets.Machine("m-unrooted", null),
            TestAssets.Machine("m-dangling", "gone-org"),
            TestAssets.Machine("m-rooted", "company"),
        ];

        Assert.Equal(
            ["company", "m-rooted"],
            StatementOfApplicability.Resolve(assets, [], [], "std").Select(n => n.Id).ToArray());
    }

    [Fact]
    public void AnOrganisationWithADanglingParentIsStillANodeResolvingTheDefaultIn()
    {
        // A dangling parent is a non-blocking sync warning, so the organisation must not drop out of
        // the projection, out of the ingest gate, or out of the default In the scoping model grants it.
        IReadOnlyList<AssetNode> assets = [TestAssets.Org("org-mid", "gone-org")];

        var node = Assert.Single(StatementOfApplicability.Resolve(assets, [], [], "std"));
        Assert.Equal("org-mid", node.Id);
        Assert.Equal(("In", SoaResolution.Default), (node.Disposition, node.Resolution));
    }

    [Fact]
    public void TwoOrganisationsInAParentCycleBothResolveAndTheNodeSetIsFinite()
    {
        IReadOnlyList<AssetNode> assets = [TestAssets.Org("org-c1", "org-c2"), TestAssets.Org("org-c2", "org-c1")];

        var nodes = StatementOfApplicability.Resolve(assets, [], [], "std");

        Assert.Equal(["org-c1", "org-c2"], nodes.Select(n => n.Id).ToArray());
        Assert.All(nodes, n => Assert.Equal(("In", SoaResolution.Default), (n.Disposition, n.Resolution)));
    }

    [Fact]
    public void AMachineUnderACycleIsANodeAndInheritsThroughIt()
    {
        IReadOnlyList<AssetNode> assets =
        [
            TestAssets.Org("org-c1", "org-c2"), TestAssets.Org("org-c2", "org-c1"),
            TestAssets.Machine("m-cyc", "org-c1"),
        ];
        var scopes = new[] { Std("s1", "Out at c2", "org-c2", "std", "Out") };

        var nodes = StatementOfApplicability.Resolve(assets, scopes, [], "std");

        Assert.Equal(("Out", SoaResolution.Inherited), Disposition(nodes, "m-cyc"));
    }

    [Fact]
    public void DrilldownNodeListIsOrganisationOnly()
    {
        var nodes = StatementOfApplicability.ResolveDrilldown(
            MixedTree(), [], [ReqA], [], [], NoneAccessible, "std");

        Assert.Equal(["company", "company-dept"], nodes.Select(n => n.Id).ToArray());
    }

    private static (string, SoaResolution) Disposition(IReadOnlyList<SoaNode> nodes, string id)
    {
        var node = nodes.Single(n => n.Id == id);
        return (node.Disposition, node.Resolution);
    }

    private static ControlRow Ctrl(string id, string[] mapsTo, string? evaluation = null) =>
        new(id, "Control " + id, mapsTo, evaluation);

    private static CollectorRow Coll(string id, string control, string? vendor = null, string type = "integration", string frequency = "daily") =>
        new(id, "Collector " + id, control, vendor, type, type == "integration" ? "fleet" : null, frequency, null, CollectorConfigView.Empty);

    // An attestation-tagged check: a manual or training collector. It carries a frequency like any other
    // collector - the projection is what drops it.
    private static CollectorRow Attest(
        string id, string control, string? vendor = null, string type = "manual", string frequency = "annual") =>
        Coll(id, control, vendor: vendor, type: type, frequency: frequency);

    [Fact]
    public void DrilldownEnumeratesEveryRequirementTaggedInOrOutAndExcludedIsLeaf()
    {
        // req-a is excluded explicitly; req-b has no requirement-target scope so it defaults In.
        var scopes = new[]
        {
            Std("s1", "In at company", "company", "std", "In"),
            Req("rs1", "Exclude a", "company", "req-a", "Out"),
        };
        // A control maps to each requirement, so the excluded requirement's leaf behaviour is provable.
        var controls = new[] { Ctrl("ctrl-a", ["req-a"]), Ctrl("ctrl-b", ["req-b"]) };

        var nodes = StatementOfApplicability.ResolveDrilldown(
            [Company, Department], scopes, [ReqA, ReqB], controls, [], NoneAccessible, "std");

        // The node lists every requirement of the standard (In and Out), ordered by id, not only the deviation.
        var company = nodes.Single(n => n.Id == "company");
        Assert.Equal(["req-a", "req-b"], company.Requirements.Select(r => r.Id).ToArray());

        var reqA = company.Requirements.Single(r => r.Id == "req-a");
        Assert.Equal("Out", reqA.Disposition);
        Assert.Equal(SoaResolution.Asset, reqA.Resolution);
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
        var scopes = new[] { Std("s1", "Out at company", "company", "std", "Out") };

        var nodes = StatementOfApplicability.ResolveDrilldown(
            [Company], scopes, [ReqA, ReqB], [], [], NoneAccessible, "std");

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
        var collectors = new[]
        {
            Coll("coll-a", "ctrl-a", vendor: "vendor-x"),
            Attest("tmpl-a", "ctrl-a", vendor: "vendor-x"),
        };
        var nodes = StatementOfApplicability.ResolveDrilldown(
            [Company, TestAssets.Vendor("vendor-x", "company", title: "Vendor X")],
            [], [ReqA, ReqB], controls, collectors, Accessible("vendor-x"), "std");

        var company = Assert.Single(nodes);
        var reqA = company.Requirements.Single(r => r.Id == "req-a");
        var control = Assert.Single(reqA.Controls);
        Assert.Equal("ctrl-a", control.Id);
        Assert.Equal("all", control.Evaluation);

        // Both check kinds present and tagged from the collector's type; a collector-tagged check carries
        // its type, frequency, and vendor by title, an attestation-tagged one carries no cadence.
        Assert.Equal(["coll-a", "tmpl-a"], control.Checks.Select(c => c.Id).ToArray());
        var coll = control.Checks[0];
        Assert.Equal(SoaCheckKind.Collector, coll.Kind);
        Assert.Equal("integration", coll.Type);
        Assert.Equal("daily", coll.Frequency);
        Assert.Equal("Vendor X", coll.Vendor);
        var tmpl = control.Checks[1];
        Assert.Equal(SoaCheckKind.Attestation, tmpl.Kind);
        // The attestation collector has an "annual" frequency of its own; the projection drops it,
        // because only a collector-tagged check carries an evidence status to back a cadence.
        Assert.Null(tmpl.Frequency);
        // The vendor is NOT dropped with the cadence: it plays no part in status interpretation, so both
        // tags carry it, resolved to the same title.
        Assert.Equal("Vendor X", tmpl.Vendor);

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
        // Two data-source and two attestation collectors on ctrl-a, seeded out of order to prove the sort.
        var collectors = new[]
        {
            Coll("coll-b", "ctrl-a"), Coll("coll-a", "ctrl-a"),
            Attest("tmpl-b", "ctrl-a"), Attest("tmpl-a", "ctrl-a"),
        };

        var nodes = StatementOfApplicability.ResolveDrilldown(
            [Company], [], [ReqA], controls, collectors, NoneAccessible, "std");

        var reqA = Assert.Single(Assert.Single(nodes).Requirements);
        Assert.Equal(["ctrl-a", "ctrl-b"], reqA.Controls.Select(c => c.Id).ToArray());

        var ctrlA = reqA.Controls.Single(c => c.Id == "ctrl-a");
        // Collectors (by id) before attestations (by id).
        Assert.Equal(["coll-a", "coll-b", "tmpl-a", "tmpl-b"], ctrlA.Checks.Select(c => c.Id).ToArray());
    }

    // The derived tag, not the id, decides the group under the kind-then-id sort: a manual collector
    // tags as an attestation and sorts after every collector-tagged check even when its id sorts first.
    [Fact]
    public void DerivedTagReordersAManualCollectorWhoseIdSortsFirst()
    {
        var controls = new[] { Ctrl("ctrl-a", ["req-a"]) };
        var collectors = new[] { Attest("a-manual", "ctrl-a"), Coll("z-integration", "ctrl-a") };

        var nodes = StatementOfApplicability.ResolveDrilldown(
            [Company], [], [ReqA], controls, collectors, NoneAccessible, "std");

        var ctrlA = Assert.Single(Assert.Single(Assert.Single(nodes).Requirements).Controls);
        Assert.Equal(["z-integration", "a-manual"], ctrlA.Checks.Select(c => c.Id).ToArray());
    }

    [Fact]
    public void DrilldownRequirementWithNoMappedControlHasEmptyControls()
    {
        var nodes = StatementOfApplicability.ResolveDrilldown(
            [Company], [], [ReqA], [], [], NoneAccessible, "std");

        var reqA = Assert.Single(Assert.Single(nodes).Requirements);
        Assert.Empty(reqA.Controls);
    }

    // The drill-down renders a vendor by title and never by id. The three cases a caller can hit -
    // readable, unreadable, and absent - must be indistinguishable except for the title itself.
    [Fact]
    public void DrilldownRendersAReadableVendorTitle()
    {
        var vendor = TestAssets.Vendor("vendor-x", "company", title: "Vendor X");
        var check = DrilldownCheck([Company, vendor], Accessible("vendor-x"));

        Assert.Equal("Vendor X", check.Vendor);
    }

    [Fact]
    public void DrilldownRendersAnUnreadableVendorExactlyAsAnUnsetOne()
    {
        // The vendor asset exists and its title is in the snapshot, but the caller cannot read it: the
        // check must carry no vendor at all rather than the id the title lookup would fall back to.
        var vendor = TestAssets.Vendor("vendor-x", "elsewhere", title: "Vendor X");
        var check = DrilldownCheck([Company, vendor], NoneAccessible);

        Assert.Null(check.Vendor);
    }

    [Fact]
    public void DrilldownRendersAnAbsentVendorExactlyAsAnUnsetOne()
    {
        var check = DrilldownCheck([Company], Accessible("vendor-x"));

        Assert.Null(check.Vendor);
    }

    [Fact]
    public void DrilldownNeverRendersARawVendorId()
    {
        // Whatever the vendor's state, no rendered vendor value equals the id.
        foreach (var (assets, accessible) in new (IReadOnlyList<AssetNode>, IReadOnlySet<string>)[]
        {
            ([Company, TestAssets.Vendor("vendor-x", "company", title: "Vendor X")], Accessible("vendor-x")),
            ([Company, TestAssets.Vendor("vendor-x", "elsewhere", title: "Vendor X")], NoneAccessible),
            ([Company, TestAssets.Vendor("vendor-x", null, title: "Vendor X")], NoneAccessible),
            ([Company], NoneAccessible),
        })
        {
            Assert.NotEqual("vendor-x", DrilldownCheck(assets, accessible).Vendor);
        }
    }

    private static SoaCheckNode DrilldownCheck(IReadOnlyList<AssetNode> assets, IReadOnlySet<string> accessible)
    {
        var nodes = StatementOfApplicability.ResolveDrilldown(
            assets, [], [ReqA], [Ctrl("ctrl-a", ["req-a"])], [Coll("coll-a", "ctrl-a", vendor: "vendor-x")],
            accessible, "std");

        return Assert.Single(Assert.Single(Assert.Single(nodes).Requirements).Controls).Checks[0];
    }

    [Fact]
    public void HasDanglingSubjectTrueWhenAnySubjectUnresolvedAcrossEveryTargetKind()
    {
        // A control-target org scope whose subject resolves to no asset warns, even though the SoA does
        // not resolve a control-level disposition for it.
        var scopes = new[]
        {
            Std("s1", "In at company", "company", "std", "In"),
            new ScopeRow("s2", "Control target, dangling subject", "ghost", null, null, "ctrl-a", "In", null),
        };

        Assert.True(StatementOfApplicability.HasDanglingSubject(scopes, [Company]));
    }

    [Fact]
    public void HasDanglingSubjectFalseWhenEverySubjectResolves()
    {
        var scopes = new[]
        {
            Std("s1", "In at company", "company", "std", "In"),
            new ScopeRow("s2", "Vendor control", "vendor-x", null, null, "ctrl-a", "In", null),
        };

        Assert.False(StatementOfApplicability.HasDanglingSubject(
            scopes, [Company, TestAssets.Vendor("vendor-x", "company")]));
    }

    [Fact]
    public void HasDanglingSubjectTreatsARetiredDiscoveredAssetAsNotLive()
    {
        var scopes = new[] { new ScopeRow("s1", "Retired machine", "m-1", null, null, "ctrl-a", "In", null) };

        Assert.True(StatementOfApplicability.HasDanglingSubject(
            scopes, [Company, TestAssets.Machine("m-1", "company", source: "discovered", state: "Retired")]));
        Assert.False(StatementOfApplicability.HasDanglingSubject(
            scopes, [Company, TestAssets.Machine("m-1", "company", source: "discovered", state: "Seen")]));
    }
}
