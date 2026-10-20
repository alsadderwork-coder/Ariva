using System.Text;

namespace Ariva.UnitTests.Setup;

/// <summary>
/// The EICAR anti-malware test file (ARV-092b): 68 ASCII characters every scanner reports as a test detection (ClamAV
/// names it <c>Eicar-Test-Signature</c>), harmless by design. Built at run time from two halves joined by a
/// <see cref="StringBuilder"/>, never a constant or a concatenation of constants, which the compiler folds into one
/// literal: a test assembly holding the whole string would be quarantined by a developer machine's antivirus.
/// </summary>
public static class Eicar
{
    /// <summary>The ClamAV signature name of the test file.</summary>
    public const string Signature = "Eicar-Test-Signature";

    private static readonly string[] Halves = ["X5O!P%@AP[4\\PZX54(P^)7CC)7}$", "EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"];

    /// <summary>The test file's text.</summary>
    public static string Text() => new StringBuilder().Append(Halves[0]).Append(Halves[1]).ToString();

    /// <summary>The test file's bytes (ASCII).</summary>
    public static byte[] Bytes() => Encoding.ASCII.GetBytes(Text());

    /// <summary>Whether <paramref name="content"/> holds the test file.</summary>
    public static bool IsIn(ReadOnlySpan<byte> content) => content.IndexOf(Bytes()) >= 0;
}
