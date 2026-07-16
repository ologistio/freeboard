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
