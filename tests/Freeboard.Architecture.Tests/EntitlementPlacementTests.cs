using Freeboard.Core.Enterprise;

namespace Freeboard.Architecture.Tests;

/// <summary>
/// Pins the entitlement seam's placement: the interface is MIT and lives in Freeboard.Core so any MIT
/// caller can ask the gate without referencing Freeboard.Enterprise.
/// </summary>
public sealed class EntitlementPlacementTests
{
    [Fact]
    public void EntitlementInterfaceIsInCoreAssembly()
        => Assert.Equal("Freeboard.Core", typeof(IEnterpriseEntitlements).Assembly.GetName().Name);
}
