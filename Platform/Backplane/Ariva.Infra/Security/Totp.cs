using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ariva.Infra.Security;

/// <summary>
/// TOTP per RFC 6238 with the parameters every authenticator app supports: HMAC-SHA1, 6 digits, 30-second steps
/// (ADR-0026, ARV-010c). Secrets are 160 bits from the CSPRNG (RFC 4226 recommends at least 128). A code is accepted
/// for the current step and one step either side, and only for a step later than the last one accepted (replay
/// guard, CWE-294); the caller stores that step.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    public const int SecretBytes = 20;
    public const string DataProtectionPurpose = "Ariva.Totp.v1";

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The 6-digit code for a time step (RFC 4226 dynamic truncation).</summary>
    public static string Code(byte[] secret, long step)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        // RFC 6238 with SHA-1 is what every authenticator app implements. HMAC-SHA1 is still a sound PRF; the SHA-1
        // collision attacks do not apply to HMAC (RFC 6151). Used for nothing but TOTP codes.
#pragma warning disable CA5350
        var hash = HMACSHA1.HashData(secret, counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The step a code matches within <paramref name="skewSteps"/> of <paramref name="now"/> and after
    /// <paramref name="lastStep"/>, or null. Every candidate is compared in constant time.
    /// </summary>
    public static long? Match(byte[] secret, string code, DateTimeOffset now, long? lastStep, int skewSteps = 1)
    {
        var candidate = Normalize(code);
        if (candidate is null)
            return null;

        long? matched = null;
        var current = StepAt(now);
        for (var step = current - skewSteps; step <= current + skewSteps; step++)
        {
            var equal = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(candidate));
            if (equal && (lastStep is null || step > lastStep) && matched is null)
                matched = step;
        }

        return matched;
    }

    /// <summary>The otpauth URI an authenticator app reads from the QR code.</summary>
    public static string OtpAuthUri(string issuer, string account, byte[] secret)
    {
        var label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(account);
        return $"otpauth://totp/{label}?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    /// <summary>Six digits, spaces allowed (authenticator apps show "123 456"); anything else is not a code.</summary>
    private static string Normalize(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        var digits = code.Replace(" ", string.Empty, StringComparison.Ordinal);
        return digits.Length == Digits && digits.All(char.IsAsciiDigit) ? digits : null;
    }
}

/// <summary>RFC 4648 base32 without padding, the encoding authenticator apps expect for secrets.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
            output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }

    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clean = text.Trim().TrimEnd('=').Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var value = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
                throw new FormatException("Not base32.");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. output];
    }
}

/// <summary>
/// Recovery codes (ARV-010c): ten characters of Crockford base32 (50 random bits), shown as XXXXX-XXXXX and accepted
/// with or without the dash, in any case, with the Crockford substitutions (O for 0, I and L for 1). Stored as the
/// hex SHA-256 of the normalised value.
/// </summary>
public static class RecoveryCodes
{
    public const int Count = 10;
    public const int Length = 10;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New()
    {
        var code = new char[Length];
        for (var i = 0; i < Length; i++)
            code[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(code, 0, 5) + "-" + new string(code, 5, Length - 5);
    }

    /// <summary>The canonical form, or null when the text cannot be a recovery code.</summary>
    public static string Normalize(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        var builder = new StringBuilder(Length);
        foreach (var raw in code.Trim().ToUpperInvariant())
        {
            var c = raw switch { 'O' => '0', 'I' or 'L' => '1', _ => raw };
            if (c is '-' or ' ')
                continue;
            if (!Alphabet.Contains(c, StringComparison.Ordinal))
                return null;
            builder.Append(c);
        }

        return builder.Length == Length ? builder.ToString() : null;
    }

    public static string Hash(string normalized) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(normalized)));
}
