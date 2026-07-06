using System.Security.Cryptography;
using System.Text;

namespace Freeboard.Core;

/// <summary>
/// Generates a CSPRNG secret as hyphen-separated Crockford-style base32 groups (no I/L/O/U) for
/// readable, transcription-safe one-time codes. The caller chooses the group shape; only a hash of
/// the returned code should ever be stored. Sharing this keeps the alphabet and format from drifting
/// between the temp-password and recovery-code generators.
/// </summary>
public static class ReadableCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // no I/L/O/U

    /// <summary>Returns <paramref name="groupCount"/> groups of <paramref name="groupLength"/> base32 chars, hyphen-joined.</summary>
    public static string Generate(int groupCount, int groupLength)
    {
        var totalChars = groupCount * groupLength;
        var builder = new StringBuilder(totalChars + groupCount - 1);
        for (var i = 0; i < totalChars; i++)
        {
            if (i > 0 && i % groupLength == 0)
            {
                builder.Append('-');
            }

            builder.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }

        return builder.ToString();
    }
}
