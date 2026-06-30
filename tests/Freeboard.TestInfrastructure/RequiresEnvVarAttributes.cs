using Xunit;

namespace Freeboard.TestInfrastructure;

/// <summary>
/// A <see cref="SkippableFactAttribute"/> that skips at discovery time unless the named environment
/// variable is set, declaring an env-var precondition for a test:
/// <c>[RequiresEnvVarFact(EnvVar = "FREEBOARD_TEST_DB")]</c>. The variable is read during discovery,
/// which is correct here because <c>dotnet test</c> discovers and runs in the same process with the
/// same environment, so the precondition checked at discovery is the one that holds at run time.
/// Deriving from SkippableFact keeps the runtime <c>Skip.If</c>/<c>Skip.IfNot</c> available for a
/// secondary precondition on top of this one (for example a service or browser that is opted in but
/// not actually reachable).
/// </summary>
/// <remarks>
/// The env var is a named property rather than a constructor argument on purpose: the SkippableFact
/// discoverer reads the attribute's constructor arguments as the set of "skippable exception" types,
/// so a string constructor argument would break test discovery.
/// </remarks>
public sealed class RequiresEnvVarFactAttribute : SkippableFactAttribute
{
    private string _envVar = "";

    public string EnvVar
    {
        get => _envVar;
        set
        {
            _envVar = value;
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(value)))
            {
                Skip = $"Set {value} to run this test.";
            }
        }
    }
}

/// <summary>
/// A <see cref="SkippableTheoryAttribute"/> that skips at discovery time unless the named environment
/// variable is set. See <see cref="RequiresEnvVarFactAttribute"/> for why the env var is a named
/// property and why the discovery-time check is correct.
/// </summary>
public sealed class RequiresEnvVarTheoryAttribute : SkippableTheoryAttribute
{
    private string _envVar = "";

    public string EnvVar
    {
        get => _envVar;
        set
        {
            _envVar = value;
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(value)))
            {
                Skip = $"Set {value} to run this test.";
            }
        }
    }
}
