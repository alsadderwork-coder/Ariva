using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ariva.Core.Queueing.Replay;

/// <summary>What a replay covered: the site, its queue zones, the range, the zone profile version and the settings.</summary>
public sealed record ReplayManifest(
    string Format,
    string SiteCode,
    IReadOnlyList<string> Zones,
    DateTime FromUtc,
    DateTime ToUtc,
    int ProfileVersion,
    string EngineVersion,
    string SettingsHash)
{
    public const string CurrentFormat = "ariva-replay/1";
}

/// <summary>
/// The heads of a replay's two hash chains: over its inputs (the archived records, in order) and over its outputs (every
/// row it produced, in order), and the replay hash that binds both to the manifest. The output head alone is the
/// replay's stable output hash: the same records under the same profile version and settings always give it.
/// </summary>
public sealed record ReplayHashes(string Genesis, long Inputs, long Outputs, string InputHead, string OutputHead, string ReplayHash);

/// <summary>The result of checking an exported replay.</summary>
public sealed record ReplayVerification(bool Valid, ReplayManifest Manifest, ReplayHashes Hashes, IReadOnlyList<string> Problems);

/// <summary>
/// Hash chains over a replay (ARV-036) and its export, one JSON document per line:
/// <list type="bullet">
/// <item><c>{"kind":"manifest","genesis":…,"manifest":{…}}</c> first;</item>
/// <item><c>{"kind":"in","seq":n,"zone":…,"chain":…,"record":{…}}</c> for each input;</item>
/// <item><c>{"kind":"out","seq":n,"zone":…,"type":…,"chain":…,"record":{…}}</c> for each output;</item>
/// <item><c>{"kind":"end",…}</c> last, with both heads and the replay hash.</item>
/// </list>
/// Each chain value is SHA-256 of the previous value and the line's kind, number, zone, type and record bytes, starting
/// from a fixed seed per chain; the replay hash is SHA-256 of the genesis (the manifest's hash) and both heads. Changing,
/// dropping, adding or reordering a line breaks every chain value after it, and the end line no longer matches; checked
/// against the replay's record kept in the database (replay_run), a recomputed chain shows too (tamper-evident).
/// </summary>
public sealed class ReplayLedger
{
    /// <summary>JSON for hashing: camelCase, enums as names, no indentation; the same bytes on every run.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly byte[] InputSeed = SHA256.HashData("ariva-replay/1 inputs"u8);
    private static readonly byte[] OutputSeed = SHA256.HashData("ariva-replay/1 outputs"u8);

    private readonly TextWriter _export;
    private readonly byte[] _genesis;
    private byte[] _input = InputSeed;
    private byte[] _output = OutputSeed;
    private long _inputs;
    private long _outputs;

    public ReplayLedger(ReplayManifest manifest, TextWriter export = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Manifest = manifest;
        _export = export;
        var manifestJson = JsonSerializer.Serialize(manifest, Json);
        _genesis = SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson));
        _export?.Write(ManifestLine(Hex(_genesis), manifestJson) + "\n");
    }

    public ReplayManifest Manifest { get; }

    public void Input(string zoneKey, object record)
    {
        var json = JsonSerializer.Serialize(record, Json);
        _inputs++;
        _input = Link(_input, "in", _inputs, zoneKey, null, json);
        _export?.Write(InputLine(_inputs, zoneKey, Hex(_input), json) + "\n");
    }

    public void Output(string zoneKey, string type, object record)
    {
        var json = JsonSerializer.Serialize(record, Json);
        _outputs++;
        _output = Link(_output, "out", _outputs, zoneKey, type, json);
        _export?.Write(OutputLine(_outputs, zoneKey, type, Hex(_output), json) + "\n");
    }

    /// <summary>Every output of a zone's drain, in a fixed order: minutes, bins, live rows, recomputations, outages, line minutes (ARV-113).</summary>
    public void Outputs(ZoneOutputs outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        foreach (var m in outputs.Minutes)
            Output(outputs.ZoneKey, "minute", m);
        foreach (var b in outputs.Bins)
            Output(outputs.ZoneKey, "bin", b);
        foreach (var l in outputs.Live)
            Output(outputs.ZoneKey, "live", l);
        foreach (var r in outputs.Recomputations)
            Output(outputs.ZoneKey, "recomputation", r);
        foreach (var o in outputs.Outages)
            Output(outputs.ZoneKey, "outage", o);
        foreach (var l in outputs.Lines)
            Output(outputs.ZoneKey, "line", l);
    }

    public ReplayHashes End()
    {
        var hashes = Hashes(_genesis, _inputs, _outputs, _input, _output);
        _export?.Write(EndLine(hashes) + "\n");
        _export?.Flush();
        return hashes;
    }

    private static ReplayHashes Hashes(byte[] genesis, long inputs, long outputs, byte[] input, byte[] output)
    {
        var replay = SHA256.HashData([.. genesis, .. input, .. output, .. Encoding.UTF8.GetBytes($"{inputs}|{outputs}")]);
        return new ReplayHashes(Hex(genesis), inputs, outputs, Hex(input), Hex(output), Hex(replay));
    }

    private static byte[] Link(byte[] previous, string kind, long seq, string zone, string type, string json)
    {
        var head = Encoding.UTF8.GetBytes($"{kind}|{seq.ToString(CultureInfo.InvariantCulture)}|{zone}|{type}|");
        return SHA256.HashData([.. previous, .. head, .. Encoding.UTF8.GetBytes(json)]);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>The longest export line accepted when checking (a record of 3,000 events with its framing).</summary>
    public const int MaxLineLength = 4 * 1024 * 1024;

    /// <summary>
    /// Checks an exported replay line by line: the manifest, every chain value, the counts and the end line. Each line must
    /// be exactly as the writer writes it, so no extra or repeated property can stand beside the hashed ones (CWE-345).
    /// </summary>
    public static ReplayVerification Verify(TextReader export)
    {
        ArgumentNullException.ThrowIfNull(export);
        var problems = new List<string>();
        ReplayManifest manifest = null;
        byte[] genesis = null;
        byte[] input = InputSeed, output = OutputSeed;
        long inputs = 0, outputs = 0, line = 0;
        ReplayHashes ended = null;
        string text;
        while ((text = ReadLine(export, MaxLineLength, out var tooLong)) is not null && problems.Count < 20)
        {
            line++;
            if (tooLong)
            {
                problems.Add($"Line {line} is longer than {MaxLineLength} characters.");
                break;
            }

            if (ended is not null)
            {
                problems.Add($"Line {line} follows the end line.");
                break;
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                var kind = root.GetProperty("kind").GetString();
                if (line == 1 != (kind == "manifest"))
                {
                    problems.Add($"Line {line}: the manifest must be the first line, and only it.");
                    break;
                }

                switch (kind)
                {
                    case "manifest":
                        var raw = root.GetProperty("manifest").GetRawText();
                        manifest = JsonSerializer.Deserialize<ReplayManifest>(raw, Json);
                        genesis = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
                        if (!string.Equals(Hex(genesis), root.GetProperty("genesis").GetString(), StringComparison.Ordinal))
                            problems.Add("The manifest does not match its genesis hash.");
                        if (manifest?.Format != ReplayManifest.CurrentFormat)
                            problems.Add($"The format is not {ReplayManifest.CurrentFormat}.");
                        else if (text != ManifestLine(Hex(genesis), raw) || raw != JsonSerializer.Serialize(manifest, Json))
                            problems.Add("Line 1 is not the manifest line as written (an extra or repeated property).");
                        break;
                    case "in":
                        inputs++;
                        input = Check(root, text, input, "in", inputs, null, line, problems);
                        break;
                    case "out":
                        outputs++;
                        output = Check(root, text, output, "out", outputs, root.GetProperty("type").GetString(), line, problems);
                        break;
                    case "end":
                        ended = Hashes(genesis ?? [], inputs, outputs, input, output);
                        if (text != EndLine(ended))
                            problems.Add("The end line does not match the chains.");
                        break;
                    default:
                        problems.Add($"Line {line} has an unknown kind.");
                        break;
                }
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                problems.Add($"Line {line} is not a replay line.");
            }
        }

        if (ended is null && problems.Count == 0)
            problems.Add("The export has no end line; it is incomplete.");
        return new ReplayVerification(problems.Count == 0, manifest, ended, problems);
    }

    private static byte[] Check(JsonElement root, string text, byte[] previous, string kind, long expected, string type, long line, List<string> problems)
    {
        var seq = root.GetProperty("seq").GetInt64();
        var zone = root.GetProperty("zone").GetString();
        var record = root.GetProperty("record").GetRawText();
        var next = Link(previous, kind, expected, zone, type, record);
        if (seq != expected)
            problems.Add($"Line {line}: {kind} number {seq} where {expected} was due (a line was dropped, added or moved).");
        else if (!string.Equals(Hex(next), root.GetProperty("chain").GetString(), StringComparison.Ordinal))
            problems.Add($"Line {line}: the chain does not match (the line or one before it was changed).");
        else if (text != (type is null ? InputLine(seq, zone, Hex(next), record) : OutputLine(seq, zone, type, Hex(next), record)))
            problems.Add($"Line {line} is not the line as written (an extra or repeated property).");
        return next;
    }

    // The writer's exact lines; Verify rebuilds each line from its parsed values and compares.
    private static string ManifestLine(string genesis, string manifestJson) =>
        "{\"kind\":\"manifest\",\"genesis\":\"" + genesis + "\",\"manifest\":" + manifestJson + "}";

    private static string InputLine(long seq, string zone, string chain, string record) =>
        "{\"kind\":\"in\",\"seq\":" + seq.ToString(CultureInfo.InvariantCulture) + ",\"zone\":" + JsonSerializer.Serialize(zone, Json) +
        ",\"chain\":\"" + chain + "\",\"record\":" + record + "}";

    private static string OutputLine(long seq, string zone, string type, string chain, string record) =>
        "{\"kind\":\"out\",\"seq\":" + seq.ToString(CultureInfo.InvariantCulture) + ",\"zone\":" + JsonSerializer.Serialize(zone, Json) +
        ",\"type\":" + JsonSerializer.Serialize(type, Json) + ",\"chain\":\"" + chain + "\",\"record\":" + record + "}";

    private static string EndLine(ReplayHashes h) =>
        "{\"kind\":\"end\",\"inputs\":" + h.Inputs.ToString(CultureInfo.InvariantCulture) + ",\"outputs\":" + h.Outputs.ToString(CultureInfo.InvariantCulture) +
        ",\"inputHead\":\"" + h.InputHead + "\",\"outputHead\":\"" + h.OutputHead + "\",\"replayHash\":\"" + h.ReplayHash + "\"}";

    // A line of at most max characters, read without holding more of it (a hostile export cannot exhaust memory, CWE-400).
    private static string ReadLine(TextReader reader, int max, out bool tooLong)
    {
        tooLong = false;
        var builder = new StringBuilder();
        int c;
        while ((c = reader.Read()) != -1)
        {
            if (c == '\n')
                return builder.ToString();
            if (builder.Length >= max)
            {
                tooLong = true;
                return string.Empty;
            }

            builder.Append((char)c);
        }

        return builder.Length > 0 ? builder.ToString() : null;
    }
}
