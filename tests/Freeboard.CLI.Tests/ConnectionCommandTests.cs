namespace Freeboard.CLI.Tests;

/// <summary>
/// In-process tests of the <c>connections</c> command group. They drive <see cref="ConnectionCommands"/>
/// directly with a <see cref="FakeApiClient"/> via the <see cref="ApiClientFactory"/> seam, so no live API
/// and no database are involved. Joins the same "user-cli" collection as the other CLI tests because they
/// mutate process-global state (the ApiClientFactory seam, env vars, Console capture).
/// </summary>
[Collection("user-cli")]
public sealed class ConnectionCommandTests : IDisposable
{
    private readonly Func<string, string?, IFreeboardApiClient> originalFactory = ApiClientFactory.Create;
    private readonly string? originalApiUrl = Environment.GetEnvironmentVariable("FREEBOARD_API_URL");
    private readonly string? originalToken = Environment.GetEnvironmentVariable("FREEBOARD_ADMIN_TOKEN");
    private readonly TextWriter originalOut = Console.Out;
    private readonly TextWriter originalErr = Console.Error;

    public ConnectionCommandTests()
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
    public void ListPrintsConnectionsWithHealthAndExitsZero()
    {
        var fake = Install(new FakeApiClient
        {
            ConnectionListResult = ApiResult<IReadOnlyList<ApiIntegrationConnection>>.Success(
            [
                new ApiIntegrationConnection("fleet-prod", "fleet", "https://fleet.example.com", "daily", "vendor-a", true),
                new ApiIntegrationConnection("fleet-dev", "fleet", "https://dev.example.com", "weekly", null, false),
            ]),
        });

        var (exit, output, _) = Capture(() => new ConnectionCommands().List());

        Assert.Equal(0, exit);
        Assert.Equal(1, fake.ConnectionListCalls);
        Assert.Contains("fleet-prod", output, StringComparison.Ordinal);
        Assert.Contains("https://fleet.example.com", output, StringComparison.Ordinal);
        Assert.Contains("daily", output, StringComparison.Ordinal);
        Assert.Contains("resolvable", output, StringComparison.Ordinal);
        Assert.Contains("unresolvable", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ListWithNoConnectionsExitsZero()
    {
        Install(new FakeApiClient
        {
            ConnectionListResult = ApiResult<IReadOnlyList<ApiIntegrationConnection>>.Success([]),
        });

        var (exit, _, _) = Capture(() => new ConnectionCommands().List());

        Assert.Equal(0, exit);
    }

    [Fact]
    public void MissingApiUrlExitsThree()
    {
        Environment.SetEnvironmentVariable("FREEBOARD_API_URL", null);
        Install(new FakeApiClient());

        var (exit, _, err) = Capture(() => new ConnectionCommands().List());

        Assert.Equal(3, exit);
        Assert.Contains("API URL", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnauthorizedReadExitsThree()
    {
        Install(new FakeApiClient
        {
            ConnectionListResult = ApiResult<IReadOnlyList<ApiIntegrationConnection>>.Unauthorized("Not authorized."),
        });

        var (exit, _, err) = Capture(() => new ConnectionCommands().List());

        Assert.Equal(3, exit);
        Assert.Contains("authorized", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OperationalFailureExitsThree()
    {
        Install(new FakeApiClient
        {
            ConnectionListResult = ApiResult<IReadOnlyList<ApiIntegrationConnection>>.Failure("Could not reach the API."),
        });

        var (exit, _, err) = Capture(() => new ConnectionCommands().List());

        Assert.Equal(3, exit);
        Assert.Contains("reach", err, StringComparison.OrdinalIgnoreCase);
    }
}
