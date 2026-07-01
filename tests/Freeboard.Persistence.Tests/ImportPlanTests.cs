using Freeboard.Core.GitOps;
using Freeboard.Persistence.GitOps;

namespace Freeboard.Persistence.Tests;

public sealed class ImportPlanTests
{
    private static GitOpsConfig SampleConfig() => new()
    {
        Standards = [new Standard { Id = "std-a", ApiVersion = "v1", Title = "Standard A" }],
        Controls =
        [
            new Control { Id = "ctrl-a", ApiVersion = "v1", Title = "Control A", MapsTo = ["std-a"] },
        ],
        Organisations =
        [
            new Organisation { Id = "org-a", ApiVersion = "v1", Title = "Org A", OrgKind = "Company" },
        ],
        Scopes =
        [
            new Scope
            {
                Id = "scope-a",
                ApiVersion = "v1",
                Title = "Scope A",
                Organisation = "org-a",
                Standard = "std-a",
                Disposition = "In",
            },
        ],
    };

    [Fact]
    public void PlanKeysDomainRowsOnId()
    {
        var plan = ImportPlan.From(SampleConfig());

        Assert.Equal("std-a", Assert.Single(plan.Standards).Id);
        Assert.Equal("ctrl-a", Assert.Single(plan.Controls).Id);
        Assert.Equal("org-a", Assert.Single(plan.Organisations).Id);
        Assert.Equal("scope-a", Assert.Single(plan.Scopes).Id);
    }

    [Fact]
    public void PlanCarriesTitleAndApiVersionForUpsert()
    {
        var row = Assert.Single(ImportPlan.From(SampleConfig()).Controls);

        Assert.Equal("Control A", row.Title);
        Assert.Equal("v1", row.ApiVersion);
    }

    [Fact]
    public void ScopeRowCarriesOrganisationStandardAndDisposition()
    {
        var row = Assert.Single(ImportPlan.From(SampleConfig()).Scopes);

        Assert.Equal("org-a", row.Organisation);
        Assert.Equal("std-a", row.Standard);
        Assert.Equal("In", row.Disposition);
    }

    [Fact]
    public void OrganisationRowCarriesKindAndNullParentForRoot()
    {
        var row = Assert.Single(ImportPlan.From(SampleConfig()).Organisations);

        Assert.Equal("Company", row.Kind);
        Assert.Null(row.Parent);
    }

    [Fact]
    public void OrganisationsOrderedParentBeforeChild()
    {
        var config = new GitOpsConfig
        {
            Organisations =
            [
                new Organisation { Id = "child", ApiVersion = "v1", Title = "Child", OrgKind = "Department", Parent = "root" },
                new Organisation { Id = "root", ApiVersion = "v1", Title = "Root", OrgKind = "Company" },
                new Organisation { Id = "grandchild", ApiVersion = "v1", Title = "GC", OrgKind = "Department", Parent = "child" },
            ],
        };

        var ids = ImportPlan.From(config).OrganisationIds;

        Assert.True(ids.ToList().IndexOf("root") < ids.ToList().IndexOf("child"));
        Assert.True(ids.ToList().IndexOf("child") < ids.ToList().IndexOf("grandchild"));
    }

    [Fact]
    public void TitleChangeWithSameIdProducesSameKey()
    {
        var first = SampleConfig();
        var renamed = first with
        {
            Standards = [first.Standards[0] with { Title = "Renamed" }],
        };

        var a = ImportPlan.From(first).Standards[0];
        var b = ImportPlan.From(renamed).Standards[0];

        Assert.Equal(a.Id, b.Id);
        Assert.NotEqual(a.Title, b.Title);
    }

    [Fact]
    public void CrossRefRowsDeriveFromMapsTo()
    {
        var plan = ImportPlan.From(SampleConfig());

        var cs = Assert.Single(plan.ControlStandards);
        Assert.Equal(("ctrl-a", "std-a"), (cs.ControlId, cs.StandardId));
    }

    [Fact]
    public void DuplicateRelationIdsCollapseToOneJoinRow()
    {
        var config = new GitOpsConfig
        {
            Standards = [new Standard { Id = "iso-27001", ApiVersion = "v1", Title = "ISO 27001" }],
            Controls =
            [
                new Control
                {
                    Id = "ctrl-a",
                    ApiVersion = "v1",
                    Title = "Control A",
                    MapsTo = ["iso-27001", "iso-27001"],
                },
            ],
        };

        var plan = ImportPlan.From(config);

        // Defensive Distinct collapses duplicates so the composite-PK join table never
        // receives a duplicate row.
        var cs = Assert.Single(plan.ControlStandards);
        Assert.Equal(("ctrl-a", "iso-27001"), (cs.ControlId, cs.StandardId));
    }

    [Fact]
    public void IdListsExposeKeepSetForDeletes()
    {
        var plan = ImportPlan.From(SampleConfig());

        Assert.Equal(["std-a"], plan.StandardIds);
        Assert.Equal(["ctrl-a"], plan.ControlIds);
        Assert.Equal(["org-a"], plan.OrganisationIds);
        Assert.Equal(["scope-a"], plan.ScopeIds);
    }
}
