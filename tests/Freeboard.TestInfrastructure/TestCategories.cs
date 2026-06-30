namespace Freeboard.TestInfrastructure;

/// <summary>
/// Category names for the <c>[Trait("Category", ...)]</c> attribute. CI selects a test
/// tier with <c>dotnet test --filter "Category=..."</c>, so the four tiers run as separate,
/// mutually-exclusive jobs. Const so the values can be used in attribute arguments.
/// </summary>
public static class TestCategories
{
    public const string Unit = "Unit";
    public const string Integration = "Integration";
    public const string E2E = "E2E";
    public const string Nfr = "NFR";
}
