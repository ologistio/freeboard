namespace Freeboard.Core.Assets;

/// <summary>
/// How much damage a vendor can do. A permanent closed set of four: nothing adds a fifth tier
/// without a code change, which is why this is an enum where <see cref="VendorDataClass"/> is not.
/// Valid on a <see cref="AssetKind.Vendor"/> asset only.
/// </summary>
public enum VendorTier
{
    Critical,
    High,
    Medium,
    Low,
}

/// <summary>
/// Which regulated data a vendor holds. Tokens are defined by regulatory REGIME, not by data type:
/// <c>phi</c> is HIPAA-regulated and <c>special-category</c> is UK/EU GDPR Article 9, so health data
/// is correctly authored as both. The tokens carry no ranking against each other.
/// <para>
/// Deliberately a string set rather than an enum: an admin-editable taxonomy is a planned enterprise
/// feature, and an enum cannot hold a value it was not compiled with, so every layer would change type
/// on the day that ships. Nothing switches on a data class today, so the exhaustiveness costs nothing.
/// </para>
/// </summary>
public static class VendorDataClass
{
    /// <summary>Closed token set for a vendor's data classes (case-sensitive).</summary>
    public static readonly IReadOnlySet<string> Tokens = new HashSet<string>(StringComparer.Ordinal)
    {
        "pii", "phi", "special-category", "payment-card", "credentials",
    };
}
