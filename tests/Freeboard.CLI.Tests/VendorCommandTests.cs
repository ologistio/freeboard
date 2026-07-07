namespace Freeboard.CLI.Tests;

/// <summary>
/// In-process tests of the <c>vendor</c> command group. They drive <see cref="VendorCommands"/>
/// directly with a <see cref="FakeApiClient"/> via the <see cref="ApiClientFactory"/> seam, so no
/// live API and no database are involved. Joins the same "user-cli" collection as the user CLI tests
/// because both mutate process-global state (the ApiClientFactory seam, env vars, Console capture).
/// </summary>
[Collection("user-cli")]
public sealed class VendorCommandTests : IDisposable
{
    private readonly Func<string, string?, IFreeboardApiClient> originalFactory = ApiClientFactory.Create;
    private readonly string? originalApiUrl = Environment.GetEnvironmentVariable("FREEBOARD_API_URL");
    private readonly string? originalToken = Environment.GetEnvironmentVariable("FREEBOARD_ADMIN_TOKEN");
    private readonly TextWriter originalOut = Console.Out;
    private readonly TextWriter originalErr = Console.Error;

    public VendorCommandTests()
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
    public void ListPrintsVendorsAndJustificationsAndExitsZero()
    {
        var fake = Install(new FakeApiClient
        {
            VendorListResult = ApiResult<IReadOnlyList<ApiVendor>>.Success(
                [new ApiVendor("vendor-a", "Vendor A"), new ApiVendor("vendor-b", "Vendor B")]),
            VendorScopeListResult = ApiResult<IReadOnlyList<ApiVendorScope>>.Success(
            [
                new ApiVendorScope("vs-a", "T", "vendor-a", "req-a", null, "Out", "Supports MFA but not SSO."),
                new ApiVendorScope("vs-b", "T", "vendor-a", null, "ctrl-a", "In", null),
            ]),
        });

        var (exit, output, _) = Capture(() => new VendorCommands().List());

        Assert.Equal(0, exit);
        Assert.Equal(1, fake.VendorListCalls);
        Assert.Equal(1, fake.VendorScopeListCalls);
        Assert.Contains("vendor-a", output, StringComparison.Ordinal);
        Assert.Contains("Vendor B", output, StringComparison.Ordinal);
        // Every Out exception prints its justification; the In one does not require one.
        Assert.Contains("Supports MFA but not SSO.", output, StringComparison.Ordinal);
        Assert.Contains("req-a", output, StringComparison.Ordinal);
        Assert.Contains("ctrl-a", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ListWithNoVendorsExitsZero()
    {
        Install(new FakeApiClient
        {
            VendorListResult = ApiResult<IReadOnlyList<ApiVendor>>.Success([]),
            VendorScopeListResult = ApiResult<IReadOnlyList<ApiVendorScope>>.Success([]),
        });

        var (exit, _, _) = Capture(() => new VendorCommands().List());

        Assert.Equal(0, exit);
    }

    [Fact]
    public void MissingApiUrlExitsThree()
    {
        Environment.SetEnvironmentVariable("FREEBOARD_API_URL", null);
        Install(new FakeApiClient());

        var (exit, _, err) = Capture(() => new VendorCommands().List());

        Assert.Equal(3, exit);
        Assert.Contains("API URL", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnauthorizedVendorReadExitsThree()
    {
        var fake = Install(new FakeApiClient
        {
            VendorListResult = ApiResult<IReadOnlyList<ApiVendor>>.Unauthorized("Not authorized."),
        });

        var (exit, _, err) = Capture(() => new VendorCommands().List());

        Assert.Equal(3, exit);
        Assert.Contains("authorized", err, StringComparison.OrdinalIgnoreCase);
        // The vendor read failed, so the vendor-scope read is never attempted.
        Assert.Equal(0, fake.VendorScopeListCalls);
    }

    [Fact]
    public void ValidationResponseExitsOne()
    {
        Install(new FakeApiClient
        {
            VendorListResult = ApiResult<IReadOnlyList<ApiVendor>>.Validation("Bad request."),
        });

        var (exit, _, err) = Capture(() => new VendorCommands().List());

        Assert.Equal(1, exit);
        Assert.Contains("Bad request", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OperationalFailureOnScopesReadExitsThree()
    {
        // Vendors read succeeds but the vendor-scopes read fails operationally -> exit 3.
        Install(new FakeApiClient
        {
            VendorScopeListResult = ApiResult<IReadOnlyList<ApiVendorScope>>.Failure("Could not reach the API."),
        });

        var (exit, _, err) = Capture(() => new VendorCommands().List());

        Assert.Equal(3, exit);
        Assert.Contains("reach", err, StringComparison.OrdinalIgnoreCase);
    }
}
