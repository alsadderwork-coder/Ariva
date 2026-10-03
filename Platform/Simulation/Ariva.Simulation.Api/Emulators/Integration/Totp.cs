using System.Security.Cryptography;

namespace Ariva.Simulation.Api.Emulators.Integration;

/// <summary>
/// RFC 6238 time-based one-time codes as AMAN and Ariva use them (ARV-029): HMAC-SHA1, 30-second steps, 6 digits, a
/// Base32 seed (RFC 4648, no padding needed). The emulators generate codes for their own integration clients and the
/// mock AMAN verifies the codes Ariva's outbound connector sends. Codes are compared in constant time (CWE-208).
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;

    /// <summary>The time step of an instant.</summary>
    public static long Step(DateTimeOffset at) => at.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code of a step.</summary>
    public static string Code(byte[] key, long step)
    {
        ArgumentNullException.ThrowIfNull(key);
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
#pragma warning disable CA5350 // RFC 6238 and the AMAN client scheme are defined over HMAC-SHA1; the seed, not the hash, is the secret
        var hash = HMACSHA1.HashData(key, counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The code now.</summary>
    public static string Code(byte[] key, DateTimeOffset at) => Code(key, Step(at));

    /// <summary>
    /// Whether <paramref name="code"/> is the code of the current step or one step either side; gives the matched step.
    /// Every candidate is compared, so timing does not tell which step matched.
    /// </summary>
    public static bool Verify(byte[] key, string code, DateTimeOffset at, out long matched)
    {
        matched = -1;
        if (key is null || code is null || code.Length != Digits || !code.All(char.IsAsciiDigit))
            return false;
        var presented = System.Text.Encoding.ASCII.GetBytes(code);
        var now = Step(at);
        for (var step = now - 1; step <= now + 1; step++)
        {
            if (CryptographicOperations.FixedTimeEquals(presented, System.Text.Encoding.ASCII.GetBytes(Code(key, step))) && matched < 0)
                matched = step;
        }

        return matched >= 0;
    }

    /// <summary>The bytes of a Base32 seed (upper or lower case, optional padding), or null when it is not Base32.</summary>
    public static byte[] FromBase32(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = text.Trim().TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var value = alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
                return null;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return output.Count >= 10 ? [.. output] : null; // at least 80 bits (RFC 4226 asks for 128, recommends 160)
    }
}
