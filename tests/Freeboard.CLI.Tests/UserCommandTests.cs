using System.Net;
using System.Text;

namespace Freeboard.CLI.Tests;

/// <summary>A stub HttpMessageHandler returning a fixed status and body.</summary>
internal sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
}

/// <summary>
/// In-process tests of the <c>user</c> command group. They drive <see cref="UserCommands"/>
/// directly with a <see cref="FakeApiClient"/> via the <see cref="ApiClientFactory"/> seam, so no
/// live API and no database are involved. The fake holds no IDbConnectionFactory, so a passing run
/// is proof the user commands open no DB connection. Serialized: ApiClientFactory, the env
/// vars, and Console are process-global.
/// </summary>
[Collection("user-cli")]
public sealed class UserCommandTests : IDisposable
{
    private readonly Func<string, string?, IFreeboardApiClient> originalFactory = ApiClientFactory.Create;
    private readonly string? originalApiUrl = Environment.GetEnvironmentVariable("FREEBOARD_API_URL");
    private readonly string? originalToken = Environment.GetEnvironmentVariable("FREEBOARD_ADMIN_TOKEN");
    private readonly string? originalSecret = Environment.GetEnvironmentVariable("FREEBOARD_BOOTSTRAP_SECRET");
    private readonly TextWriter originalOut = Console.Out;
    private readonly TextWriter originalErr = Console.Error;

    public UserCommandTests()
    {
        // Default to a present API URL and token so a test asserting a specific failure controls it.
        Environment.SetEnvironmentVariable("FREEBOARD_API_URL", "http://localhost:5000");
        Environment.SetEnvironmentVariable("FREEBOARD_ADMIN_TOKEN", "admin-token");
        Environment.SetEnvironmentVariable("FREEBOARD_BOOTSTRAP_SECRET", null);
    }

    public void Dispose()
    {
        ApiClientFactory.Create = originalFactory;
        Environment.SetEnvironmentVariable("FREEBOARD_API_URL", originalApiUrl);
        Environment.SetEnvironmentVariable("FREEBOARD_ADMIN_TOKEN", originalToken);
        Environment.SetEnvironmentVariable("FREEBOARD_BOOTSTRAP_SECRET", originalSecret);
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

    private FakeApiClient Install(FakeApiClient fake)
    {
        ApiClientFactory.Create = (_, _) => fake;
        return fake;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    // create maps to the API and prints the one-time temp password exactly once, exit 0.
    [Fact]
    public void CreatePrintsTempPasswordOnceAndExitsZero()
    {
        var fake = Install(new FakeApiClient
        {
            CreateResult = ApiResult<CreatedUser>.Success(
                new CreatedUser(FakeApiClient.SampleUser, "ONE-TIME-PW")),
        });

        var (exit, output, _) = Capture(() => new UserCommands().Create("a@b.test", "Alice"));

        Assert.Equal(0, exit);
        Assert.Equal(1, fake.CreateCalls);
        Assert.Equal(1, CountOccurrences(output, "ONE-TIME-PW"));
    }

    // reset-password prints its returned temp password once, exit 0.
    [Fact]
    public void ResetPasswordPrintsTempPasswordOnceAndExitsZero()
    {
        var fake = Install(new FakeApiClient
        {
            ResetResult = ApiResult<ResetPassword>.Success(new ResetPassword("RESET-ONCE")),
        });

        // An id (no '@') is used directly; no list lookup is needed.
        var (exit, output, _) = Capture(() =>
            new UserCommands().ResetPassword("01HZZ0000000000000000000AA"));

        Assert.Equal(0, exit);
        Assert.Equal(1, fake.ResetCalls);
        Assert.Equal(0, fake.ListCalls);
        Assert.Equal(1, CountOccurrences(output, "RESET-ONCE"));
    }

    // A duplicate email is a 422 -> exit 1, surfacing the API message.
    [Fact]
    public void CreateDuplicateEmailExitsOne()
    {
        Install(new FakeApiClient
        {
            CreateResult = ApiResult<CreatedUser>.Validation("A user with this email already exists."),
        });

        var (exit, _, err) = Capture(() => new UserCommands().Create("dupe@b.test", "Dupe"));

        Assert.Equal(1, exit);
        Assert.Contains("already exists", err, StringComparison.OrdinalIgnoreCase);
    }

    // Missing API URL -> exit 3.
    [Fact]
    public void MissingApiUrlExitsThree()
    {
        Environment.SetEnvironmentVariable("FREEBOARD_API_URL", null);
        Install(new FakeApiClient());

        var (exit, _, err) = Capture(() => new UserCommands().List());

        Assert.Equal(3, exit);
        Assert.Contains("API URL", err, StringComparison.OrdinalIgnoreCase);
    }

    // A malformed --api-url throws while constructing the HttpClient; it must map to an
    // operational failure (exit 3), not escape the exit-code contract as an unhandled exception.
    [Fact]
    public void MalformedApiUrlExitsThree()
    {
        // Use the REAL client factory so the bad URL actually reaches the HttpClient constructor.
        ApiClientFactory.Create = originalFactory;

        var (exit, _, err) = Capture(() => new UserCommands().List(apiUrl: "http://[not a url"));

        Assert.Equal(3, exit);
        Assert.Contains("Invalid API URL", err, StringComparison.OrdinalIgnoreCase);
    }

    // A 200 success response with an unparseable body is an operational failure (exit 3),
    // not an unhandled JsonException.
    [Fact]
    public void SuccessWithUnparseableBodyExitsThree()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, "this-is-not-json");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        ApiClientFactory.Create = (_, _) => new HttpFreeboardApiClient(http);

        var (exit, _, err) = Capture(() => new UserCommands().List());

        Assert.Equal(3, exit);
        Assert.Contains("unreadable body", err, StringComparison.OrdinalIgnoreCase);
    }

    // A 401/403 -> exit 3.
    [Fact]
    public void UnauthorizedExitsThree()
    {
        Install(new FakeApiClient
        {
            ListResult = ApiResult<IReadOnlyList<ApiUser>>.Unauthorized("Not authorized."),
        });

        var (exit, _, err) = Capture(() => new UserCommands().List());

        Assert.Equal(3, exit);
        Assert.Contains("authorized", err, StringComparison.OrdinalIgnoreCase);
    }

    // A generic operational failure (5xx / connection) -> exit 3.
    [Fact]
    public void OperationalFailureExitsThree()
    {
        Install(new FakeApiClient
        {
            CreateResult = ApiResult<CreatedUser>.Failure("Could not reach the API."),
        });

        var (exit, _, err) = Capture(() => new UserCommands().Create("a@b.test", "Alice"));

        Assert.Equal(3, exit);
        Assert.Contains("reach", err, StringComparison.OrdinalIgnoreCase);
    }

    // disable by email resolves the id client-side via the list, then calls disable.
    [Fact]
    public void DisableByEmailResolvesIdViaListThenDisables()
    {
        var fake = Install(new FakeApiClient());

        var (exit, _, _) = Capture(() => new UserCommands().Disable("user@example.test"));

        Assert.Equal(0, exit);
        Assert.Equal(1, fake.ListCalls);
        Assert.Equal(1, fake.DisableCalls);
        Assert.Equal(FakeApiClient.SampleUser.Id, fake.LastId);
    }

    // disable by id uses it directly without a list lookup.
    [Fact]
    public void DisableByIdSkipsListLookup()
    {
        var fake = Install(new FakeApiClient());

        var (exit, _, _) = Capture(() => new UserCommands().Disable("01HZZ0000000000000000000AA"));

        Assert.Equal(0, exit);
        Assert.Equal(0, fake.ListCalls);
        Assert.Equal(1, fake.DisableCalls);
    }

    // An unknown email (no match) is a validation error -> exit 1.
    [Fact]
    public void DisableUnknownEmailExitsOne()
    {
        Install(new FakeApiClient
        {
            ListResult = ApiResult<IReadOnlyList<ApiUser>>.Success([]),
        });

        var (exit, _, err) = Capture(() => new UserCommands().Disable("ghost@example.test"));

        Assert.Equal(1, exit);
        Assert.Contains("No user found", err, StringComparison.OrdinalIgnoreCase);
    }

    // bootstrap success prints the returned admin token, exit 0.
    [Fact]
    public void BootstrapPrintsTokenAndExitsZero()
    {
        var fake = Install(new FakeApiClient
        {
            BootstrapResult = ApiResult<BootstrapResult>.Success(
                new BootstrapResult(FakeApiClient.SampleUser, "ADMIN-TOKEN-123")),
        });

        var (exit, output, _) = Capture(() =>
            new UserCommands().Bootstrap("admin@b.test", "Admin", bootstrapSecret: "s3cret"));

        Assert.Equal(0, exit);
        Assert.Equal(1, fake.BootstrapCalls);
        Assert.Contains("ADMIN-TOKEN-123", output, StringComparison.Ordinal);
    }

    // bootstrap conflict (409: an admin already exists) -> exit 3.
    [Fact]
    public void BootstrapConflictExitsThree()
    {
        Install(new FakeApiClient
        {
            BootstrapResult = ApiResult<BootstrapResult>.Conflict("Already initialized."),
        });

        var (exit, _, err) = Capture(() =>
            new UserCommands().Bootstrap("admin@b.test", "Admin", bootstrapSecret: "s3cret"));

        Assert.Equal(3, exit);
        Assert.Contains("initialized", err, StringComparison.OrdinalIgnoreCase);
    }

    // bootstrap with no secret (neither option nor env) is rejected before any API call -> exit 3.
    [Fact]
    public void BootstrapWithoutSecretExitsThree()
    {
        var fake = Install(new FakeApiClient());

        var (exit, _, err) = Capture(() => new UserCommands().Bootstrap("admin@b.test", "Admin"));

        Assert.Equal(3, exit);
        Assert.Equal(0, fake.BootstrapCalls);
        Assert.Contains("bootstrap secret", err, StringComparison.OrdinalIgnoreCase);
    }
}

[CollectionDefinition("user-cli", DisableParallelization = true)]
public sealed class UserCliCollection;
