using Freeboard.Core.Enterprise;
using Freeboard.Entitlements;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Freeboard.Web.Tests;

/// <summary>
/// The config-backed entitlement seam: on/off resolution from IConfiguration, fail-safe for unmapped
/// values, and resolvability from the real web app service provider.
/// </summary>
public sealed class EnterpriseEntitlementsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void AbsentConfigIsNotEntitled()
    {
        var entitlements = new ConfigurationEnterpriseEntitlements(Config());
        Assert.False(entitlements.IsEntitled(EnterpriseEntitlement.CustomPolicies));
    }

    [Fact]
    public void ExplicitFalseIsNotEntitled()
    {
        var entitlements = new ConfigurationEnterpriseEntitlements(Config(("Enterprise:CustomPolicies", "false")));
        Assert.False(entitlements.IsEntitled(EnterpriseEntitlement.CustomPolicies));
    }

    [Fact]
    public void ExplicitTrueIsEntitled()
    {
        var entitlements = new ConfigurationEnterpriseEntitlements(Config(("Enterprise:CustomPolicies", "true")));
        Assert.True(entitlements.IsEntitled(EnterpriseEntitlement.CustomPolicies));
    }

    [Fact]
    public void DefaultZeroValueIsNotEntitled()
    {
        // default(EnterpriseEntitlement) is the unmapped value 0; it must fall through the switch default.
        var entitlements = new ConfigurationEnterpriseEntitlements(Config(("Enterprise:CustomPolicies", "true")));
        Assert.False(entitlements.IsEntitled((EnterpriseEntitlement)0));
    }

    [Fact]
    public void OutOfRangeValueIsNotEntitled()
    {
        var entitlements = new ConfigurationEnterpriseEntitlements(Config(("Enterprise:CustomPolicies", "true")));
        Assert.False(entitlements.IsEntitled((EnterpriseEntitlement)999));
    }

    [Fact]
    public void ResolvesConfigBackedImplementationFromWebAppProvider()
    {
        using var factory = new AuthWebFactory();
        var resolved = factory.Services.GetRequiredService<IEnterpriseEntitlements>();

        Assert.IsType<ConfigurationEnterpriseEntitlements>(resolved);
        Assert.Equal("Freeboard", resolved.GetType().Assembly.GetName().Name);
        // Default build is off: no Enterprise section in appsettings.json.
        Assert.False(resolved.IsEntitled(EnterpriseEntitlement.CustomPolicies));
    }

    [Fact]
    public void ConfigFlipEnablesEntitlementThroughResolvedService()
    {
        using var factory = new EntitledFactory();
        var resolved = factory.Services.GetRequiredService<IEnterpriseEntitlements>();
        Assert.True(resolved.IsEntitled(EnterpriseEntitlement.CustomPolicies));
    }

    private sealed class EntitledFactory : AuthWebFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Enterprise:CustomPolicies", "true");
        }
    }
}
