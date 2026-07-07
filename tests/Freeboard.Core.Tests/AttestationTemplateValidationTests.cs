using Freeboard.Core.GitOps;

namespace Freeboard.Core.Tests;

/// <summary>
/// Covers the AttestationTemplate kind: distinct kind routing, required fields, a resolvable control
/// reference, the type token set, the field/quiz shape (field types, the single-choice options rule,
/// unique option labels, the two-option minimum, answer-in-options), the optional pass_mark range check
/// (a malformed value is a diagnostic, not a crash), the training-vs-manual conditional rules, duplicate
/// ids, unknown fields, and the nested-list normalization. The loader and validator never throw or print.
/// </summary>
public sealed class AttestationTemplateValidationTests
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

    private const string ValidControl = """
        apiVersion: freeboard.dev/v1alpha1
        kind: Control
        id: ctrl-a
        title: Control A
        maps_to:
          - req-a
        """;

    private static string ValidSet(string template, string control = ValidControl) =>
        $"{ValidStandard}\n---\n{ValidRequirement}\n---\n{control}\n---\n{template}";

    [Fact]
    public void ManualTemplateLoadsWithFields()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
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
                options: [pass, pass-with-notes, fail]
              - id: notes
                label: Notes
                type: short-text
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var template = Assert.Single(result.Config.AttestationTemplates);
        Assert.Equal("attest-manual", template.Id);
        Assert.Equal("ctrl-a", template.Control);
        Assert.Equal("manual", template.Type);
        Assert.Equal(3, template.Fields.Count);
        Assert.Equal(["pass", "pass-with-notes", "fail"], template.Fields[1].Options.ToArray());
    }

    [Fact]
    public void TrainingTemplateLoadsWithQuizAndPassMark()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-training
            title: Phishing awareness
            control: ctrl-a
            type: training
            body: Read the guidance, then answer.
            pass_mark: 80
            quiz:
              - id: q1
                prompt: What should you do with an unexpected attachment?
                options: [Open it, Report it, Forward it]
                answer: Report it
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var template = Assert.Single(result.Config.AttestationTemplates);
        Assert.Equal("training", template.Type);
        Assert.Equal("80", template.PassMark);
        var item = Assert.Single(template.Quiz);
        Assert.Equal("q1", item.Id);
        Assert.Equal("Report it", item.Answer);
    }

    [Fact]
    public void ManualTemplateWithOptionalsOmittedStillValid()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var template = Assert.Single(result.Config.AttestationTemplates);
        Assert.Equal(string.Empty, template.Body);
        Assert.Empty(template.Fields);
        Assert.Empty(template.Quiz);
        Assert.Equal(string.Empty, template.PassMark);
    }

    [Fact]
    public void UnknownFieldIsRejected()
    {
        using var dir = TempConfig.Create(("t.yaml", """
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            organisation: org-a
            """));

        var result = ConfigLoader.Load(dir.Path);

        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unknown field 'organisation'") && d.Message.Contains("AttestationTemplate"));
    }

    [Fact]
    public void MissingRequiredFieldsRejected()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-manual
            title: T
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-manual") && d.Message.Contains("'control'"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-manual") && d.Message.Contains("'type'"));
    }

    [Fact]
    public void UnknownTypeFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-manual
            title: T
            control: ctrl-a
            type: survey
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-manual") && d.Message.Contains("unknown type 'survey'"));
    }

    [Fact]
    public void UnknownFieldTypeFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            fields:
              - id: f1
                label: L
                type: rating
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-manual") && d.Message.Contains("unknown type 'rating'"));
    }

    [Fact]
    public void SingleChoiceWithFewerThanTwoOptionsFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            fields:
              - id: f1
                label: L
                type: single-choice
                options: [only]
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-manual") && d.Message.Contains("fewer than two options"));
    }

    [Fact]
    public void SingleChoiceWithDuplicateOptionsFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            fields:
              - id: f1
                label: L
                type: single-choice
                options: [pass, pass]
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-manual") && d.Message.Contains("duplicate option 'pass'"));
    }

    [Fact]
    public void NonChoiceFieldWithOptionsFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            fields:
              - id: f1
                label: L
                type: boolean
                options: [yes, no]
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-manual") && d.Message.Contains("declares options"));
    }

    [Fact]
    public void NonIntegerPassMarkIsDiagnosticNotCrash()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-training
            title: T
            control: ctrl-a
            type: training
            pass_mark: high
            quiz:
              - id: q1
                prompt: P
                options: [a, b]
                answer: a
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-training") && d.Message.Contains("invalid pass_mark 'high'"));
    }

    [Fact]
    public void OutOfRangePassMarkFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-training
            title: T
            control: ctrl-a
            type: training
            pass_mark: 150
            quiz:
              - id: q1
                prompt: P
                options: [a, b]
                answer: a
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-training") && d.Message.Contains("invalid pass_mark '150'"));
    }

    [Fact]
    public void UnknownControlReferenceFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-manual
            title: T
            control: ctrl-missing
            type: manual
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-manual") && d.Message.Contains("unknown Control id 'ctrl-missing'"));
    }

    [Fact]
    public void DuplicateTemplateIdFails()
    {
        using var dir = TempConfig.Create(("all.yaml", $"""
            {ValidStandard}
            ---
            {ValidRequirement}
            ---
            {ValidControl}
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-a
            title: T
            control: ctrl-a
            type: manual
            ---
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-a
            title: T
            control: ctrl-a
            type: manual
            """));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Duplicate AttestationTemplate id 'attest-a'"));
    }

    [Fact]
    public void DuplicateFieldIdFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            fields:
              - id: dup
                label: A
                type: boolean
              - id: dup
                label: B
                type: short-text
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-manual") && d.Message.Contains("duplicate field id 'dup'"));
    }

    [Fact]
    public void DuplicateQuizIdFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-training
            title: T
            control: ctrl-a
            type: training
            pass_mark: 50
            quiz:
              - id: dup
                prompt: P1
                options: [a, b]
                answer: a
              - id: dup
                prompt: P2
                options: [c, d]
                answer: c
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-training") && d.Message.Contains("duplicate quiz id 'dup'"));
    }

    [Fact]
    public void QuizItemWithFewerThanTwoOptionsFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-training
            title: T
            control: ctrl-a
            type: training
            pass_mark: 50
            quiz:
              - id: q1
                prompt: P
                options: [only]
                answer: only
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-training") && d.Message.Contains("fewer than two options"));
    }

    [Fact]
    public void QuizItemWithDuplicateOptionsFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-training
            title: T
            control: ctrl-a
            type: training
            pass_mark: 50
            quiz:
              - id: q1
                prompt: P
                options: [a, a]
                answer: a
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-training") && d.Message.Contains("duplicate option 'a'"));
    }

    [Fact]
    public void QuizAnswerNotInOptionsFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-training
            title: T
            control: ctrl-a
            type: training
            pass_mark: 50
            quiz:
              - id: q1
                prompt: P
                options: [a, b]
                answer: c
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-training") && d.Message.Contains("answer 'c'") && d.Message.Contains("not one of its options"));
    }

    [Fact]
    public void WrongApiVersionFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v2
            kind: AttestationTemplate
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-manual") && d.Message.Contains("unknown apiVersion"));
    }

    [Fact]
    public void TrainingWithoutPassMarkFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-training
            title: T
            control: ctrl-a
            type: training
            quiz:
              - id: q1
                prompt: P
                options: [a, b]
                answer: a
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-training") && d.Message.Contains("'pass_mark'"));
    }

    [Fact]
    public void TrainingWithEmptyQuizFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-training
            title: T
            control: ctrl-a
            type: training
            pass_mark: 80
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-training") && d.Message.Contains("'quiz'"));
    }

    [Fact]
    public void TrainingWithPassMarkAndQuizPasses()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-training
            title: T
            control: ctrl-a
            type: training
            pass_mark: 80
            quiz:
              - id: q1
                prompt: P
                options: [a, b]
                answer: a
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void ManualWithPassMarkFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            pass_mark: 80
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-manual") && d.Message.Contains("'pass_mark'"));
    }

    [Fact]
    public void ManualWithQuizFails()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            quiz:
              - id: q1
                prompt: P
                options: [a, b]
                answer: a
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("attest-manual") && d.Message.Contains("'quiz'"));
    }

    [Fact]
    public void ManualWithNeitherPassMarkNorQuizPasses()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public void NonListFieldsIsDiagnosticNotCrash()
    {
        // `fields` authored as a scalar cannot bind to a list. The loader must return a diagnostic
        // (never an uncaught exception), and other kinds still load.
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            fields: not-a-list
            """)));

        var result = ConfigLoader.Load(dir.Path);

        Assert.NotEmpty(result.Diagnostics);
        Assert.Single(result.Config.Standards);
        Assert.Empty(result.Config.AttestationTemplates);
    }

    [Fact]
    public void NonListQuizIsDiagnosticNotCrash()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-training
            title: T
            control: ctrl-a
            type: training
            pass_mark: 80
            quiz: not-a-list
            """)));

        var result = ConfigLoader.Load(dir.Path);

        Assert.NotEmpty(result.Diagnostics);
        Assert.Single(result.Config.Standards);
        Assert.Empty(result.Config.AttestationTemplates);
    }

    [Fact]
    public void NonListFieldOptionsIsDiagnosticNotCrash()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            fields:
              - id: f1
                label: L
                type: single-choice
                options: not-a-list
            """)));

        var result = ConfigLoader.Load(dir.Path);

        Assert.NotEmpty(result.Diagnostics);
        Assert.Single(result.Config.Standards);
        Assert.Empty(result.Config.AttestationTemplates);
    }

    [Fact]
    public void ExplicitNullNestedCollectionsNormalizeToEmpty()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            fields:
            quiz:
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var template = Assert.Single(result.Config.AttestationTemplates);
        Assert.NotNull(template.Fields);
        Assert.Empty(template.Fields);
        Assert.NotNull(template.Quiz);
        Assert.Empty(template.Quiz);
    }

    [Fact]
    public void ExplicitNullFieldOptionsNormalizeToEmpty()
    {
        using var dir = TempConfig.Create(("all.yaml", ValidSet("""
            apiVersion: freeboard.dev/v1alpha1
            kind: AttestationTemplate
            id: attest-manual
            title: T
            control: ctrl-a
            type: manual
            fields:
              - id: f1
                label: L
                type: boolean
                options:
            """)));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        var field = Assert.Single(Assert.Single(result.Config.AttestationTemplates).Fields);
        Assert.NotNull(field.Options);
        Assert.Empty(field.Options);
    }

    [Fact]
    public void ConfigWithNoTemplatesStillLoadsAndValidates()
    {
        using var dir = TempConfig.Create(("all.yaml", $"{ValidStandard}\n---\n{ValidRequirement}"));

        var result = ConfigValidator.LoadAndValidate(dir.Path);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Config.AttestationTemplates);
    }
}
