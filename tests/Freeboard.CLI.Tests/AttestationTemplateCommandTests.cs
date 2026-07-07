namespace Freeboard.CLI.Tests;

/// <summary>
/// In-process tests of the <c>attestation-template</c> command group. They drive
/// <see cref="AttestationTemplateCommands"/> directly with a <see cref="FakeApiClient"/> via the
/// <see cref="ApiClientFactory"/> seam, so no live API and no database are involved. Joins the same
/// "user-cli" collection as the other CLI tests because they mutate process-global state (the
/// ApiClientFactory seam, env vars, Console capture).
/// </summary>
[Collection("user-cli")]
public sealed class AttestationTemplateCommandTests : IDisposable
{
    private readonly Func<string, string?, IFreeboardApiClient> originalFactory = ApiClientFactory.Create;
    private readonly string? originalApiUrl = Environment.GetEnvironmentVariable("FREEBOARD_API_URL");
    private readonly string? originalToken = Environment.GetEnvironmentVariable("FREEBOARD_ADMIN_TOKEN");
    private readonly TextWriter originalOut = Console.Out;
    private readonly TextWriter originalErr = Console.Error;

    public AttestationTemplateCommandTests()
    {
        Environment.SetEnvironmentVariable("FREEBOARD_API_URL", "http://localhost:5000");
        Environment.SetEnvironmentVariable("FREEBOARD_ADMIN_TOKEN", "admin-token");
    }

    public void Dispose()
    {
        ApiClientFactory.Create = originalFactory;
        Environment.SetEnvironmentVariable("FREEBOARD_API_URL", originalApiUrl);
        Environment.SetEnvironmentVariable("FREEBOARD_ADMIN_TOKEN", originalToken);
        Console.SetOut(originalOut);
        Console.SetError(originalErr);
    }

    private static (int Exit, string Out, string Err) Capture(Func<int> run)
    {
        using var outW = new StringWriter();
        using var errW = new StringWriter();
        Console.SetOut(outW);
        Console.SetError(errW);
        var exit = run();
        return (exit, outW.ToString(), errW.ToString());
    }

    private static FakeApiClient Install(FakeApiClient fake)
    {
        ApiClientFactory.Create = (_, _) => fake;
        return fake;
    }

    [Fact]
    public void ListPrintsTemplatesFieldsPassMarkAndQuizAndExitsZero()
    {
        var fake = Install(new FakeApiClient());

        var (exit, output, _) = Capture(() => new AttestationTemplateCommands().List());

        Assert.Equal(0, exit);
        Assert.Equal(1, fake.TemplateListCalls);
        // It does not read /controls: a template carries its control id.
        Assert.Equal(0, fake.ControlListCalls);
        Assert.Contains("ctrl-a", output, StringComparison.Ordinal);
        Assert.Contains("attest-manual", output, StringComparison.Ordinal);
        Assert.Contains("Ruleset reviewed?", output, StringComparison.Ordinal);
        Assert.Contains("attest-training", output, StringComparison.Ordinal);
        Assert.Contains("pass mark: 80%", output, StringComparison.Ordinal);
        Assert.Contains("What should you do with an unexpected attachment?", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ListOutputCarriesNoQuizAnswer()
    {
        // Options are the two answer-free choices; the correct answer is a distinctive sentinel that is
        // used nowhere as a prompt, option, field, or label. It can only appear in the output if an answer
        // leaked - and the wire record has no field to carry one (asserted structurally below).
        const string answerSentinel = "SECRET_ANSWER_SENTINEL";
        var fake = Install(new FakeApiClient
        {
            TemplateListResult = ApiResult<IReadOnlyList<ApiAttestationTemplate>>.Success(
            [
                new ApiAttestationTemplate("attest-training", "Phishing awareness", "ctrl-a", "training", null, [], 80,
                    [new ApiQuizItem("q1", "Pick the safe action", ["alpha", "bravo"])]),
            ]),
        });

        var (exit, output, _) = Capture(() => new AttestationTemplateCommands().List());

        Assert.Equal(0, exit);
        Assert.Equal(1, fake.TemplateListCalls);
        // Prompt and options print; the answer sentinel does not.
        Assert.Contains("Pick the safe action", output, StringComparison.Ordinal);
        Assert.DoesNotContain(answerSentinel, output, StringComparison.Ordinal);
    }

    [Fact]
    public void QuizWireRecordHasNoAnswerProperty()
    {
        // Structural guarantee: the CLI wire record cannot carry a quiz answer, so the redacted answer
        // can never reach the CLI read surface regardless of formatting.
        Assert.DoesNotContain("Answer", typeof(ApiQuizItem).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public void ListWithNoTemplatesExitsZero()
    {
        Install(new FakeApiClient
        {
            TemplateListResult = ApiResult<IReadOnlyList<ApiAttestationTemplate>>.Success([]),
        });

        var (exit, _, _) = Capture(() => new AttestationTemplateCommands().List());

        Assert.Equal(0, exit);
    }

    [Fact]
    public void MissingApiUrlExitsThree()
    {
        Environment.SetEnvironmentVariable("FREEBOARD_API_URL", null);
        Install(new FakeApiClient());

        var (exit, _, err) = Capture(() => new AttestationTemplateCommands().List());

        Assert.Equal(3, exit);
        Assert.Contains("API URL", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidationResponseExitsOne()
    {
        Install(new FakeApiClient
        {
            TemplateListResult = ApiResult<IReadOnlyList<ApiAttestationTemplate>>.Validation("Bad request."),
        });

        var (exit, _, err) = Capture(() => new AttestationTemplateCommands().List());

        Assert.Equal(1, exit);
        Assert.Contains("Bad request", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OperationalFailureExitsThree()
    {
        Install(new FakeApiClient
        {
            TemplateListResult = ApiResult<IReadOnlyList<ApiAttestationTemplate>>.Failure("Could not reach the API."),
        });

        var (exit, _, err) = Capture(() => new AttestationTemplateCommands().List());

        Assert.Equal(3, exit);
        Assert.Contains("reach", err, StringComparison.OrdinalIgnoreCase);
    }
}
