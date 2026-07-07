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
    public void ListPrintsControlsEvaluationCollectorsAndConfigAndExitsZero()
    {
        var fake = Install(new FakeApiClient
        {
            ControlListResult = ApiResult<IReadOnlyList<ApiControl>>.Success(
            [
                new ApiControl("ctrl-a", "Control A", ["req-a"], "all"),
                new ApiControl("ctrl-b", "Control B", ["req-b"], null),
            ]),
            CollectorListResult = ApiResult<IReadOnlyList<ApiEvidenceCollector>>.Success(
            [
                new ApiEvidenceCollector("collector-a", "Endpoint MFA", "ctrl-a", "vendor-a", "integration", "daily", 100,
                    new Dictionary<string, string> { ["endpoint"] = "policies.mfa" }),
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
        Assert.Contains("vendor-a", output, StringComparison.Ordinal);
        // The config key/value pairs of a seeded collector appear in the output.
        Assert.Contains("endpoint", output, StringComparison.Ordinal);
        Assert.Contains("policies.mfa", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ListWithNoControlsExitsZero()
    {
        Install(new FakeApiClient
        {
            ControlListResult = ApiResult<IReadOnlyList<ApiControl>>.Success([]),
            CollectorListResult = ApiResult<IReadOnlyList<ApiEvidenceCollector>>.Success([]),
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
            CollectorListResult = ApiResult<IReadOnlyList<ApiEvidenceCollector>>.Failure("Could not reach the API."),
        });

        var (exit, _, err) = Capture(() => new CollectorCommands().List());

        Assert.Equal(3, exit);
        Assert.Contains("reach", err, StringComparison.OrdinalIgnoreCase);
    }
}
