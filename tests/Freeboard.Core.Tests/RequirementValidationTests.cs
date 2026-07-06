using Freeboard.Core.GitOps;

namespace Freeboard.Core.Tests;

/// <summary>
/// Covers the Requirement kind and the new Standard metadata: distinct kind routing, required
/// and optional fields, the citation and source_url URL checks, and the repointed
/// Control.maps_to -> Requirement id resolution.
/// </summary>
public sealed class RequirementValidationTests
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

    [Fact]
    public void RequirementLoadsAndRoutesDistinctlyFromControl()
    {
        using var dir = TempConfig.Create(("all.yaml", $"{ValidStandard}\n---\n{ValidRequirement}"));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.Config.Controls);
        var requirement = Assert.Single(result.Config.Requirements);
        Assert.Equal("req-a", requirement.Id);
        Assert.Equal("std-a", requirement.Standard);
        Assert.Equal("Theme A", requirement.Theme);
        Assert.Equal("Do the thing.", requirement.Statement);
        Assert.Equal("https://example.com/a", requirement.CitationUrl);
    }

    [Fact]
    public void ValidRequirementSetPassesValidation()
    {
        using var dir = TempConfig.Create(("all.yaml", $"{ValidStandard}\n---\n{ValidRequirement}"));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void RequirementWithUnknownStandardFails()
    {
        using var dir = TempConfig.Create(("req.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Requirement
            id: req-a
            title: Requirement A
            standard: std-missing
            theme: Theme A
            statement: Do the thing.
            citation_label: Source A
            citation_url: https://example.com/a
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("req-a") && d.Message.Contains("unknown Standard id 'std-missing'"));
    }

    [Fact]
    public void RequirementMissingStatementFails()
    {
        using var dir = TempConfig.Create(("all.yaml", $"""
            {ValidStandard}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Requirement
            id: req-a
            title: Requirement A
            standard: std-a
            theme: Theme A
            citation_label: Source A
            citation_url: https://example.com/a
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("req-a") && d.Message.Contains("statement"));
    }

    [Fact]
    public void UnknownFieldOnRequirementIsRejected()
    {
        using var dir = TempConfig.Create(("req.yaml", $"{ValidRequirement}\ncolour: blue\n"));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown field 'colour'") && d.Message.Contains("Requirement"));
    }

    [Fact]
    public void MalformedCitationUrlFails()
    {
        using var dir = TempConfig.Create(("all.yaml", $"""
            {ValidStandard}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Requirement
            id: req-a
            title: Requirement A
            standard: std-a
            theme: Theme A
            statement: Do the thing.
            citation_label: Source A
            citation_url: not-a-url
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("req-a") && d.Message.Contains("malformed citation_url"));
    }

    [Fact]
    public void OmittedGuidanceReadsBackAsEmpty()
    {
        using var dir = TempConfig.Create(("req.yaml", ValidRequirement));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Empty(Assert.Single(result.Config.Requirements).Guidance);
    }

    [Fact]
    public void ControlMapsToResolvesToRequirement()
    {
        using var dir = TempConfig.Create(("all.yaml", $"""
            {ValidStandard}
            ---
            {ValidRequirement}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Control
            id: ctrl-a
            title: Control A
            maps_to:
              - req-a
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void ControlWithDuplicateMapsToRequirementFails()
    {
        using var dir = TempConfig.Create(("all.yaml", $"""
            {ValidStandard}
            ---
            {ValidRequirement}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Control
            id: ctrl-a
            title: Control A
            maps_to:
              - req-a
              - req-a
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("ctrl-a") && d.Message.Contains("duplicate Requirement id 'req-a'"));
    }

    [Fact]
    public void StandardMissingVersionAndAuthorityFails()
    {
        using var dir = TempConfig.Create(("std.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Standard
            id: std-a
            title: Standard A
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("std-a") && d.Message.Contains("version"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("std-a") && d.Message.Contains("authority"));
    }

    [Fact]
    public void StandardWithWhitespaceVersionFails()
    {
        using var dir = TempConfig.Create(("std.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Standard
            id: std-a
            title: Standard A
            version: "   "
            authority: Example Authority
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("std-a") && d.Message.Contains("version"));
    }

    [Fact]
    public void StandardWithOmittedPublisherAndSourceUrlIsValid()
    {
        using var dir = TempConfig.Create(("std.yaml", ValidStandard));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var standard = Assert.Single(result.Config.Standards);
        Assert.Empty(standard.Publisher);
        Assert.Empty(standard.SourceUrl);
    }

    [Fact]
    public void StandardWithBlankPublisherAndSourceUrlIsValid()
    {
        using var dir = TempConfig.Create(("std.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Standard
            id: std-a
            title: Standard A
            version: "1.0"
            authority: Example Authority
            publisher: "   "
            source_url: "   "
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void MalformedSourceUrlFails()
    {
        using var dir = TempConfig.Create(("std.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: Standard
            id: std-a
            title: Standard A
            version: "1.0"
            authority: Example Authority
            source_url: not-a-url
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("std-a") && d.Message.Contains("malformed source_url"));
    }
}
