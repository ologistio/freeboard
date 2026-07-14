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
        RequirementScopes =
        [
            new RequirementScope
            {
                Id = "rs-a",
                ApiVersion = "v1",
                Title = "RequirementScope A",
                Organisation = "org-a",
                Requirement = "req-a",
                Disposition = "Out",
            },
        ],
        Vendors =
        [
            new Vendor { Id = "vendor-a", ApiVersion = "v1", Title = "Vendor A" },
        ],
        VendorScopes =
        [
            new VendorScope
            {
                Id = "vs-a",
                ApiVersion = "v1",
                Title = "VendorScope A",
                Vendor = "vendor-a",
                Requirement = "req-a",
                Disposition = "Out",
                Justification = "Supports MFA but not SSO.",
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
        Assert.Equal("rs-a", Assert.Single(plan.RequirementScopes).Id);
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
    public void RequirementScopeRowCarriesOrganisationRequirementAndDisposition()
    {
        var row = Assert.Single(ImportPlan.From(SampleConfig()).RequirementScopes);

        Assert.Equal("org-a", row.Organisation);
        Assert.Equal("req-a", row.Requirement);
        Assert.Equal("Out", row.Disposition);
    }

    [Fact]
    public void RequirementScopesFlattenInConfigOrder()
    {
        var config = new GitOpsConfig
        {
            RequirementScopes =
            [
                new RequirementScope { Id = "rs-b", ApiVersion = "v1", Title = "B", Organisation = "org-a", Requirement = "req-b", Disposition = "Out" },
                new RequirementScope { Id = "rs-a", ApiVersion = "v1", Title = "A", Organisation = "org-a", Requirement = "req-a", Disposition = "In" },
            ],
        };

        var rows = ImportPlan.From(config).RequirementScopes;

        Assert.Equal(["rs-b", "rs-a"], rows.Select(r => r.Id).ToArray());
        Assert.Equal(["rs-b", "rs-a"], ImportPlan.From(config).RequirementScopeIds.ToArray());
    }

    [Fact]
    public void VendorScopeRowCarriesTargetDispositionAndJustification()
    {
        var row = Assert.Single(ImportPlan.From(SampleConfig()).VendorScopes);

        Assert.Equal("vendor-a", row.Vendor);
        Assert.Equal("req-a", row.Requirement);
        Assert.Null(row.Control);
        Assert.Equal("Out", row.Disposition);
        Assert.Equal("Supports MFA but not SSO.", row.Justification);
    }

    [Fact]
    public void VendorScopeControlTargetNullsRequirementAndBlankJustification()
    {
        var config = new GitOpsConfig
        {
            VendorScopes =
            [
                new VendorScope
                {
                    Id = "vs-c", ApiVersion = "v1", Title = "T", Vendor = "vendor-a",
                    Control = "ctrl-a", Disposition = "In", Justification = "   ",
                },
            ],
        };

        var row = Assert.Single(ImportPlan.From(config).VendorScopes);

        Assert.Equal("ctrl-a", row.Control);
        Assert.Null(row.Requirement);
        // A blank justification (permitted on an In scope) normalizes to null like other optional fields.
        Assert.Null(row.Justification);
    }

    [Fact]
    public void ControlRowCarriesEvaluationNullWhenBlank()
    {
        var config = new GitOpsConfig
        {
            Controls =
            [
                new Control { Id = "ctrl-a", ApiVersion = "v1", Title = "A", MapsTo = ["req-a"], Evaluation = "all" },
                new Control { Id = "ctrl-b", ApiVersion = "v1", Title = "B", MapsTo = ["req-a"] },
            ],
        };

        var rows = ImportPlan.From(config).Controls;

        Assert.Equal("all", rows.Single(r => r.Id == "ctrl-a").Evaluation);
        Assert.Null(rows.Single(r => r.Id == "ctrl-b").Evaluation);
    }

    [Fact]
    public void EvidenceCollectorRowCarriesFieldsAndSerializesConfig()
    {
        var config = new GitOpsConfig
        {
            EvidenceCollectors =
            [
                new EvidenceCollector
                {
                    Id = "collector-a", ApiVersion = "v1", Title = "T", Control = "ctrl-a", Vendor = "vendor-a",
                    Type = "integration", Frequency = "daily", Threshold = "100",
                    Config = new Dictionary<string, string> { ["endpoint"] = "policies.mfa" },
                },
            ],
        };

        var row = Assert.Single(ImportPlan.From(config).EvidenceCollectors);

        Assert.Equal("ctrl-a", row.Control);
        Assert.Equal("vendor-a", row.Vendor);
        Assert.Equal("integration", row.Type);
        Assert.Equal("daily", row.Frequency);
        Assert.Equal(100, row.Threshold);
        Assert.Equal("{\"endpoint\":\"policies.mfa\"}", row.ConfigJson);
        Assert.Equal(["collector-a"], ImportPlan.From(config).EvidenceCollectorIds);
    }

    [Fact]
    public void EvidenceCollectorRowNullsOptionalFieldsWhenAbsent()
    {
        var config = new GitOpsConfig
        {
            EvidenceCollectors =
            [
                new EvidenceCollector
                {
                    Id = "collector-a", ApiVersion = "v1", Title = "T", Control = "ctrl-a",
                    Type = "manual-attestation", Frequency = "annual",
                },
            ],
        };

        var row = Assert.Single(ImportPlan.From(config).EvidenceCollectors);

        Assert.Null(row.Vendor);
        Assert.Null(row.Threshold);
        // An empty config map serializes to null (stored as SQL NULL), never throwing.
        Assert.Null(row.ConfigJson);
    }

    [Fact]
    public void AttestationTemplateRowSerializesFieldsQuizAndParsesPassMark()
    {
        var config = new GitOpsConfig
        {
            AttestationTemplates =
            [
                new AttestationTemplate
                {
                    Id = "attest-training", ApiVersion = "v1", Title = "T", Control = "ctrl-a", Type = "training",
                    Body = "Read this.", PassMark = "80",
                    Quiz =
                    [
                        new QuizItem { Id = "q1", Prompt = "P", Options = ["a", "b"], Answer = "a" },
                    ],
                },
            ],
        };

        var row = Assert.Single(ImportPlan.From(config).AttestationTemplates);

        Assert.Equal("ctrl-a", row.Control);
        Assert.Equal("training", row.Type);
        Assert.Equal("Read this.", row.Body);
        Assert.Equal(80, row.PassMark);
        Assert.Null(row.FieldsJson);
        // The serialized quiz keeps the answer for the grading runtime; redaction happens at read.
        Assert.Contains("\"Answer\":\"a\"", row.QuizJson);
        Assert.Equal(["attest-training"], ImportPlan.From(config).AttestationTemplateIds);
    }

    [Fact]
    public void AttestationTemplateRowNullsOptionalFieldsWhenAbsent()
    {
        var config = new GitOpsConfig
        {
            AttestationTemplates =
            [
                new AttestationTemplate
                {
                    Id = "attest-manual", ApiVersion = "v1", Title = "T", Control = "ctrl-a", Type = "manual",
                },
            ],
        };

        var row = Assert.Single(ImportPlan.From(config).AttestationTemplates);

        Assert.Null(row.Body);
        Assert.Null(row.PassMark);
        // Empty field and quiz lists serialize to null (stored as SQL NULL), never throwing.
        Assert.Null(row.FieldsJson);
        Assert.Null(row.QuizJson);
    }

    [Fact]
    public void ExplicitNullNestedListsLoadedFromYamlSerializeToNull()
    {
        // A template authored with explicit-null `fields:`/`quiz:` normalizes to empty lists on load,
        // then serializes to null (SQL NULL) in the import plan without throwing.
        var dir = Directory.CreateTempSubdirectory("fb-importplan-");
        try
        {
            File.WriteAllText(Path.Join(dir.FullName, "template.yaml"), """
                apiVersion: freeboard.dev/v1alpha1
                kind: AttestationTemplate
                id: attest-manual
                title: T
                control: ctrl-a
                type: manual
                fields:
                quiz:
                """);

            var loaded = ConfigLoader.Load(dir.FullName);
            Assert.Empty(loaded.Diagnostics);

            var row = Assert.Single(ImportPlan.From(loaded.Config).AttestationTemplates);

            Assert.Null(row.FieldsJson);
            Assert.Null(row.QuizJson);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ExplicitNullConfigLoadedFromYamlSerializesToNullConfigJson()
    {
        // A collector authored with an explicit-null `config:` normalizes to an empty map on load,
        // then serializes to null (SQL NULL) in the import plan without throwing.
        var dir = Directory.CreateTempSubdirectory("fb-importplan-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "collector.yaml"), """
                apiVersion: freeboard.dev/v1alpha1
                kind: EvidenceCollector
                id: collector-a
                title: T
                control: ctrl-a
                type: integration
                frequency: daily
                config:
                """);

            var loaded = ConfigLoader.Load(dir.FullName);
            Assert.Empty(loaded.Diagnostics);

            var row = Assert.Single(ImportPlan.From(loaded.Config).EvidenceCollectors);

            Assert.Null(row.ConfigJson);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void IntegrationConnectionMapsToRow()
    {
        var config = new GitOpsConfig
        {
            IntegrationConnections =
            [
                new IntegrationConnection
                {
                    Id = "fleet-prod", ApiVersion = "v1", Title = "Fleet Production", Provider = "fleet",
                    BaseUrl = "https://fleet.example.com", DiscoveryCadence = "daily", Vendor = "vendor-a",
                },
                new IntegrationConnection
                {
                    Id = "fleet-dev", ApiVersion = "v1", Title = "Fleet Dev", Provider = "fleet",
                    BaseUrl = "https://dev.example.com", DiscoveryCadence = "weekly", Vendor = "   ",
                },
            ],
        };

        var plan = ImportPlan.From(config);

        var prod = plan.IntegrationConnections.Single(r => r.Id == "fleet-prod");
        Assert.Equal("fleet", prod.Provider);
        Assert.Equal("https://fleet.example.com", prod.BaseUrl);
        Assert.Equal("daily", prod.DiscoveryCadence);
        Assert.Equal("vendor-a", prod.Vendor);
        // A blank vendor normalizes to null like other optional fields.
        Assert.Null(plan.IntegrationConnections.Single(r => r.Id == "fleet-dev").Vendor);
        Assert.Equal(["fleet-prod", "fleet-dev"], plan.IntegrationConnectionIds);
    }

    [Fact]
    public void IntegrationCollectorSerializesChecksAndMapsConnection()
    {
        var config = new GitOpsConfig
        {
            EvidenceCollectors =
            [
                new EvidenceCollector
                {
                    Id = "collector-a", ApiVersion = "v1", Title = "T", Control = "ctrl-a",
                    Type = "integration", Frequency = "daily", Connection = "fleet-prod",
                    Checks =
                    [
                        new Check { SourceKey = "12", Name = "mfa-enforced", Severity = "Hard" },
                        new Check { SourceKey = "34", Name = "disk-encrypted", Severity = "Soft" },
                    ],
                },
            ],
        };

        var row = Assert.Single(ImportPlan.From(config).EvidenceCollectors);

        Assert.Equal("fleet-prod", row.Connection);
        Assert.NotNull(row.ChecksJson);
        Assert.Contains("\"SourceKey\":\"12\"", row.ChecksJson);
        Assert.Contains("\"Severity\":\"Hard\"", row.ChecksJson);
    }

    [Fact]
    public void NonIntegrationCollectorNullsConnectionAndChecksJson()
    {
        var config = new GitOpsConfig
        {
            EvidenceCollectors =
            [
                new EvidenceCollector
                {
                    Id = "collector-a", ApiVersion = "v1", Title = "T", Control = "ctrl-a",
                    Type = "script", Frequency = "weekly",
                },
            ],
        };

        var row = Assert.Single(ImportPlan.From(config).EvidenceCollectors);

        Assert.Null(row.Connection);
        // An empty checks list serializes to null (stored as SQL NULL), never throwing.
        Assert.Null(row.ChecksJson);
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
        Assert.Equal(["rs-a"], plan.RequirementScopeIds);
        Assert.Equal(["vendor-a"], plan.VendorIds);
        Assert.Equal(["vs-a"], plan.VendorScopeIds);
    }
}
