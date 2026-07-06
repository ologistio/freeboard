using Freeboard.Core;

namespace Freeboard.Auth;

/// <summary>
/// Generates a cryptographically random one-time temporary password. Returned to the
/// admin once on user-create / reset-password; only its Argon2id hash is stored, never the
/// plaintext. Crockford-style base32 groups for readability.
/// </summary>
public static class TempPassword
{
    private const int GroupCount = 4;
    private const int GroupLength = 5; // 20 chars * 5 bits = 100 bits of entropy.

    public static string Generate() => ReadableCode.Generate(GroupCount, GroupLength);
}
