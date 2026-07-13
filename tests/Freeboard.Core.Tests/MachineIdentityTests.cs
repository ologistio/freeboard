using Freeboard.Core.Assets;

namespace Freeboard.Core.Tests;

/// <summary>
/// Covers the machine asset domain model: the kind/state enums and the single-axis identity derivation
/// (serial primary, host uuid fallback, placeholder rejection, and canonicalization).
/// </summary>
public sealed class MachineIdentityTests
{
    [Fact]
    public void AssetKindHasMachineAndStateHasSeenAndRetired()
    {
        Assert.True(Enum.IsDefined(AssetKind.Machine));
        Assert.True(Enum.IsDefined(AssetState.Seen));
        Assert.True(Enum.IsDefined(AssetState.Retired));
    }

    [Fact]
    public void SerialIsPrimaryIdentityWhenPresent()
    {
        var identity = MachineIdentity.Derive("ABC123", "550e8400-e29b-41d4-a716-446655440000");

        Assert.NotNull(identity);
        Assert.Equal(MachineIdentityKind.Serial, identity!.Kind);
        Assert.Equal("ABC123", identity.Value);
    }

    [Fact]
    public void HostUuidIsFallbackWhenSerialBlank()
    {
        var identity = MachineIdentity.Derive("   ", "550E8400-E29B-41D4-A716-446655440000");

        Assert.NotNull(identity);
        Assert.Equal(MachineIdentityKind.HostUuid, identity!.Kind);
        Assert.Equal("550e8400-e29b-41d4-a716-446655440000", identity.Value);
    }

    [Fact]
    public void NoIdentityWhenNeitherIsUsable()
    {
        Assert.Null(MachineIdentity.Derive(null, null));
        Assert.Null(MachineIdentity.Derive("  ", "not-a-uuid"));
    }

    [Theory]
    [InlineData("  abc  123  ", "ABC 123")]
    [InlineData("abc123", "ABC123")]
    [InlineData("ABC123", "ABC123")]
    public void SerialNormalizationCollapsesToOneValue(string raw, string expected)
    {
        var identity = MachineIdentity.Derive(raw, null);

        Assert.NotNull(identity);
        Assert.Equal(MachineIdentityKind.Serial, identity!.Kind);
        Assert.Equal(expected, identity.Value);
    }

    [Theory]
    [InlineData("To be filled by O.E.M.")]
    [InlineData("unknown")]
    [InlineData("Default string")]
    [InlineData("0")]
    public void PlaceholderSerialIsTreatedAsMissingAndFallsThroughToUuid(string placeholder)
    {
        var identity = MachineIdentity.Derive(placeholder, "550e8400-e29b-41d4-a716-446655440000");

        Assert.NotNull(identity);
        Assert.Equal(MachineIdentityKind.HostUuid, identity!.Kind);
        Assert.Equal("550e8400-e29b-41d4-a716-446655440000", identity.Value);
    }

    [Fact]
    public void PlaceholderSerialWithNoUuidProducesNoIdentity()
    {
        Assert.Null(MachineIdentity.Derive("N/A", null));
    }

    [Theory]
    [InlineData("{550E8400-E29B-41D4-A716-446655440000}")]
    [InlineData("550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("550E8400E29B41D4A716446655440000")]
    public void UuidVariantsCanonicalizeToOneValue(string variant)
    {
        var identity = MachineIdentity.Derive(null, variant);

        Assert.NotNull(identity);
        Assert.Equal(MachineIdentityKind.HostUuid, identity!.Kind);
        Assert.Equal("550e8400-e29b-41d4-a716-446655440000", identity.Value);
    }
}
