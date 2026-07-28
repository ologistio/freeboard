using Freeboard.Core.GitOps;

namespace Freeboard.Core.Tests;

/// <summary>
/// Covers the Integration kind (persisted as an IntegrationConnection) and the type-conditional
/// Collector fields that bind to it (provider, connection, and the config checks list): distinct kind
/// routing, required fields, the closed provider token, absolute base_url, the discovery_cadence token,
/// the optional vendor reference, duplicate and configuration-key-unsafe ids, the provider/connection
/// conditional rules, and each tracked check's shape and severity. The loader and validator never throw
/// or print.
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

    private const string ValidOwnerCompany = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: org-a
        title: Org A
        type: Company
        source: declared
        """;

    private const string ValidVendor = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: vendor-a
        title: Vendor A
        type: Vendor
        source: declared
        owner: org-a
        """;

    private const string ValidConnection = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Integration
        id: fleet-prod
        title: Fleet Production
        provider: fleet
        base_url: https://fleet.example.com
        discovery_cadence: daily
        vendor: vendor-a
        """;

    /// <summary>Base set plus a connection; the collector under test is appended.</summary>
    private static string ValidSet(string collector, string connection = ValidConnection) =>
        $"{ValidStandard}\n---\n{ValidRequirement}\n---\n{ControlWithEvaluation}\n---\n{ValidOwnerCompany}\n---\n{ValidVendor}\n---\n{connection}\n---\n{collector}";

    private const string IntegrationCollector = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Collector
        id: collector-a
        title: Endpoint MFA
        control: ctrl-a
        type: integration
        provider: fleet
        frequency: daily
        connection: fleet-prod
        config:
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
        var collector = Assert.Single(result.Config.Collectors);
        Assert.Equal("fleet", collector.Provider);
        Assert.Equal("fleet-prod", collector.Connection);
        Assert.Equal(["mfa-enforced", "disk-encrypted"], collector.Config.Checks.Select(c => c.Name).ToArray());
        Assert.Equal("12", collector.Config.Checks[0].SourceKey);
        Assert.Equal("Hard", collector.Config.Checks[0].Severity);
        Assert.Equal("Soft", collector.Config.Checks[1].Severity);
    }

    [Fact]
    public void UnknownKindMessageListsIntegration()
    {
        using var dir = TempConfig.Create(("x.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Nope
            id: x
            """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Integration"));
    }

    [Fact]
    public void RetiredIntegrationConnectionKindIsNowUnknown()
    {
        // The authored kind is Integration; the retired two-word token no longer validates and there is
        // no backwards-compatible acceptance of it. "Integration" is a substring of "IntegrationConnection",
        // so a plain Contains("Integration") would pass even if the old token lingered. Inspect only the
        // valid-kinds enumeration (after "Expected one of:") to prove the old token is gone there.
        using var dir = TempConfig.Create(("x.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: IntegrationConnection
            id: fleet-prod
            title: Fleet Production
            provider: fleet
            base_url: https://fleet.example.com
            discovery_cadence: daily
            """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Empty(result.Config.IntegrationConnections);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            d => d.Message.Contains("Unknown kind 'IntegrationConnection'"));

        const string marker = "Expected one of:";
        var markerIndex = diagnostic.Message.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"diagnostic missing '{marker}': {diagnostic.Message}");
        var enumeration = diagnostic.Message[(markerIndex + marker.Length)..];
        Assert.Contains("Integration", enumeration);
        Assert.DoesNotContain("IntegrationConnection", enumeration);
    }

    [Fact]
    public void ExplicitNullChecksNormalizesToEmptyList()
    {
        var collector = """
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            provider: fleet
            frequency: daily
            connection: fleet-prod
            config:
              checks:
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet(collector)));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Empty(result.Diagnostics);
        var loaded = Assert.Single(result.Config.Collectors);
        Assert.NotNull(loaded.Config.Checks);
        Assert.Empty(loaded.Config.Checks);
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
            kind: Integration
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
            kind: Integration
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
            kind: Integration
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
            kind: Integration
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
            kind: Integration
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
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Duplicate Integration id 'fleet-prod'"));
    }

    [Fact]
    public void ConnectionIdWithColonFails()
    {
        var connection = """
            apiVersion: freeboard.dev/v1alpha1
            kind: Integration
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
            kind: Integration
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
            kind: Integration
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
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            provider: fleet
            frequency: daily
            config:
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
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            provider: fleet
            frequency: daily
            connection: fleet-missing
            config:
              checks:
                - source_key: "12"
                  name: mfa-enforced
                  severity: Hard
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet(collector)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("unknown Integration id 'fleet-missing'"));
    }

    [Fact]
    public void IntegrationCollectorEmptyChecksFails()
    {
        // The (integration, fleet) schema is what requires a non-empty checks list, and requiredness is
        // evaluated on the value, so an omitted config fails the same way an empty list would.
        var collector = """
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            provider: fleet
            frequency: daily
            connection: fleet-prod
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet(collector)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("missing required config key 'checks'"));
    }

    [Fact]
    public void IntegrationCollectorNullChecksItemReportsMissingFields()
    {
        // A blank list item ("checks:\n  -") deserializes to a null check. It is kept as an empty Check,
        // not silently dropped, so the validator reports its missing fields rather than accepting one valid
        // check plus a malformed blank one.
        var collector = """
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            provider: fleet
            frequency: daily
            connection: fleet-prod
            config:
              checks:
                - source_key: "12"
                  name: mfa-enforced
                  severity: Hard
                -
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet(collector)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("collector-a") && d.Message.Contains("check source_key"));
    }

    [Fact]
    public void ConnectionOnNonIntegrationCollectorFails()
    {
        var collector = """
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
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
        // No non-integration pair registers `checks`, so this is an unregistered config key rather than a
        // dedicated type rule.
        var collector = """
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: script
            frequency: weekly
            config:
              checks:
                - source_key: "12"
                  name: mfa-enforced
                  severity: Hard
            """;
        using var dir = TempConfig.Create(("all.yaml", ValidSet(collector)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown field 'checks' on Collector config."));
    }

    [Fact]
    public void UnknownCheckSeverityFails()
    {
        var collector = """
            apiVersion: freeboard.dev/v1alpha1
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            provider: fleet
            frequency: daily
            connection: fleet-prod
            config:
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
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            provider: fleet
            frequency: daily
            connection: fleet-prod
            config:
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
            kind: Collector
            id: collector-a
            title: T
            control: ctrl-a
            type: integration
            provider: fleet
            frequency: daily
            connection: fleet-prod
            config:
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
        var collector = Assert.Single(result.Config.Collectors);
        Assert.Equal(2, collector.Config.Checks.Count);
        Assert.Equal([("12", "mfa-enforced", "Hard"), ("34", "disk-encrypted", "Soft")],
            collector.Config.Checks.Select(c => (c.SourceKey, c.Name, c.Severity)).ToArray());
        // A Fleet policy id not in the authored list is absent, so it is untracked.
        Assert.DoesNotContain(collector.Config.Checks, c => c.SourceKey == "99");
    }
}
