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
        Scopes =
        [
            new Scope { Id = "scope-a", ApiVersion = "v1", Title = "Scope A", Controls = ["ctrl-a"] },
        ],
    };

    [Fact]
    public void PlanKeysDomainRowsOnId()
    {
        var plan = ImportPlan.From(SampleConfig());

        Assert.Equal("std-a", Assert.Single(plan.Standards).Id);
        Assert.Equal("ctrl-a", Assert.Single(plan.Controls).Id);
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
    public void CrossRefRowsDeriveFromMapsToAndControls()
    {
        var plan = ImportPlan.From(SampleConfig());

        var cs = Assert.Single(plan.ControlStandards);
        Assert.Equal(("ctrl-a", "std-a"), (cs.ControlId, cs.StandardId));

        var sc = Assert.Single(plan.ScopeControls);
        Assert.Equal(("scope-a", "ctrl-a"), (sc.ScopeId, sc.ControlId));
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
            Scopes =
            [
                new Scope { Id = "scope-a", ApiVersion = "v1", Title = "Scope A", Controls = ["ctrl-a", "ctrl-a"] },
            ],
        };

        var plan = ImportPlan.From(config);

        // Defensive Distinct collapses duplicates so the composite-PK join tables never
        // receive a duplicate row.
        var cs = Assert.Single(plan.ControlStandards);
        Assert.Equal(("ctrl-a", "iso-27001"), (cs.ControlId, cs.StandardId));

        var sc = Assert.Single(plan.ScopeControls);
        Assert.Equal(("scope-a", "ctrl-a"), (sc.ScopeId, sc.ControlId));
    }

    [Fact]
    public void IdListsExposeKeepSetForDeletes()
    {
        var plan = ImportPlan.From(SampleConfig());

        Assert.Equal(["std-a"], plan.StandardIds);
        Assert.Equal(["ctrl-a"], plan.ControlIds);
        Assert.Equal(["scope-a"], plan.ScopeIds);
    }
}
