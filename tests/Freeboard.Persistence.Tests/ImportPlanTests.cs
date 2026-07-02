using Freeboard.Core.GitOps;
using Freeboard.Persistence.GitOps;

namespace Freeboard.Persistence.Tests;

public sealed class ImportPlanTests
{
    private static GitOpsConfig SampleConfig() => new()
    {
        Standards =
        [
            new Standard { Id = "std-a", ApiVersion = "v1", Title = "Standard A", Version = "1.0", Authority = "Example Authority" },
        ],
        Requirements =
        [
            new Requirement
            {
                Id = "req-a",
                ApiVersion = "v1",
                Title = "Requirement A",
                Standard = "std-a",
                Theme = "Theme A",
                Statement = "Do the thing.",
                CitationLabel = "Source A",
                CitationUrl = "https://example.com/a",
            },
        ],
        Controls =
        [
            new Control { Id = "ctrl-a", ApiVersion = "v1", Title = "Control A", MapsTo = ["req-a"] },
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

        var cr = Assert.Single(plan.ControlRequirements);
        Assert.Equal(("ctrl-a", "req-a"), (cr.ControlId, cr.RequirementId));
    }

    [Fact]
    public void DuplicateRelationIdsCollapseToOneJoinRow()
    {
        var config = new GitOpsConfig
        {
            Controls =
            [
                new Control
                {
                    Id = "ctrl-a",
                    ApiVersion = "v1",
                    Title = "Control A",
                    MapsTo = ["req-a", "req-a"],
                },
            ],
        };

        var plan = ImportPlan.From(config);

        // Defensive Distinct collapses duplicates so the composite-PK join table never
        // receives a duplicate row.
        var cr = Assert.Single(plan.ControlRequirements);
        Assert.Equal(("ctrl-a", "req-a"), (cr.ControlId, cr.RequirementId));
    }

    [Fact]
    public void RequirementRowsFlattenInOrderCarryingStandardAndCitation()
    {
        var config = new GitOpsConfig
        {
            Requirements =
            [
                new Requirement
                {
                    Id = "req-b", ApiVersion = "v1", Title = "B", Standard = "std-a", Theme = "T",
                    Statement = "S", CitationLabel = "L", CitationUrl = "https://example.com/b",
                },
                new Requirement
                {
                    Id = "req-a", ApiVersion = "v1", Title = "A", Standard = "std-a", Theme = "T",
                    Statement = "S", CitationLabel = "L", CitationUrl = "https://example.com/a",
                },
            ],
        };

        var rows = ImportPlan.From(config).Requirements;

        // Flatten preserves config order (no reordering imposed by the plan).
        Assert.Equal(["req-b", "req-a"], rows.Select(r => r.Id).ToArray());
        Assert.Equal("std-a", rows[0].Standard);
        Assert.Equal("https://example.com/b", rows[0].CitationUrl);
    }

    [Fact]
    public void BlankOptionalFieldsNormalizeToNull()
    {
        var config = new GitOpsConfig
        {
            Standards =
            [
                new Standard
                {
                    Id = "std-a", ApiVersion = "v1", Title = "A", Version = "1.0", Authority = "Auth",
                    Publisher = "   ", SourceUrl = string.Empty,
                },
            ],
            Requirements =
            [
                new Requirement
                {
                    Id = "req-a", ApiVersion = "v1", Title = "A", Standard = "std-a", Theme = "T",
                    Statement = "S", Guidance = "   ", CitationLabel = "L", CitationUrl = "https://example.com/a",
                },
            ],
        };

        var plan = ImportPlan.From(config);

        var standard = Assert.Single(plan.Standards);
        Assert.Null(standard.Publisher);
        Assert.Null(standard.SourceUrl);
        Assert.Null(Assert.Single(plan.Requirements).Guidance);
    }

    [Fact]
    public void IdListsExposeKeepSetForDeletes()
    {
        var plan = ImportPlan.From(SampleConfig());

        Assert.Equal(["std-a"], plan.StandardIds);
        Assert.Equal(["req-a"], plan.RequirementIds);
        Assert.Equal(["ctrl-a"], plan.ControlIds);
        Assert.Equal(["org-a"], plan.OrganisationIds);
        Assert.Equal(["scope-a"], plan.ScopeIds);
    }
}
