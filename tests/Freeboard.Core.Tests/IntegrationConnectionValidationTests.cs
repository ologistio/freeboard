using Freeboard.Core.GitOps;

namespace Freeboard.Core.Tests;

/// <summary>
/// Covers the IntegrationConnection kind and the two type-conditional EvidenceCollector fields
/// (connection, checks): distinct kind routing, required fields, the closed provider token, absolute
/// base_url, the discovery_cadence token, the optional vendor reference, duplicate and
/// configuration-key-unsafe ids, the connection/checks conditional rules, and each tracked check's
/// shape and severity. The loader and validator never throw or print.
/// </summary>
public sealed class IntegrationConnectionValidationTests
{
    private const string ValidStandard = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Standard
        id: std-a
        title: Standard A
        version: "1.0"
        authority: Example Authority
        """;

    private const string ValidRequirement = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Requirement
        id: req-a
        title: Requirement A
        standard: std-a
        theme: Theme A
        statement: Do the thing.
        citation_label: Source A
        citation_url: https://example.com/a
        """;

    private const string ControlWithEvaluation = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Control
        id: ctrl-a
        title: Control A
        maps_to:
          - req-a
        evaluation: all
        """;

    private const string ValidVendor = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Vendor
        id: vendor-a
        title: Vendor A
        """;

    private const string ValidConnection = """
        apiVersion: freeboard.dev/v1alpha1
        kind: IntegrationConnection
        id: fleet-prod
        title: Fleet Production
        provider: fleet
        base_url: https://fleet.example.com
        discovery_cadence: daily
        vendor: vendor-a
        """;

    /// <summary>Base set plus a connection; the collector under test is appended.</summary>
    private static string ValidSet(string collector, string connection = ValidConnection) =>
        $"{ValidStandard}\n---\n{ValidRequirement}\n---\n{ControlWithEvaluation}\n---\n{ValidVendor}\n---\n{connection}\n---\n{collector}";

    private const string IntegrationCollector = """
        apiVersion: freeboard.dev/v1alpha1
        kind: EvidenceCollector
        id: collector-a
        title: Endpoint MFA
        control: ctrl-a
        type: integration
        frequency: daily
        connection: fleet-prod
        checks:
          - source_key: "12"
            name: mfa-enforced
            severity: Hard
          - source_key: "34"
            name: disk-encrypted
            severity: Soft
        """;

    [Fact]
    public void IntegrationConnectionLoadsIntoTypedModel()
    {
        using var dir = TempConfig.Create(("c.yaml", $"{ValidVendor}\n---\n{ValidConnection}"));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Empty(result.Diagnostics);
        var connection = Assert.Single(result.Config.IntegrationConnections);
        Assert.Equal("fleet-prod", connection.Id);
        Assert.Equal("fleet", connection.Provider);
        Assert.Equal("https://fleet.example.com", connection.BaseUrl);
        Assert.Equal("daily", connection.DiscoveryCadence);
        Assert.Equal("vendor-a", connection.Vendor);
    }

    [Fact]
    public void IntegrationCollectorLoadsWithConnectionAndChecks()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet(IntegrationCollector)));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Empty(result.Diagnostics);
        var collector = Assert.Single(result.Config.EvidenceCollectors);
        Assert.Equal("fleet-prod", collector.Connection);
        Assert.Equal(["mfa-enforced", "disk-encrypted"], collector.Checks.Select(c => c.Name).ToArray());
        Assert.Equal("12", collector.Checks[0].SourceKey);
        Assert.Equal("Hard", collector.Checks[0].Severity);
        Assert.Equal("Soft", collector.Checks[1].Severity);
    }

    [Fact]
    public void UnknownKindMessageIncludesIntegrationConnection()
    {
        using var dir = TempConfig.Create(("x.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Nope
            id: x
            """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("IntegrationConnection"));
    }

    [Fact]
    public void ExplicitNullChecksNormalizesToEmptyList()
    {
        using var dir = TempConfig.Create(("all.yaml", $"{ValidStandard}\n---\n{ValidRequirement}\n---\n" + """
            apiVersion: freeboard.dev/v1alpha1
            kind: Control
            id: ctrl-a
            title: Control A
            maps_to:
              - req-a
            evaluation: all
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: weekly
            checks:
            """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Empty(result.Diagnostics);
        var collector = Assert.Single(result.Config.EvidenceCollectors);
        Assert.NotNull(collector.Checks);
        Assert.Empty(collector.Checks);
    }

    [Fact]
    public void ValidIntegrationCollectorAndConnectionValidatesClean()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet(IntegrationCollector)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void MissingConnectionBaseUrlFails()
    {
        var connection = """
            apiVersion: freeboard.dev/v1alpha1
            kind: IntegrationConnection
            id: fleet-prod
            title: Fleet Production
            provider: fleet
            discovery_cadence: daily
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet(IntegrationCollector, connection)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("fleet-prod") && d.Message.Contains("base_url"));
    }

    [Fact]
    public void UnknownProviderFails()
    {
        var connection = """
            apiVersion: freeboard.dev/v1alpha1
            kind: IntegrationConnection
            id: fleet-prod
            title: Fleet Production
            provider: crowdstrike
            base_url: https://fleet.example.com
            discovery_cadence: daily
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet(IntegrationCollector, connection)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("fleet-prod") && d.Message.Contains("unknown provider 'crowdstrike'"));
    }

    [Fact]
    public void MalformedBaseUrlFails()
    {
        var connection = """
            apiVersion: freeboard.dev/v1alpha1
            kind: IntegrationConnection
            id: fleet-prod
            title: Fleet Production
            provider: fleet
            base_url: not-a-url
            discovery_cadence: daily
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet(IntegrationCollector, connection)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("fleet-prod") && d.Message.Contains("malformed base_url"));
    }

    [Fact]
    public void UnknownDiscoveryCadenceFails()
    {
        var connection = """
            apiVersion: freeboard.dev/v1alpha1
            kind: IntegrationConnection
            id: fleet-prod
            title: Fleet Production
            provider: fleet
            base_url: https://fleet.example.com
            discovery_cadence: hourly
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet(IntegrationCollector, connection)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("fleet-prod") && d.Message.Contains("unknown discovery_cadence 'hourly'"));
    }

    [Fact]
    public void DanglingVendorReferenceFails()
    {
        var connection = """
            apiVersion: freeboard.dev/v1alpha1
            kind: IntegrationConnection
            id: fleet-prod
            title: Fleet Production
            provider: fleet
            base_url: https://fleet.example.com
            discovery_cadence: daily
            vendor: vendor-missing
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet(IntegrationCollector, connection)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("fleet-prod") && d.Message.Contains("unknown Vendor id 'vendor-missing'"));
    }

    [Fact]
    public void DuplicateConnectionIdFails()
    {
        using var dir = TempConfig.Create(("all.yaml", $"{ValidVendor}\n---\n{ValidConnection}\n---\n{ValidConnection}"));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Duplicate IntegrationConnection id 'fleet-prod'"));
    }

    [Fact]
    public void ConnectionIdWithColonFails()
    {
        var connection = """
            apiVersion: freeboard.dev/v1alpha1
            kind: IntegrationConnection
            id: "fleet:prod"
            title: Fleet Production
            provider: fleet
            base_url: https://fleet.example.com
            discovery_cadence: daily
            """;
        using var dir = TempConfig.Create(("c.yaml", connection));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("fleet:prod") && d.Message.Contains("':' or '__'"));
    }

    [Fact]
    public void ConnectionIdWithDoubleUnderscoreFails()
    {
        var connection = """
            apiVersion: freeboard.dev/v1alpha1
            kind: IntegrationConnection
            id: fleet__prod
            title: Fleet Production
            provider: fleet
            base_url: https://fleet.example.com
            discovery_cadence: daily
            """;
        using var dir = TempConfig.Create(("c.yaml", connection));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("fleet__prod") && d.Message.Contains("':' or '__'"));
    }

    [Fact]
    public void ConnectionIdsCollidingOnlyByCaseFail()
    {
        var second = """
            apiVersion: freeboard.dev/v1alpha1
            kind: IntegrationConnection
            id: Fleet-Prod
            title: Fleet Production Two
            provider: fleet
            base_url: https://fleet.example.com
            discovery_cadence: daily
            """;
        using var dir = TempConfig.Create(("all.yaml", $"{ValidVendor}\n---\n{ValidConnection}\n---\n{second}"));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Fleet-Prod") && d.Message.Contains("collides case-insensitively"));
    }

    [Fact]
    public void IntegrationCollectorMissingConnectionFails()
    {
        var collector = """
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            frequency: daily
            checks:
              - source_key: "12"
                name: mfa-enforced
                severity: Hard
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet(collector)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("missing required field 'connection'"));
    }

    [Fact]
    public void IntegrationCollectorDanglingConnectionFails()
    {
        var collector = """
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            frequency: daily
            connection: fleet-missing
            checks:
              - source_key: "12"
                name: mfa-enforced
                severity: Hard
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet(collector)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("unknown IntegrationConnection id 'fleet-missing'"));
    }

    [Fact]
    public void IntegrationCollectorEmptyChecksFails()
    {
        var collector = """
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            frequency: daily
            connection: fleet-prod
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet(collector)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("missing a non-empty 'checks'"));
    }

    [Fact]
    public void ConnectionOnNonIntegrationCollectorFails()
    {
        var collector = """
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: weekly
            connection: fleet-prod
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet(collector)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("declares 'connection'"));
    }

    [Fact]
    public void ChecksOnNonIntegrationCollectorFails()
    {
        var collector = """
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: weekly
            checks:
              - source_key: "12"
                name: mfa-enforced
                severity: Hard
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet(collector)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("declares 'checks'"));
    }

    [Fact]
    public void UnknownCheckSeverityFails()
    {
        var collector = """
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            frequency: daily
            connection: fleet-prod
            checks:
              - source_key: "12"
                name: mfa-enforced
                severity: Critical
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet(collector)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("unknown severity 'Critical'"));
    }

    [Fact]
    public void DuplicateCheckNameFails()
    {
        var collector = """
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            frequency: daily
            connection: fleet-prod
            checks:
              - source_key: "12"
                name: mfa-enforced
                severity: Hard
              - source_key: "34"
                name: mfa-enforced
                severity: Soft
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet(collector)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("duplicate check name 'mfa-enforced'"));
    }

    [Fact]
    public void DuplicateCheckSourceKeyFails()
    {
        var collector = """
            apiVersion: freeboard.dev/v1alpha1
            kind: EvidenceCollector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            frequency: daily
            connection: fleet-prod
            checks:
              - source_key: "12"
                name: mfa-enforced
                severity: Hard
              - source_key: "12"
                name: disk-encrypted
                severity: Soft
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet(collector)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("duplicate check source_key '12'"));
    }

    [Fact]
    public void TrackedCheckSetEqualsExactlyTheAuthoredChecks()
    {
        // The authored checks list is the exhaustive tracked set. A provider-native id (Fleet policy)
        // absent from checks is not represented, so it changes nothing.
        using var dir = TempConfig.Create(("all.yaml", ValidSet(IntegrationCollector)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var collector = Assert.Single(result.Config.EvidenceCollectors);
        Assert.Equal(2, collector.Checks.Count);
        Assert.Equal([("12", "mfa-enforced", "Hard"), ("34", "disk-encrypted", "Soft")],
            collector.Checks.Select(c => (c.SourceKey, c.Name, c.Severity)).ToArray());
        // A Fleet policy id not in the authored list is absent, so it is untracked.
        Assert.DoesNotContain(collector.Checks, c => c.SourceKey == "99");
    }
}
