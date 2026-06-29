using System.Security.Cryptography;
using System.Text;

namespace Freeboard.Auth;

/// <summary>
/// Generates a cryptographically random one-time temporary password. Returned to the
/// admin once on user-create / reset-password; only its Argon2id hash is stored, never the
/// plaintext. Crockford-style base32 groups for readability.
/// </summary>
public static class TempPassword
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // no I/L/O/U
    private const int GroupCount = 4;
    private const int GroupLength = 5; // 20 chars * 5 bits = 100 bits of entropy.

    public static string Generate()
    {
        var totalChars = GroupCount * GroupLength;
        var builder = new StringBuilder(totalChars + GroupCount - 1);
        for (var i = 0; i < totalChars; i++)
        {
            if (i > 0 && i % GroupLength == 0)
            {
                builder.Append('-');
            }

            builder.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }

        return builder.ToString();
    }
}
