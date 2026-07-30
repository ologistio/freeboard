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
                kind: Asset
                id: org-a
                title: Org A
                type: Company
                source: declared
                """),
            ("scopes.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Scope
                id: scope-a
                title: Scope A
                subject: org-a
                standard: std-a
                disposition: In
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Single(result.Config.Standards);
        Assert.Single(result.Config.Requirements);
        Assert.Single(result.Config.Controls);
        Assert.Single(result.Config.Assets);
        Assert.Single(result.Config.Scopes);

        var standard = result.Config.Standards[0];
        Assert.Equal("std-a", standard.Id);
        Assert.Equal("Standard A", standard.Title);
        Assert.NotEqual(standard.Id, standard.Title);
        Assert.Equal(["req-a"], result.Config.Controls[0].MapsTo);

        var organisation = result.Config.Assets[0];
        Assert.Equal("org-a", organisation.Id);
        Assert.Equal("Company", organisation.Type);
        Assert.Empty(organisation.Parent);

        var scope = result.Config.Scopes[0];
        Assert.Equal("org-a", scope.Subject);
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
                kind: Asset
                id: org-a
                title: Org A
                type: Company
                source: declared
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Scope
                id: scope-org-std
                title: Org A in std-a
                subject: org-a
                standard: std-a
                disposition: In
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Scope
                id: scope-org-req
                title: Exclude req-a for org-a
                subject: org-a
                requirement: req-a
                disposition: Out
                justification: Handled by a compensating control.
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Asset
                id: vendor-a
                title: Vendor A
                type: Vendor
                source: declared
                owner: org-a
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Scope
                id: scope-vendor-req
                title: Except req-a for vendor-a
                subject: vendor-a
                requirement: req-a
                disposition: Out
                justification: Supports MFA but not SSO.
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Scope
                id: scope-vendor-ctrl
                title: Include ctrl-a for vendor-a
                subject: vendor-a
                control: ctrl-a
                disposition: In
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Equal(2, result.Config.Assets.Count);
        Assert.Equal(4, result.Config.Scopes.Count);
        Assert.Contains(result.Config.Assets, a => a.Id == "vendor-a" && a.Type == "Vendor");
        Assert.Equal(
            ["scope-org-std", "scope-org-req", "scope-vendor-req", "scope-vendor-ctrl"],
            result.Config.Scopes.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void ValidMultiKindConfigIncludingCollectorsLoadsAndValidates()
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
                kind: Asset
                id: org-a
                title: Org A
                type: Company
                source: declared
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Asset
                id: vendor-a
                title: Vendor A
                type: Vendor
                source: declared
                owner: org-a
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Integration
                id: fleet-prod
                title: Fleet Production
                provider: fleet
                base_url: https://fleet.example.com
                discovery_cadence: daily
                vendor: vendor-a
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Collector
                id: collector-integration
                title: Endpoint MFA via Fleet
                control: ctrl-a
                vendor: vendor-a
                type: integration
                provider: fleet
                frequency: daily
                threshold: 100
                connection: fleet-prod
                config:
                  checks:
                    - source_key: "12"
                      name: mfa-enforced
                      severity: Hard
                ---
                apiVersion: freeboard.dev/v1alpha1
                kind: Collector
                id: collector-manual
                title: Annual policy attestation
                control: ctrl-a
                type: manual
                frequency: annual
                config:
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
                kind: Collector
                id: collector-training
                title: Phishing awareness
                control: ctrl-a
                type: training
                frequency: annual
                config:
                  pass_mark: 80
                  quiz:
                    - id: q1
                      prompt: What should you do with an unexpected attachment?
                      options: [Open it, Report it]
                      answer: Report it
                """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Equal("all", result.Config.Controls[0].Evaluation);
        Assert.Equal(
            ["collector-integration", "collector-manual", "collector-training"],
            result.Config.Collectors.Select(c => c.Id).ToArray());
        var integration = result.Config.Collectors[0];
        Assert.Equal("vendor-a", integration.Vendor);
        Assert.Equal("fleet", integration.Provider);
        Assert.Equal("mfa-enforced", Assert.Single(integration.Config.Checks).Name);
        var manual = result.Config.Collectors[1];
        Assert.Equal("Confirm the ruleset was reviewed.", manual.Config.Body);
        Assert.Equal(2, manual.Config.Fields.Count);
        Assert.Equal(["pass", "fail"], manual.Config.Fields[1].Options.ToArray());
        Assert.Empty(manual.Config.Checks);
        var training = result.Config.Collectors[2];
        Assert.Equal("80", training.Config.PassMark);
        Assert.Equal("Report it", Assert.Single(training.Config.Quiz).Answer);
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
    public void AssetTypeAuthoredUnderType()
    {
        using var dir = TempConfig.Create(
            ("org.yaml", """
                apiVersion: freeboard.dev/v1alpha1
                kind: Asset
                id: org-a
                title: Org A
                type: Company
                source: declared
                """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Empty(result.Diagnostics);
        Assert.Equal("Company", Assert.Single(result.Config.Assets).Type);
    }

    [Theory]
    [InlineData("EvidenceCollector")]
    [InlineData("AttestationTemplate")]
    public void RetiredCollectorKindsAreNowUnknown(string kind)
    {
        using var dir = TempConfig.Create(
            ("x.yaml", $"""
                apiVersion: freeboard.dev/v1alpha1
                kind: {kind}
                id: collector-a
                title: T
                control: ctrl-a
                type: manual
                """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Empty(result.Config.Collectors);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Message.Contains($"Unknown kind '{kind}'"));

        const string marker = "Expected one of:";
        var markerIndex = diagnostic.Message.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"diagnostic missing '{marker}': {diagnostic.Message}");
        var enumeration = diagnostic.Message[(markerIndex + marker.Length)..];
        Assert.Contains("Collector", enumeration);
        Assert.DoesNotContain("EvidenceCollector", enumeration);
        Assert.DoesNotContain("AttestationTemplate", enumeration);
    }

    // The v1 top-level asset kinds, now folded into Asset.type. They are the likeliest stale-config
    // mistake because the same words are still legal `type` values, so a document authoring one as its
    // `kind` must error rather than quietly load nothing.
    [Theory]
    [InlineData("Vendor")]
    [InlineData("Organisation")]
    [InlineData("Machine")]
    public void RetiredAssetKindsAreNowUnknown(string kind)
    {
        using var dir = TempConfig.Create(
            ("x.yaml", $"""
                apiVersion: freeboard.dev/v1alpha1
                kind: {kind}
                id: asset-a
                title: T
                source: declared
                """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Empty(result.Config.Assets);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Message.Contains($"Unknown kind '{kind}'"));

        const string marker = "Expected one of:";
        var markerIndex = diagnostic.Message.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"diagnostic missing '{marker}': {diagnostic.Message}");
        var enumeration = diagnostic.Message[(markerIndex + marker.Length)..];
        Assert.Contains("Asset", enumeration);
        Assert.DoesNotContain(kind, enumeration);
    }

    [Fact]
    public void LoadOrderMatchesNormalizedPathThenInFileOrder()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "fixtures", "order");

        var result = ConfigLoader.Load(fixtureDir);

        Assert.Equal(["a1", "a2", "b1", "b2"], result.Config.Standards.Select(s => s.Id).ToArray());
    }
}
