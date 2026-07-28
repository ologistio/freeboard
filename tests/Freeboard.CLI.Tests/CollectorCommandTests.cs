namespace Freeboard.CLI.Tests;

/// <summary>
/// In-process tests of the <c>collector</c> command group. They drive <see cref="CollectorCommands"/>
/// directly with a <see cref="FakeApiClient"/> via the <see cref="ApiClientFactory"/> seam, so no live
/// API and no database are involved. Joins the same "user-cli" collection as the other CLI tests
/// because they mutate process-global state (the ApiClientFactory seam, env vars, Console capture).
/// </summary>
[Collection("user-cli")]
public sealed class CollectorCommandTests : IDisposable
{
    private readonly Func<string, string?, IFreeboardApiClient> originalFactory = ApiClientFactory.Create;
    private readonly string? originalApiUrl = Environment.GetEnvironmentVariable("FREEBOARD_API_URL");
    private readonly string? originalToken = Environment.GetEnvironmentVariable("FREEBOARD_ADMIN_TOKEN");
    private readonly TextWriter originalOut = Console.Out;
    private readonly TextWriter originalErr = Console.Error;

    public CollectorCommandTests()
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
    public void ListPrintsControlsEvaluationCollectorsAndTheirConfigAndExitsZero()
    {
        var fake = Install(new FakeApiClient
        {
            ControlListResult = ApiResult<IReadOnlyList<ApiControl>>.Success(
            [
                new ApiControl("ctrl-a", "Control A", ["req-a"], "all"),
                new ApiControl("ctrl-b", "Control B", ["req-b"], null),
            ]),
        });

        var (exit, output, _) = Capture(() => new CollectorCommands().List());

        Assert.Equal(0, exit);
        Assert.Equal(1, fake.ControlListCalls);
        Assert.Equal(1, fake.CollectorListCalls);
        Assert.Contains("ctrl-a", output, StringComparison.Ordinal);
        Assert.Contains("all", output, StringComparison.Ordinal);
        Assert.Contains("collector-a", output, StringComparison.Ordinal);
        Assert.Contains("Endpoint MFA", output, StringComparison.Ordinal);
        Assert.Contains("integration", output, StringComparison.Ordinal);
        Assert.Contains("fleet", output, StringComparison.Ordinal);
        Assert.Contains("vendor-a", output, StringComparison.Ordinal);
        // An integration collector's tracked checks, by name and severity.
        Assert.Contains("check mfa-enforced", output, StringComparison.Ordinal);
        Assert.Contains("Hard", output, StringComparison.Ordinal);
    }

    // The merge's point at the command surface: a former template lists under the same command, in the
    // same control block, as a data source.
    [Fact]
    public void ListPrintsAnAttestationsFormAndATrainingCollectorsQuiz()
    {
        Install(new FakeApiClient());

        var (exit, output, _) = Capture(() => new CollectorCommands().List());

        Assert.Equal(0, exit);
        Assert.Contains("attest-manual", output, StringComparison.Ordinal);
        Assert.Contains("Ruleset reviewed?", output, StringComparison.Ordinal);
        Assert.Contains("attest-training", output, StringComparison.Ordinal);
        Assert.Contains("pass mark: 80%", output, StringComparison.Ordinal);
        Assert.Contains("What should you do with an unexpected attachment?", output, StringComparison.Ordinal);
    }

    // The body itself is rendered by the register page, so the CLI only reports whether one is authored.
    [Fact]
    public void ListPrintsTheBodyIndicatorForBothCasesAndForNoOtherType()
    {
        Install(new FakeApiClient());

        var (_, output, _) = Capture(() => new CollectorCommands().List());

        var manual = output[output.IndexOf("attest-manual", StringComparison.Ordinal)..];
        Assert.Contains("has body", manual[..manual.IndexOf("attest-training", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Contains("no body", output[output.IndexOf("attest-training", StringComparison.Ordinal)..], StringComparison.Ordinal);

        // A script collector's schema registers no config key, so its block carries no indicator at all
        // rather than falling through to "no body".
        var script = output[output.IndexOf("collector-script", StringComparison.Ordinal)..];
        Assert.DoesNotContain("body", script[..script.IndexOf("attest-manual", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void ListOutputCarriesNoQuizAnswer()
    {
        // The sentinel is not in the fixture and structurally cannot be: ApiQuizItem has no answer member
        // (asserted directly below). It is belt-and-braces against a future wire record that gains one -
        // a distinctive value to search for that no prompt, option, field, or label would ever contain.
        const string answerSentinel = "SECRET_ANSWER_SENTINEL";
        Install(new FakeApiClient
        {
            CollectorListResult = ApiResult<IReadOnlyList<ApiCollector>>.Success(
            [
                new ApiCollector(
                    "attest-training", "Phishing awareness", "ctrl-a", null, "training", null, "annual", null,
                    new ApiCollectorConfig(
                        null, [], 80, [new ApiQuizItem("q1", "Pick the safe action", ["alpha", "bravo"])], [])),
            ]),
        });

        var (exit, output, _) = Capture(() => new CollectorCommands().List());

        Assert.Equal(0, exit);
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

    // The retired group is gone from the command surface, not merely unused.
    [Fact]
    public void TheAttestationTemplateCommandGroupIsRemoved()
    {
        Assert.DoesNotContain(
            typeof(CollectorCommands).Assembly.GetTypes(),
            t => t.Name.Contains("AttestationTemplate", StringComparison.Ordinal));
    }

    [Fact]
    public void ListWithNoControlsExitsZero()
    {
        Install(new FakeApiClient
        {
            ControlListResult = ApiResult<IReadOnlyList<ApiControl>>.Success([]),
            CollectorListResult = ApiResult<IReadOnlyList<ApiCollector>>.Success([]),
        });

        var (exit, _, _) = Capture(() => new CollectorCommands().List());

        Assert.Equal(0, exit);
    }

    [Fact]
    public void MissingApiUrlExitsThree()
    {
        Environment.SetEnvironmentVariable("FREEBOARD_API_URL", null);
        Install(new FakeApiClient());

        var (exit, _, err) = Capture(() => new CollectorCommands().List());

        Assert.Equal(3, exit);
        Assert.Contains("API URL", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnauthorizedControlReadExitsThreeAndSkipsCollectors()
    {
        var fake = Install(new FakeApiClient
        {
            ControlListResult = ApiResult<IReadOnlyList<ApiControl>>.Unauthorized("Not authorized."),
        });

        var (exit, _, err) = Capture(() => new CollectorCommands().List());

        Assert.Equal(3, exit);
        Assert.Contains("authorized", err, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fake.CollectorListCalls);
    }

    [Fact]
    public void ValidationResponseExitsOne()
    {
        Install(new FakeApiClient
        {
            ControlListResult = ApiResult<IReadOnlyList<ApiControl>>.Validation("Bad request."),
        });

        var (exit, _, err) = Capture(() => new CollectorCommands().List());

        Assert.Equal(1, exit);
        Assert.Contains("Bad request", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OperationalFailureOnCollectorsReadExitsThree()
    {
        Install(new FakeApiClient
        {
            CollectorListResult = ApiResult<IReadOnlyList<ApiCollector>>.Failure("Could not reach the API."),
        });

        var (exit, _, err) = Capture(() => new CollectorCommands().List());

        Assert.Equal(3, exit);
        Assert.Contains("reach", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CredentialIssuePrintsRawTokenAndExitsZero()
    {
        var fake = Install(new FakeApiClient
        {
            IssueResult = ApiResult<IssuedCredential>.Success(
                new IssuedCredential("cred-9", "collector-a", "v1.the-raw-token", "2027-01-01T00:00:00Z")),
        });

        var (exit, output, _) = Capture(() =>
            new CollectorCommands().CredentialIssue("collector-a", expiresAt: "2027-01-01T00:00:00Z"));

        Assert.Equal(0, exit);
        Assert.Equal(1, fake.CredentialIssueCalls);
        Assert.Equal("collector-a", fake.LastId);
        Assert.Equal("2027-01-01T00:00:00Z", fake.LastExpiresAt);
        // The raw token is printed on stdout exactly as returned.
        Assert.Contains("v1.the-raw-token", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialIssueUnknownCollectorExitsOne()
    {
        Install(new FakeApiClient
        {
            IssueResult = ApiResult<IssuedCredential>.Validation("Collector 'nope' does not exist."),
        });

        var (exit, _, err) = Capture(() => new CollectorCommands().CredentialIssue("nope"));

        Assert.Equal(1, exit);
        Assert.Contains("does not exist", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CredentialIssueOperationalFailureExitsThree()
    {
        Install(new FakeApiClient
        {
            IssueResult = ApiResult<IssuedCredential>.Failure("Could not reach the API."),
        });

        var (exit, _, err) = Capture(() => new CollectorCommands().CredentialIssue("collector-a"));

        Assert.Equal(3, exit);
        Assert.Contains("reach", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CredentialRevokeExitsZero()
    {
        var fake = Install(new FakeApiClient());

        var (exit, _, _) = Capture(() => new CollectorCommands().CredentialRevoke("collector-a", "cred-9"));

        Assert.Equal(0, exit);
        Assert.Equal(1, fake.CredentialRevokeCalls);
        Assert.Equal("collector-a", fake.LastId);
        Assert.Equal("cred-9", fake.LastCredentialId);
    }
}
