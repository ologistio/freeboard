using Freeboard.Core.GitOps;

namespace Freeboard.Core.Tests;

public sealed class ConfigLoaderTests
{
    [Fact]
    public void ValidConfigLoadsWithCorrectCounts()
    {
        using var dir = TempConfig.Create(
            ("standards.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Standard
                id: std-a
                title: Standard A
                version: "1.0"
                authority: Example Authority
                """),
            ("requirements.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Requirement
                id: req-a
                title: Requirement A
                standard: std-a
                theme: Theme A
                statement: Do the thing.
                citation_label: Source A
                citation_url: https://example.com/a
                """),
            ("controls.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Control
                id: ctrl-a
                title: Control A
                maps_to:
                  - req-a
                """),
            ("orgs.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Organisation
                id: org-a
                title: Org A
                type: Company
                """),
            ("scopes.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Scope
                id: scope-a
                title: Scope A
                organisation: org-a
                standard: std-a
                disposition: In
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Single(result.Config.Standards);
        Assert.Single(result.Config.Requirements);
        Assert.Single(result.Config.Controls);
        Assert.Single(result.Config.Organisations);
        Assert.Single(result.Config.Scopes);

        var standard = result.Config.Standards[0];
        Assert.Equal("std-a", standard.Id);
        Assert.Equal("Standard A", standard.Title);
        Assert.NotEqual(standard.Id, standard.Title);
        Assert.Equal(["req-a"], result.Config.Controls[0].MapsTo);

        var organisation = result.Config.Organisations[0];
        Assert.Equal("org-a", organisation.Id);
        Assert.Equal("Company", organisation.OrgKind);
        Assert.Empty(organisation.Parent);

        var scope = result.Config.Scopes[0];
        Assert.Equal("org-a", scope.Organisation);
        Assert.Equal("std-a", scope.Standard);
        Assert.Equal("In", scope.Disposition);
    }

    [Fact]
    public void ValidMultiKindConfigIncludingVendorsLoadsAndValidates()
    {
        using var dir = TempConfig.Create(
            ("all.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Standard
                id: std-a
                title: Standard A
                version: "1.0"
                authority: Example Authority
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Requirement
                id: req-a
                title: Requirement A
                standard: std-a
                theme: Theme A
                statement: Do the thing.
                citation_label: Source A
                citation_url: https://example.com/a
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Control
                id: ctrl-a
                title: Control A
                maps_to:
                  - req-a
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Organisation
                id: org-a
                title: Org A
                type: Company
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Scope
                id: scope-a
                title: Scope A
                organisation: org-a
                standard: std-a
                disposition: In
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: RequirementScope
                id: rs-a
                title: Exclude req-a
                organisation: org-a
                requirement: req-a
                disposition: Out
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Vendor
                id: vendor-a
                title: Vendor A
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: VendorScope
                id: vs-req
                title: Except req-a for vendor-a
                vendor: vendor-a
                requirement: req-a
                disposition: Out
                justification: Supports MFA but not SSO.
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: VendorScope
                id: vs-ctrl
                title: Include ctrl-a for vendor-a
                vendor: vendor-a
                control: ctrl-a
                disposition: In
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Single(result.Config.Vendors);
        Assert.Equal(2, result.Config.VendorScopes.Count);
        Assert.Equal("vendor-a", result.Config.Vendors[0].Id);
        Assert.Equal(["vs-req", "vs-ctrl"], result.Config.VendorScopes.Select(v => v.Id).ToArray());
    }

    [Fact]
    public void ValidMultiKindConfigIncludingEvidenceCollectorsLoadsAndValidates()
    {
        using var dir = TempConfig.Create(
            ("all.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Standard
                id: std-a
                title: Standard A
                version: "1.0"
                authority: Example Authority
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Requirement
                id: req-a
                title: Requirement A
                standard: std-a
                theme: Theme A
                statement: Do the thing.
                citation_label: Source A
                citation_url: https://example.com/a
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Control
                id: ctrl-a
                title: Control A
                maps_to:
                  - req-a
                evaluation: all
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Vendor
                id: vendor-a
                title: Vendor A
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: IntegrationConnection
                id: fleet-prod
                title: Fleet Production
                provider: fleet
                base_url: https://fleet.example.com
                discovery_cadence: daily
                vendor: vendor-a
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: EvidenceCollector
                id: collector-integration
                title: Endpoint MFA via Crowdstrike
                control: ctrl-a
                vendor: vendor-a
                type: integration
                frequency: daily
                threshold: 100
                connection: fleet-prod
                config:
                  endpoint: policies.mfa
                checks:
                  - source_key: "12"
                    name: mfa-enforced
                    severity: Hard
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: EvidenceCollector
                id: collector-manual
                title: Annual policy attestation
                control: ctrl-a
                type: manual-attestation
                frequency: annual
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Equal("all", result.Config.Controls[0].Evaluation);
        Assert.Equal(["collector-integration", "collector-manual"], result.Config.EvidenceCollectors.Select(c => c.Id).ToArray());
        var integration = result.Config.EvidenceCollectors[0];
        Assert.Equal("vendor-a", integration.Vendor);
        Assert.Equal("policies.mfa", integration.Config["endpoint"]);
        Assert.Empty(result.Config.EvidenceCollectors[1].Config);
    }

    [Fact]
    public void ValidMultiKindConfigIncludingAttestationTemplatesLoadsAndValidates()
    {
        using var dir = TempConfig.Create(
            ("all.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Standard
                id: std-a
                title: Standard A
                version: "1.0"
                authority: Example Authority
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Requirement
                id: req-a
                title: Requirement A
                standard: std-a
                theme: Theme A
                statement: Do the thing.
                citation_label: Source A
                citation_url: https://example.com/a
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Control
                id: ctrl-a
                title: Control A
                maps_to:
                  - req-a
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: AttestationTemplate
                id: attest-manual
                title: Firewall change attestation
                control: ctrl-a
                type: manual
                body: Confirm the ruleset was reviewed.
                fields:
                  - id: reviewed
                    label: Ruleset reviewed?
                    type: boolean
                  - id: outcome
                    label: Review outcome
                    type: single-choice
                    options: [pass, fail]
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: AttestationTemplate
                id: attest-training
                title: Phishing awareness
                control: ctrl-a
                type: training
                pass_mark: 80
                quiz:
                  - id: q1
                    prompt: What should you do with an unexpected attachment?
                    options: [Open it, Report it]
                    answer: Report it
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Equal(["attest-manual", "attest-training"], result.Config.AttestationTemplates.Select(t => t.Id).ToArray());
        var manual = result.Config.AttestationTemplates[0];
        Assert.Equal(2, manual.Fields.Count);
        Assert.Equal(["pass", "fail"], manual.Fields[1].Options.ToArray());
        var training = result.Config.AttestationTemplates[1];
        Assert.Equal("80", training.PassMark);
        Assert.Equal("Report it", Assert.Single(training.Quiz).Answer);
    }

    [Fact]
    public void MultipleDocumentsInOneFileAllParse()
    {
        using var dir = TempConfig.Create(
            ("all.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Standard
                id: std-a
                title: A
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Standard
                id: std-b
                title: B
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Control
                id: ctrl-a
                title: Control A
                maps_to:
                  - std-a
                """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Equal(2, result.Config.Standards.Count);
        Assert.Single(result.Config.Controls);
    }

    [Fact]
    public void MissingDirectoryReturnsDiagnosticNotException()
    {
        var result = ConfigLoader.Load("/no/such/dir/here");

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("not found"));
    }

    [Fact]
    public void MalformedYamlReturnsDiagnosticNotException()
    {
        using var dir = TempConfig.Create(
            ("bad.yaml", "kind: Standard\n  id: x\n :::not valid"));

        var result = ConfigLoader.Load(dir.Path);

        Assert.False(result.IsValid);
        var diag = Assert.Single(result.Diagnostics);
        Assert.Contains("Malformed YAML", diag.Message);
        Assert.Equal("bad.yaml", diag.File);
    }

    [Fact]
    public void MissingKindIsLoaderDiagnostic()
    {
        using var dir = TempConfig.Create(
            ("x.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                id: std-a
                title: A
                """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("no 'kind'"));
    }

    [Fact]
    public void UnknownKindIsLoaderDiagnostic()
    {
        using var dir = TempConfig.Create(
            ("x.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Widget
                id: w-a
                title: A
                """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown kind 'Widget'"));
        Assert.Empty(result.Config.Standards);
    }

    [Fact]
    public void UnknownFieldIsRejected()
    {
        using var dir = TempConfig.Create(
            ("x.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Standard
                id: std-a
                title: A
                colour: blue
                """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown field 'colour'"));
    }

    [Fact]
    public void OrganisationKindAuthoredUnderType()
    {
        using var dir = TempConfig.Create(
            ("org.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Organisation
                id: org-a
                title: Org A
                type: Company
                """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Empty(result.Diagnostics);
        Assert.Equal("Company", Assert.Single(result.Config.Organisations).OrgKind);
    }

    [Fact]
    public void OrganisationOrgKindKeyIsRejectedAsUnknownField()
    {
        using var dir = TempConfig.Create(
            ("org.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Organisation
                id: org-a
                title: Org A
                org_kind: Company
                """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown field 'org_kind'"));
    }

    [Fact]
    public void ExplicitNullFieldAndQuizItemsAreDroppedNotThrown()
    {
        // A null sequence item (`fields:\n  -`) deserializes to a null element; the loader must
        // drop it rather than NRE while normalizing, keeping its never-throw contract.
        using var dir = TempConfig.Create(
            ("template.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: AttestationTemplate
                id: attest-manual
                title: T
                control: ctrl-a
                type: manual
                fields:
                  -
                quiz:
                  -
                """));

        var result = ConfigLoader.Load(dir.Path);

        var template = Assert.Single(result.Config.AttestationTemplates);
        Assert.Empty(template.Fields);
        Assert.Empty(template.Quiz);
    }

    [Fact]
    public void LoadOrderMatchesNormalizedPathThenInFileOrder()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "order");

        var result = ConfigLoader.Load(fixtureDir);

        Assert.Equal(["a1", "a2", "b1", "b2"], result.Config.Standards.Select(s => s.Id).ToArray());
    }
}
