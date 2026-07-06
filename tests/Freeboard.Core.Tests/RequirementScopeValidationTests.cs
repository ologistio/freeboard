using Freeboard.Core.GitOps;

namespace Freeboard.Core.Tests;

/// <summary>
/// Covers the RequirementScope kind: distinct kind routing, required fields, resolvable
/// organisation and requirement references, the disposition enum, the unknown-field rejection
/// (including a stray `standard` field), and the unique (organisation, requirement) pair. The
/// loader and validator never throw or print.
/// </summary>
public sealed class RequirementScopeValidationTests
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

    private const string ValidOrganisation = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Organisation
        id: org-a
        title: Org A
        type: Company
        """;

    private const string ValidRequirementScope = """
        apiVersion: freeboard.dev/v1alpha1
        kind: RequirementScope
        id: rs-a
        title: Exclude req-a
        organisation: org-a
        requirement: req-a
        disposition: Out
        """;

    private static string ValidSet(string requirementScope) =>
        $"{ValidStandard}\n---\n{ValidRequirement}\n---\n{ValidOrganisation}\n---\n{requirementScope}";

    [Fact]
    public void RequirementScopeLoadsAndRoutesDistinctlyFromScope()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet(ValidRequirementScope)));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.Config.Scopes);
        var requirementScope = Assert.Single(result.Config.RequirementScopes);
        Assert.Equal("rs-a", requirementScope.Id);
        Assert.Equal("org-a", requirementScope.Organisation);
        Assert.Equal("req-a", requirementScope.Requirement);
        Assert.Equal("Out", requirementScope.Disposition);
    }

    [Fact]
    public void ValidRequirementScopePassesValidation()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet(ValidRequirementScope)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void UnknownOrganisationFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: RequirementScope
            id: rs-a
            title: Exclude req-a
            organisation: org-missing
            requirement: req-a
            disposition: Out
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("rs-a") && d.Message.Contains("unknown Organisation id 'org-missing'"));
    }

    [Fact]
    public void UnknownRequirementFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: RequirementScope
            id: rs-a
            title: Exclude req-a
            organisation: org-a
            requirement: req-missing
            disposition: Out
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("rs-a") && d.Message.Contains("unknown Requirement id 'req-missing'"));
    }

    [Fact]
    public void MissingRequirementFieldFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: RequirementScope
            id: rs-a
            title: Exclude req-a
            organisation: org-a
            disposition: Out
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("rs-a") && d.Message.Contains("requirement"));
    }

    [Fact]
    public void StrayStandardFieldIsRejected()
    {
        // RequirementScope carries no `standard`; the requirement fixes the standard.
        using var dir = TempConfig.Create(("rs.yaml", $"{ValidRequirementScope}\nstandard: std-a\n"));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown field 'standard'") && d.Message.Contains("RequirementScope"));
    }

    [Fact]
    public void BadDispositionFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: RequirementScope
            id: rs-a
            title: Exclude req-a
            organisation: org-a
            requirement: req-a
            disposition: Maybe
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("rs-a") && d.Message.Contains("unknown disposition 'Maybe'"));
    }

    [Fact]
    public void DuplicateOrganisationRequirementPairFails()
    {
        using var dir = TempConfig.Create(("all.yaml", $"""
            {ValidStandard}
            ---
            {ValidRequirement}
            ---
            {ValidOrganisation}
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
            kind: RequirementScope
            id: rs-b
            title: Re-include req-a
            organisation: org-a
            requirement: req-a
            disposition: In
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("organisation 'org-a'") && d.Message.Contains("requirement 'req-a'") && d.Message.Contains("more than once"));
    }

    [Fact]
    public void DuplicateIdFails()
    {
        using var dir = TempConfig.Create(("all.yaml", $"""
            {ValidStandard}
            ---
            {ValidRequirement}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: Requirement
            id: req-b
            title: Requirement B
            standard: std-a
            theme: Theme A
            statement: Do another thing.
            citation_label: Source B
            citation_url: https://example.com/b
            ---
            {ValidOrganisation}
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
            kind: RequirementScope
            id: rs-a
            title: Exclude req-b
            organisation: org-a
            requirement: req-b
            disposition: Out
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Duplicate RequirementScope id 'rs-a'"));
    }

    [Fact]
    public void ConfigWithNoRequirementScopesStillLoadsAndValidates()
    {
        using var dir = TempConfig.Create(("all.yaml", $"{ValidStandard}\n---\n{ValidRequirement}\n---\n{ValidOrganisation}"));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Config.RequirementScopes);
    }
}
