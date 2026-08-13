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
        Assets =
        [
            new Asset { Id = "org-a", ApiVersion = "v1", Title = "Org A", Type = "Company", Source = "declared" },
            new Asset { Id = "vendor-a", ApiVersion = "v1", Title = "Vendor A", Type = "Vendor", Source = "declared", Owner = "org-a" },
        ],
        Scopes =
        [
            new Scope
            {
                Id = "scope-a",
                ApiVersion = "v1",
                Title = "Scope A",
                Subject = "org-a",
                Standard = "std-a",
                Disposition = "In",
            },
            new Scope
            {
                Id = "rs-a",
                ApiVersion = "v1",
                Title = "Requirement scope A",
                Subject = "org-a",
                Requirement = "req-a",
                Disposition = "Out",
                Justification = "Handled by a compensating control.",
            },
            new Scope
            {
                Id = "vs-a",
                ApiVersion = "v1",
                Title = "Vendor scope A",
                Subject = "vendor-a",
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
        Assert.Equal(["org-a", "vendor-a"], plan.Assets.Select(a => a.Id).ToArray());
        Assert.Equal(["scope-a", "rs-a", "vs-a"], plan.Scopes.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void PlanCarriesTitleAndApiVersionForUpsert()
    {
        var row = Assert.Single(ImportPlan.From(SampleConfig()).Controls);

        Assert.Equal("Control A", row.Title);
        Assert.Equal("v1", row.ApiVersion);
    }

    [Fact]
    public void StandardTargetScopeRowCarriesSubjectStandardAndNullsOtherTargets()
    {
        var row = ImportPlan.From(SampleConfig()).Scopes.Single(s => s.Id == "scope-a");

        Assert.Equal("org-a", row.Subject);
        Assert.Equal("std-a", row.Standard);
        Assert.Null(row.Requirement);
        Assert.Null(row.Control);
        Assert.Equal("In", row.Disposition);
        // A blank justification (permitted on an In scope) normalizes to null.
        Assert.Null(row.Justification);
    }

    [Fact]
    public void RequirementTargetScopeRowCarriesSubjectRequirementAndJustification()
    {
        var row = ImportPlan.From(SampleConfig()).Scopes.Single(s => s.Id == "rs-a");

        Assert.Equal("org-a", row.Subject);
        Assert.Equal("req-a", row.Requirement);
        Assert.Null(row.Standard);
        Assert.Null(row.Control);
        Assert.Equal("Out", row.Disposition);
        Assert.Equal("Handled by a compensating control.", row.Justification);
    }

    [Fact]
    public void VendorSubjectScopeRowCarriesTargetDispositionAndJustification()
    {
        var row = ImportPlan.From(SampleConfig()).Scopes.Single(s => s.Id == "vs-a");

        Assert.Equal("vendor-a", row.Subject);
        Assert.Equal("req-a", row.Requirement);
        Assert.Null(row.Standard);
        Assert.Null(row.Control);
        Assert.Equal("Out", row.Disposition);
        Assert.Equal("Supports MFA but not SSO.", row.Justification);
    }

    [Fact]
    public void ScopesFlattenInConfigOrder()
    {
        var config = new GitOpsConfig
        {
            Scopes =
            [
                new Scope { Id = "sc-b", ApiVersion = "v1", Title = "B", Subject = "org-a", Requirement = "req-b", Disposition = "Out", Justification = "r" },
                new Scope { Id = "sc-a", ApiVersion = "v1", Title = "A", Subject = "org-a", Requirement = "req-a", Disposition = "In" },
            ],
        };

        var plan = ImportPlan.From(config);

        Assert.Equal(["sc-b", "sc-a"], plan.Scopes.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void ControlTargetScopeNullsRequirementAndBlankJustification()
    {
        var config = new GitOpsConfig
        {
            Scopes =
            [
                new Scope
                {
                    Id = "vs-c", ApiVersion = "v1", Title = "T", Subject = "vendor-a",
                    Control = "ctrl-a", Disposition = "In", Justification = "   ",
                },
            ],
        };

        var row = Assert.Single(ImportPlan.From(config).Scopes);

        Assert.Equal("ctrl-a", row.Control);
        Assert.Null(row.Standard);
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
    public void CollectorRowCarriesItsTopLevelFields()
    {
        var config = new GitOpsConfig
        {
            Collectors =
            [
                new Collector
                {
                    Id = "collector-a", ApiVersion = "v1", Title = "T", Control = "ctrl-a", Vendor = "vendor-a",
                    Type = "integration", Provider = "fleet", Frequency = "daily", Threshold = "100",
                    Connection = "fleet-prod",
                    Config = new CollectorConfig
                    {
                        Checks = [new Check { SourceKey = "12", Name = "mfa-enforced", Severity = "Hard" }],
                    },
                },
            ],
        };

        var plan = ImportPlan.From(config);
        var row = Assert.Single(plan.Collectors);

        Assert.Equal("ctrl-a", row.Control);
        Assert.Equal("vendor-a", row.Vendor);
        Assert.Equal("fleet-prod", row.Connection);
        Assert.Equal("integration", row.Type);
        Assert.Equal("fleet", row.Provider);
        Assert.Equal("daily", row.Frequency);
        Assert.Equal(100, row.Threshold);
        Assert.Equal(["collector-a"], plan.CollectorIds);
    }

    [Fact]
    public void CollectorRowNullsOptionalFieldsWhenAbsent()
    {
        var config = new GitOpsConfig
        {
            Collectors =
            [
                new Collector
                {
                    Id = "collector-a", ApiVersion = "v1", Title = "T", Control = "ctrl-a",
                    Type = "manual", Frequency = "annual",
                },
            ],
        };

        var row = Assert.Single(ImportPlan.From(config).Collectors);

        Assert.Null(row.Vendor);
        Assert.Null(row.Connection);
        Assert.Null(row.Provider);
        Assert.Null(row.Threshold);
        // A config with no member present serializes to null (stored as SQL NULL), never to "{}".
        Assert.Null(row.ConfigJson);
    }

    [Fact]
    public void StoredConfigUsesMemberNamesOmitsAbsentMembersAndNumbersThePassMark()
    {
        var config = new GitOpsConfig
        {
            Collectors =
            [
                new Collector
                {
                    Id = "attest-training", ApiVersion = "v1", Title = "T", Control = "ctrl-a",
                    Type = "training", Frequency = "annual",
                    Config = new CollectorConfig
                    {
                        Body = "Read this.", PassMark = "80",
                        Quiz = [new QuizItem { Id = "q1", Prompt = "P", Options = ["a", "b"], Answer = "a" }],
                    },
                },
            ],
        };

        var json = Assert.Single(ImportPlan.From(config).Collectors).ConfigJson;

        Assert.NotNull(json);
        // The stored keys are the C# member names, which is what migration 021 composes in SQL.
        Assert.Contains("\"Body\":\"Read this.\"", json);
        // The pass mark is a JSON NUMBER, not the raw authored text the config record holds.
        Assert.Contains("\"PassMark\":80", json);
        // The stored quiz keeps the answer for the grading runtime; redaction happens at read.
        Assert.Contains("\"Answer\":\"a\"", json);
        // Absent members are omitted, never written as JSON null or as an empty array.
        Assert.DoesNotContain("Fields", json);
        Assert.DoesNotContain("Checks", json);
    }

    [Fact]
    public void StoredConfigTreatsABlankBodyAndAnEmptyListAsAbsent()
    {
        var config = new GitOpsConfig
        {
            Collectors =
            [
                new Collector
                {
                    Id = "attest-manual", ApiVersion = "v1", Title = "T", Control = "ctrl-a",
                    Type = "manual", Frequency = "annual",
                    Config = new CollectorConfig { Body = "   ", Fields = [] },
                },
            ],
        };

        // A blank scalar and an empty list are both absent, so no member is present and the whole
        // config is SQL NULL - the same rule the read model and the wire projection apply.
        Assert.Null(Assert.Single(ImportPlan.From(config).Collectors).ConfigJson);
    }

    [Fact]
    public void StoredConfigRoundTripsThroughTheReadProjection()
    {
        // The writer and the reader are the pair the storage contract binds together, so what one
        // writes the other must read back - answer excepted, which the read model has no member for.
        var config = new GitOpsConfig
        {
            Collectors =
            [
                new Collector
                {
                    Id = "attest-training", ApiVersion = "v1", Title = "T", Control = "ctrl-a",
                    Type = "training", Frequency = "annual",
                    Config = new CollectorConfig
                    {
                        Body = "Read this.", PassMark = "80",
                        Fields = [new AttestationField { Id = "f1", Label = "L", Type = "single-choice", Options = ["a", "b"] }],
                        Quiz = [new QuizItem { Id = "q1", Prompt = "P", Options = ["a", "b"], Answer = "a" }],
                    },
                },
            ],
        };

        var view = StoredCollectorConfig.Read(Assert.Single(ImportPlan.From(config).Collectors).ConfigJson);

        Assert.Equal("Read this.", view.Body);
        Assert.Equal(80, view.PassMark);
        var field = Assert.Single(view.Fields);
        Assert.Equal(("f1", "L", "single-choice"), (field.Id, field.Label, field.Type));
        Assert.Equal(["a", "b"], field.Options);
        var item = Assert.Single(view.Quiz);
        Assert.Equal(("q1", "P"), (item.Id, item.Prompt));
        Assert.Equal(["a", "b"], item.Options);
        Assert.Empty(view.Checks);
    }

    [Fact]
    public void NullStoredConfigReadsAsTheEmptyView()
    {
        // The path a script collector and a migrated form-less attestation both take.
        var view = StoredCollectorConfig.Read(null);

        Assert.Null(view.Body);
        Assert.Null(view.PassMark);
        Assert.Empty(view.Fields);
        Assert.Empty(view.Quiz);
        Assert.Empty(view.Checks);
    }

    [Fact]
    public void ExplicitNullConfigLoadedFromYamlSerializesToNullConfigJson()
    {
        // A collector authored with an explicit-null `config:` normalizes to an empty config on load,
        // then serializes to null (SQL NULL) in the import plan without throwing.
        var dir = Directory.CreateTempSubdirectory("fb-importplan-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "collector.yaml"), """
                apiVersion: freeboard.dev/v1alpha1
                kind: Collector
                id: collector-a
                title: T
                control: ctrl-a
                type: script
                frequency: daily
                config:
                """);

            var loaded = ConfigLoader.Load(dir.FullName);
            Assert.Empty(loaded.Diagnostics);

            var row = Assert.Single(ImportPlan.From(loaded.Config).Collectors);

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
    public void IntegrationCollectorStoresItsChecksUnderTheConfigChecksKey()
    {
        var config = new GitOpsConfig
        {
            Collectors =
            [
                new Collector
                {
                    Id = "collector-a", ApiVersion = "v1", Title = "T", Control = "ctrl-a",
                    Type = "integration", Provider = "fleet", Frequency = "daily", Connection = "fleet-prod",
                    Config = new CollectorConfig
                    {
                        Checks =
                        [
                            new Check { SourceKey = "12", Name = "mfa-enforced", Severity = "Hard" },
                            new Check { SourceKey = "34", Name = "disk-encrypted", Severity = "Soft" },
                        ],
                    },
                },
            ],
        };

        var row = Assert.Single(ImportPlan.From(config).Collectors);

        Assert.Equal("fleet-prod", row.Connection);
        Assert.NotNull(row.ConfigJson);
        // Each item keeps the key names the pre-merge checks column already held, which is what lets
        // migration 021 carry that column value across verbatim.
        Assert.Contains("\"SourceKey\":\"12\"", row.ConfigJson);
        Assert.Contains("\"Severity\":\"Hard\"", row.ConfigJson);
        Assert.Equal(2, StoredCollectorConfig.Read(row.ConfigJson).Checks.Count);
    }

    [Fact]
    public void AssetRowCarriesTypeAndNullEdgesForRoot()
    {
        var row = Assert.Single(ImportPlan.From(SampleConfig()).Assets, a => a.Id == "org-a");

        Assert.Equal("Company", row.Type);
        Assert.Null(row.Parent);
        Assert.Null(row.Owner);
    }

    [Fact]
    public void AssetsFlattenInConfigOrderWithNoParentBeforeChildReordering()
    {
        // assets.parent has no FK, so the plan imposes no topological order: rows keep config order.
        var config = new GitOpsConfig
        {
            Assets =
            [
                new Asset { Id = "child", ApiVersion = "v1", Title = "Child", Type = "Department", Source = "declared", Parent = "root" },
                new Asset { Id = "root", ApiVersion = "v1", Title = "Root", Type = "Company", Source = "declared" },
                new Asset { Id = "grandchild", ApiVersion = "v1", Title = "GC", Type = "Department", Source = "declared", Parent = "child" },
            ],
        };

        Assert.Equal(["child", "root", "grandchild"], ImportPlan.From(config).AssetIds.ToArray());
    }

    [Fact]
    public void AssetEdgesNormalizeToNullIfBlank()
    {
        var config = new GitOpsConfig
        {
            Assets =
            [
                new Asset { Id = "v", ApiVersion = "v1", Title = "V", Type = "Vendor", Source = "declared", Owner = "  " },
            ],
        };

        var row = Assert.Single(ImportPlan.From(config).Assets);
        Assert.Null(row.Parent);
        Assert.Null(row.Owner);
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
    public void VendorAssuranceRowsProjectFromEveryAssetsEntries()
    {
        var config = new GitOpsConfig
        {
            Assets =
            [
                new Asset
                {
                    Id = "vendor-a", ApiVersion = "v1", Title = "Vendor A", Type = "Vendor", Source = "declared",
                    Owner = "org-a",
                    Assurances =
                    [
                        new Assurance { Standard = "std-a", Expires = "2027-03-27" },
                        new Assurance { Standard = "std-b", Expires = "2026-11-01", WarnDays = "30" },
                    ],
                },
                new Asset { Id = "org-a", ApiVersion = "v1", Title = "Org A", Type = "Company", Source = "declared" },
            ],
        };

        var rows = ImportPlan.From(config).VendorAssurances;

        Assert.Equal(2, rows.Count);
        Assert.Equal("vendor-a", rows[0].VendorId);
        Assert.Equal("std-a", rows[0].StandardId);
        Assert.Equal(new DateOnly(2027, 3, 27), rows[0].Expires);
        Assert.Null(rows[0].WarnDays);
        Assert.Equal(30, rows[1].WarnDays);
    }

    [Fact]
    public void AnAssetWithNoAssurancesContributesNoRows()
    {
        Assert.Empty(ImportPlan.From(SampleConfig()).VendorAssurances);
    }

    [Fact]
    public void AnUnparseableExpiryThrowsRatherThanVanishing()
    {
        // Validation has already rejected this, so reaching it means the caller skipped validation. A
        // dropped row would read as a vendor that quietly lost a certification.
        var config = new GitOpsConfig
        {
            Assets =
            [
                new Asset
                {
                    Id = "vendor-a", ApiVersion = "v1", Title = "Vendor A", Type = "Vendor", Source = "declared",
                    Assurances = [new Assurance { Standard = "std-a", Expires = "soon" }],
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => ImportPlan.From(config));

        Assert.Contains("vendor-a", ex.Message, StringComparison.Ordinal);
        Assert.Contains("soon", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IdListsExposeKeepSetForDeletes()
    {
        var plan = ImportPlan.From(SampleConfig());

        Assert.Equal(["std-a"], plan.StandardIds);
        Assert.Equal(["req-a"], plan.RequirementIds);
        Assert.Equal(["ctrl-a"], plan.ControlIds);
        Assert.Equal(["org-a", "vendor-a"], plan.AssetIds);
        Assert.Equal(["org-a"], plan.OrganisationIds);
    }
}
