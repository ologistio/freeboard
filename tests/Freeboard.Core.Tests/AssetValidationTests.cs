using Freeboard.Core.GitOps;

namespace Freeboard.Core.Tests;

/// <summary>
/// Covers the unified Asset kind: the type/source tokens, the mutually-exclusive parent/owner edges
/// with their carrier-type and target-type rules, and the severity split - a dangling edge, a parent
/// cycle, and a missing required read anchor are non-blocking warnings, while a bad token, a wrong-typed
/// edge target, and an authored discovered-only field are blocking errors. The loader and validator
/// never throw or print.
/// </summary>
public sealed class AssetValidationTests
{
    private const string Company = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: org-a
        title: Org A
        type: Company
        source: declared
        """;

    [Fact]
    public void ValidAssetTreeLoadsAndValidatesWithNoWarnings()
    {
        using var dir = TempConfig.Create(("assets.yaml", $"""
            {Company}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: dept-a
            title: Engineering
            type: Department
            source: declared
            parent: org-a
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: vendor-a
            title: Vendor A
            type: Vendor
            source: declared
            owner: org-a
            tier: Critical
            data_classes: [pii, payment-card]
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: machine-a
            title: Laptop A
            type: Machine
            source: declared
            parent: org-a
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Warnings);
        Assert.Equal(4, result.Config.Assets.Count);
        var vendor = result.Config.Assets.Single(a => a.Id == "vendor-a");
        Assert.Equal("Vendor", vendor.Type);
        Assert.Equal("org-a", vendor.Owner);
        Assert.Equal("Critical", vendor.Tier);
        Assert.Equal(["pii", "payment-card"], vendor.DataClasses);
    }

    [Fact]
    public void UnknownTypeFails()
    {
        using var dir = TempConfig.Create(("a.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: asset-a
            title: A
            type: Guild
            source: declared
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("asset-a") && d.Message.Contains("unknown type 'Guild'"));
    }

    [Fact]
    public void UnknownSourceFails()
    {
        using var dir = TempConfig.Create(("a.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: asset-a
            title: A
            type: Company
            source: imported
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("asset-a") && d.Message.Contains("unknown source 'imported'"));
    }

    [Fact]
    public void DiscoveredSourceFails()
    {
        // A discovered asset is written by ingest; authoring 'source: discovered' in config is an error.
        using var dir = TempConfig.Create(("a.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: asset-a
            title: A
            type: Machine
            source: discovered
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("asset-a") && d.Message.Contains("source 'discovered', which cannot be authored"));
    }

    [Fact]
    public void BothParentAndOwnerFails()
    {
        using var dir = TempConfig.Create(("a.yaml", $"""
            {Company}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: dept-a
            title: Engineering
            type: Department
            source: declared
            parent: org-a
            owner: org-a
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("dept-a") && d.Message.Contains("sets both 'parent' and 'owner'"));
    }

    [Fact]
    public void VendorWithParentFails()
    {
        using var dir = TempConfig.Create(("a.yaml", $"""
            {Company}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: vendor-a
            title: Vendor A
            type: Vendor
            source: declared
            parent: org-a
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vendor-a") && d.Message.Contains("is a Vendor and cannot set 'parent'"));
    }

    [Fact]
    public void NonVendorWithOwnerFails()
    {
        using var dir = TempConfig.Create(("a.yaml", $"""
            {Company}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: dept-a
            title: Engineering
            type: Department
            source: declared
            owner: org-a
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("dept-a") && d.Message.Contains("sets 'owner' but is not a Vendor"));
    }

    [Fact]
    public void DanglingParentIsWarningNotError()
    {
        using var dir = TempConfig.Create(("a.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: dept-a
            title: Engineering
            type: Department
            source: declared
            parent: org-missing
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Contains(result.Warnings, d => d.Message.Contains("dept-a") && d.Message.Contains("unknown parent 'org-missing'"));
    }

    [Fact]
    public void DanglingOwnerIsWarningNotError()
    {
        using var dir = TempConfig.Create(("a.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: vendor-a
            title: Vendor A
            type: Vendor
            source: declared
            owner: org-missing
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Contains(result.Warnings, d => d.Message.Contains("vendor-a") && d.Message.Contains("unknown owner 'org-missing'"));
    }

    [Fact]
    public void ParentTargetOfWrongTypeFails()
    {
        // A parent must be a Company or Department; pointing it at a Vendor is a blocking error, not a
        // tolerated dangling edge, because the target resolves to the wrong kind.
        using var dir = TempConfig.Create(("a.yaml", $"""
            {Company}
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
            kind: Asset
            id: dept-a
            title: Engineering
            type: Department
            source: declared
            parent: vendor-a
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("dept-a") && d.Message.Contains("parent 'vendor-a' must be a Company or Department asset"));
    }

    [Fact]
    public void OwnerTargetOfWrongTypeFails()
    {
        using var dir = TempConfig.Create(("a.yaml", $"""
            {Company}
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
            kind: Asset
            id: vendor-b
            title: Vendor B
            type: Vendor
            source: declared
            owner: vendor-a
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vendor-b") && d.Message.Contains("owner 'vendor-a' must be a Company or Department asset"));
    }

    [Fact]
    public void ParentCycleIsWarningNotError()
    {
        using var dir = TempConfig.Create(("a.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: dept-a
            title: A
            type: Department
            source: declared
            parent: dept-a
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Contains(result.Warnings, d => d.Message.Contains("dept-a") && d.Message.Contains("part of a parent cycle"));
    }

    [Fact]
    public void VendorWithNoOwnerIsWarningNotError()
    {
        using var dir = TempConfig.Create(("a.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: vendor-a
            title: Vendor A
            type: Vendor
            source: declared
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Contains(result.Warnings, d => d.Message.Contains("vendor-a") && d.Message.Contains("is a Vendor with no owner"));
    }

    [Fact]
    public void MachineWithNoParentIsWarningNotError()
    {
        using var dir = TempConfig.Create(("a.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: machine-a
            title: Laptop A
            type: Machine
            source: declared
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Contains(result.Warnings, d => d.Message.Contains("machine-a") && d.Message.Contains("is a Machine with no parent"));
    }

    [Fact]
    public void RootCompanyOrDepartmentNeedsNoParent()
    {
        // A parent-less Company or Department is a legitimate root, so it must emit no missing-edge warning.
        using var dir = TempConfig.Create(("a.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: org-a
            title: Org A
            type: Company
            source: declared
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: dept-a
            title: Standalone Department
            type: Department
            source: declared
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void WhitespaceOnlyEdgeIsTreatedAsAbsent()
    {
        // A whitespace-only parent/owner is absent (spec and import agree), so it raises no wrong-carrier
        // error: the Vendor's blank parent and the Company's blank owner are both edge-less, not miswired.
        using var dir = TempConfig.Create(("a.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: org-a
            title: Org A
            type: Company
            source: declared
            owner: " "
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: vendor-a
            title: Vendor A
            type: Vendor
            source: declared
            parent: " "
            owner: org-a
            tier: Low
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("cannot set 'parent'"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("sets 'owner' but is not a Vendor"));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void VendorWithWhitespaceOwnerStillWarnsMissingOwner()
    {
        // A whitespace-only owner is absent, so the Vendor still trips the missing-owner read-anchor warning.
        using var dir = TempConfig.Create(("a.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: vendor-a
            title: Vendor A
            type: Vendor
            source: declared
            owner: " "
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Contains(result.Warnings, d => d.Message.Contains("vendor-a") && d.Message.Contains("is a Vendor with no owner"));
    }

    [Fact]
    public void MachineWithWhitespaceParentStillWarnsMissingParent()
    {
        // A whitespace-only parent is absent, so the Machine still trips the missing-parent read-anchor warning.
        using var dir = TempConfig.Create(("a.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: machine-a
            title: Laptop A
            type: Machine
            source: declared
            parent: " "
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Contains(result.Warnings, d => d.Message.Contains("machine-a") && d.Message.Contains("is a Machine with no parent"));
    }

    [Fact]
    public void NonBlankEdgeStillValidates()
    {
        // A present, well-typed owner resolves as before: no missing-edge warning, no error.
        using var dir = TempConfig.Create(("a.yaml", $"""
            {Company}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: vendor-a
            title: Vendor A
            type: Vendor
            source: declared
            owner: org-a
            tier: Medium
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void AuthoredDiscoveredOnlyFieldIsLoaderError()
    {
        // A discovered-only column (written by ingest) authored on a declared Asset gets a distinct
        // loader diagnostic, separate from the source:discovered validator error and the generic
        // unknown-field message.
        using var dir = TempConfig.Create(("a.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: machine-a
            title: Laptop A
            type: Machine
            source: declared
            state: Seen
            """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Field 'state' on Asset is discovered-only and cannot be authored"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.Contains("Unknown field 'state'"));
    }

    [Fact]
    public void UnknownTierFails()
    {
        using var dir = TempConfig.Create(("a.yaml", $"""
            {Company}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: vendor-a
            title: Vendor A
            type: Vendor
            source: declared
            owner: org-a
            tier: Severe
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vendor-a") && d.Message.Contains("unknown tier 'Severe'"));
    }

    [Fact]
    public void UnknownDataClassFails()
    {
        using var dir = TempConfig.Create(("a.yaml", $"""
            {Company}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: vendor-a
            title: Vendor A
            type: Vendor
            source: declared
            owner: org-a
            tier: High
            data_classes: [pii, biometric]
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vendor-a") && d.Message.Contains("unknown data class 'biometric'"));
    }

    [Fact]
    public void DuplicateDataClassFails()
    {
        // Reported rather than silently deduped: a repeated token is an authoring mistake.
        using var dir = TempConfig.Create(("a.yaml", $"""
            {Company}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: vendor-a
            title: Vendor A
            type: Vendor
            source: declared
            owner: org-a
            tier: High
            data_classes: [pii, pii]
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vendor-a") && d.Message.Contains("duplicate data class 'pii'"));
    }

    [Theory]
    [InlineData("tier: High")]
    [InlineData("data_classes: [pii]")]
    public void RiskProfileOnNonVendorFails(string field)
    {
        using var dir = TempConfig.Create(("a.yaml", $"""
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: org-a
            title: Org A
            type: Company
            source: declared
            {field}
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("org-a") && d.Message.Contains("but is not a Vendor; only a vendor carries a risk profile"));
    }

    [Fact]
    public void VendorWithNoTierIsWarningNotError()
    {
        // The tier only colors a tag, so it must not fail a sync when the owner edge - which decides
        // whether the vendor is visible at all - only warns.
        using var dir = TempConfig.Create(("a.yaml", $"""
            {Company}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: vendor-a
            title: Vendor A
            type: Vendor
            source: declared
            owner: org-a
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("vendor-a", warning.Message, StringComparison.Ordinal);
        Assert.Contains("is a Vendor with no tier", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("data_classes: []")]
    [InlineData("data_classes:")]
    public void AbsentOrEmptyDataClassesIsSilent(string field)
    {
        // A vendor holding none of your regulated data is a real state, so it warrants no diagnostic.
        using var dir = TempConfig.Create(("a.yaml", $"""
            {Company}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: vendor-a
            title: Vendor A
            type: Vendor
            source: declared
            owner: org-a
            tier: Low
            {field}
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Config.Assets.Single(a => a.Id == "vendor-a").DataClasses);
    }

    private const string Soc2 = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Standard
        id: std-soc2
        title: SOC 2
        version: "2017"
        authority: AICPA
        """;

    private const string Iso27001 = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Standard
        id: std-iso27001
        title: ISO/IEC 27001
        version: "2022"
        authority: ISO
        """;

    // A vendor document carrying the standards the assurance cases reference, so each case authors only
    // the assurances block under test.
    private static string VendorWith(string assurances) => $"""
        {Soc2}
        ---
        {Iso27001}
        ---
        {Company}
        ---
        apiVersion: freeboard.dev/v1alpha1
        kind: Asset
        id: vendor-a
        title: Vendor A
        type: Vendor
        source: declared
        owner: org-a
        tier: High
        {assurances}
        """;

    [Fact]
    public void VendorWithAssurancesLoads()
    {
        using var dir = TempConfig.Create(("a.yaml", VendorWith("""
            assurances:
              - standard: std-soc2
                expires: 2027-03-27
              - standard: std-iso27001
                expires: 2026-11-01
                warn_days: 30
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Warnings);
        var assurances = result.Config.Assets.Single(a => a.Id == "vendor-a").Assurances;
        Assert.Equal(2, assurances.Count);
        Assert.Equal("std-soc2", assurances[0].Standard);
        Assert.Equal("2027-03-27", assurances[0].Expires);
        Assert.Equal(string.Empty, assurances[0].WarnDays);
        Assert.Equal("30", assurances[1].WarnDays);
    }

    [Fact]
    public void AssurancesOnNonVendorFails()
    {
        using var dir = TempConfig.Create(("a.yaml", $"""
            {Soc2}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: org-a
            title: Org A
            type: Company
            source: declared
            assurances:
              - standard: std-soc2
                expires: 2027-03-27
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("org-a")
            && d.Message.Contains("sets 'assurances' but is not a Vendor"));
    }

    [Fact]
    public void DanglingAssuranceStandardFails()
    {
        // An Error, not the Warning a dangling parent or owner draws: a certification names a document in
        // the same config, matching the Scope target references.
        using var dir = TempConfig.Create(("a.yaml", VendorWith("""
            assurances:
              - standard: std-nope
                expires: 2027-03-27
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vendor-a")
            && d.Message.Contains("unknown Standard id 'std-nope'"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("expires:")]
    [InlineData("expires: soon")]
    [InlineData("expires: 2027-13-01")]
    public void MissingOrUnparseableExpiryFails(string expires)
    {
        using var dir = TempConfig.Create(("a.yaml", VendorWith($"""
            assurances:
              - standard: std-soc2
                {expires}
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vendor-a")
            && d.Message.Contains("std-soc2")
            && (d.Message.Contains("missing required field 'expires'") || d.Message.Contains("malformed expires")));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("soon")]
    [InlineData("1.5")]
    public void InvalidWarnDaysFails(string warnDays)
    {
        using var dir = TempConfig.Create(("a.yaml", VendorWith($"""
            assurances:
              - standard: std-soc2
                expires: 2027-03-27
                warn_days: {warnDays}
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vendor-a")
            && d.Message.Contains($"invalid warn_days '{warnDays}'"));
    }

    [Fact]
    public void ZeroWarnDaysIsAccepted()
    {
        using var dir = TempConfig.Create(("a.yaml", VendorWith("""
            assurances:
              - standard: std-soc2
                expires: 2027-03-27
                warn_days: 0
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void DuplicateAssuranceStandardFails()
    {
        using var dir = TempConfig.Create(("a.yaml", VendorWith("""
            assurances:
              - standard: std-soc2
                expires: 2027-03-27
              - standard: std-soc2
                expires: 2028-03-27
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("vendor-a")
            && d.Message.Contains("duplicate assurance standard 'std-soc2'"));
    }

    [Fact]
    public void DuplicateAssuranceStandardFailsEvenWhenTheStandardIsUnknown()
    {
        using var dir = TempConfig.Create(("a.yaml", VendorWith("""
            assurances:
              - standard: std-nowhere
                expires: 2027-03-27
              - standard: std-nowhere
                expires: 2028-03-27
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        // Both are true and each is its own mistake: declaring the standard must not leave a duplicate
        // that nothing reported.
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("unknown Standard id 'std-nowhere'"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("duplicate assurance standard 'std-nowhere'"));
    }

    [Fact]
    public void UnknownFieldOnAssuranceIsRejected()
    {
        // The document-level check reads top-level keys only, and the deserializer ignores unmatched
        // properties, so without the nested check this key vanishes.
        using var dir = TempConfig.Create(("a.yaml", VendorWith("""
            assurances:
              - standard: std-soc2
                expires: 2027-03-27
                auditor:
            """)));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown field 'auditor' on Asset assurance"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("assurances: []")]
    [InlineData("assurances:")]
    public void AbsentOrEmptyAssurancesIsSilent(string field)
    {
        // Holding no certification is a real and common state, so it warrants no diagnostic.
        using var dir = TempConfig.Create(("a.yaml", VendorWith(field)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Config.Assets.Single(a => a.Id == "vendor-a").Assurances);
    }

    [Fact]
    public void PastExpiryProducesNoDiagnostic()
    {
        // Validation reads no clock, so an already-lapsed certificate is a state the read surfaces render
        // rather than a diagnostic that would make two runs over one config disagree.
        using var dir = TempConfig.Create(("a.yaml", VendorWith("""
            assurances:
              - standard: std-soc2
                expires: 2001-01-01
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void NullAssuranceEntryIsReportedNotDropped()
    {
        using var dir = TempConfig.Create(("a.yaml", VendorWith("""
            assurances:
              -
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.Single(result.Config.Assets.Single(a => a.Id == "vendor-a").Assurances);
        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("an assurance with no 'standard'"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("missing required field 'expires'"));
    }

    [Fact]
    public void UnknownFieldOnAssetIsRejected()
    {
        using var dir = TempConfig.Create(("a.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Asset
            id: org-a
            title: Org A
            type: Company
            source: declared
            colour: blue
            """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown field 'colour' on Asset"));
    }
}
