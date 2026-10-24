using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Services.Storage;
using Microsoft.Extensions.Options;

namespace Ariva.Infra.Storage;

/// <summary>
/// A clamd client over TCP speaking INSTREAM (ARV-092b, ClamAV's clamd protocol), written in Ariva: it connects to
/// <c>MalwareScan:Host</c>:<c>MalwareScan:Port</c> (configuration only, never an address from a request: CWE-918) within
/// <see cref="DefaultConnectTimeout"/>, sends <c>zINSTREAM\0</c>, the content in chunks of at most 64 KB each preceded by
/// its length as 4 bytes big-endian, and 4 zero bytes, then reads the reply up to its first NUL or the end of the stream,
/// at most 1 KB. <c>stream: OK</c> is clean; <c>stream: &lt;name&gt; FOUND</c> is a detection, its name kept only as
/// <see cref="MalwareScan.SafeSignature"/> allows; anything else (an error line, the size limit refusal, no reply, a
/// refused or reset connection, a timeout) is no verdict, with a fixed reason and never the reply's text. Content longer
/// than <c>MalwareScan:MaxStreamBytes</c> gets no verdict either, and nothing beyond the limit is sent: a stream whose
/// length is known is refused before connecting, any other is cut off before the chunk that would cross the limit and the
/// connection dropped without its end mark, so clamd never answers for part of a file. The whole scan has
/// <c>MalwareScan:TimeoutSeconds</c>, linked to the caller's token; the caller's own cancellation is thrown, never turned
/// into a verdict. <see cref="SignaturesAsync"/> asks clamd for its signature database (<c>zVERSION\0</c>, ARV-092c) under
/// <see cref="DefaultVersionTimeout"/> and keeps only what <see cref="Signatures"/>' strict pattern accepts.
/// </summary>
internal sealed partial class ClamdScanner : IMalwareScanner
{
    #region Fields

    /// <summary>The largest chunk INSTREAM sends.</summary>
    public const int ChunkBytes = 64 * 1024;

    /// <summary>The longest reply read; a longer one is no verdict.</summary>
    public const int MaxReplyBytes = 1024;

    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The whole VERSION exchange's deadline (connect, ask, answer): clamd answers it at once, from memory.</summary>
    public static readonly TimeSpan DefaultVersionTimeout = TimeSpan.FromSeconds(10);

    private static readonly byte[] Command = "zINSTREAM\0"u8.ToArray();

    private static readonly byte[] VersionCommand = "zVERSION\0"u8.ToArray();

    private static readonly byte[] EndMark = new byte[4];

    /// <summary>The asctime month names (C locale), in order.</summary>
    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>The asctime day names (C locale), Sunday first as <see cref="DayOfWeek"/> counts.</summary>
    private static readonly string[] Days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    private readonly MalwareScanSettings _settings;

    private readonly TimeSpan _timeout;

    private readonly TimeSpan _connectTimeout;

    private readonly TimeSpan _versionTimeout;

    #endregion

    #region Constructors

    public ClamdScanner(IOptions<MalwareScanSettings> options)
        : this(options?.Value, null, null)
    {
    }

    /// <summary>For tests: timeouts shorter than the settings allow.</summary>
    internal ClamdScanner(MalwareScanSettings settings, TimeSpan? timeout, TimeSpan? connectTimeout, TimeSpan? versionTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _timeout = timeout ?? TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, MalwareScanSettings.MinTimeoutSeconds, MalwareScanSettings.MaxTimeoutSeconds));
        var connect = connectTimeout ?? DefaultConnectTimeout;
        _connectTimeout = connect < _timeout ? connect : _timeout;
        _versionTimeout = versionTimeout ?? DefaultVersionTimeout;
    }

    #endregion

    #region Signatures

    /// <summary>
    /// Asks clamd for its signature database: <c>zVERSION\0</c> on a connection of its own, the reply read up to its first NUL
    /// or the end of the stream, at most <see cref="MaxReplyBytes"/>, the whole exchange within <see cref="DefaultVersionTimeout"/>
    /// linked to the caller's token. No answer, a refused or lost connection, the deadline passed, an over-long reply or one
    /// the pattern refuses: null, never the reply's text. The caller's own cancellation is thrown.
    /// </summary>
    public async Task<MalwareSignatures> SignaturesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_settings.Configured)
            return null;

        using var check = CancellationTokenSource.CreateLinkedTokenSource(ct);
        check.CancelAfter(_versionTimeout);
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(_settings.Host.Trim(), _settings.Port, check.Token);
            var network = client.GetStream();
            await network.WriteAsync(VersionCommand, check.Token);
            await network.FlushAsync(check.Token);
            var reply = await ReadReplyAsync(network, check.Token);
            return reply is null ? null : Signatures(reply);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The signatures a VERSION reply names, or null: only <c>ClamAV &lt;engine&gt;/&lt;database&gt;/&lt;built&gt;</c> exactly,
    /// the engine a digit then up to 31 letters, digits, '.' and '-', the database 1 to 9 digits without a leading zero, the
    /// build time as C's asctime writes it (<c>Sun Mar  1 07:24:39 2026</c>: a one-digit day after two spaces, a two-digit day
    /// after one), a real date whose day name matches, taken as UTC (clamd writes it in its own time zone, which the image
    /// keeps at UTC). Nothing else: no trailing newline or space, no other digits than 0 to 9, no reply without a database
    /// (clamd's <c>ClamAV 1.4.6</c> when none is loaded).
    /// </summary>
    internal static MalwareSignatures Signatures(string reply)
    {
        if (string.IsNullOrEmpty(reply) || reply.Length > MaxReplyBytes)
            return null;
        var match = VersionPattern().Match(reply);
        if (!match.Success)
            return null;

        var invariant = CultureInfo.InvariantCulture;
        if (!int.TryParse(match.Groups["database"].ValueSpan, NumberStyles.None, invariant, out var database))
            return null;
        var year = int.Parse(match.Groups["year"].ValueSpan, NumberStyles.None, invariant);
        var month = Array.IndexOf(Months, match.Groups["month"].Value) + 1;
        var day = int.Parse(match.Groups["day"].ValueSpan.Trim(' '), NumberStyles.None, invariant);
        if (month < 1 || day > DateTime.DaysInMonth(year, month))
            return null;
        var built = new DateTime(year, month, day,
            int.Parse(match.Groups["hour"].ValueSpan, NumberStyles.None, invariant),
            int.Parse(match.Groups["minute"].ValueSpan, NumberStyles.None, invariant),
            int.Parse(match.Groups["second"].ValueSpan, NumberStyles.None, invariant),
            DateTimeKind.Utc);
        return Days[(int)built.DayOfWeek] == match.Groups["weekday"].Value
            ? new MalwareSignatures(match.Groups["engine"].Value, database, built)
            : null;
    }

    // Literal ranges only ([0-9] is ASCII under CultureInvariant; \d would take any script's digits), anchored at both ends.
    // Linear (NonBacktracking), so no timeout that a loaded node could fire (ARV-163).
    [GeneratedRegex(@"\AClamAV (?<engine>[0-9][0-9A-Za-z.-]{0,31})/(?<database>[1-9][0-9]{0,8})/(?<weekday>Sun|Mon|Tue|Wed|Thu|Fri|Sat) (?<month>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) (?<day> [1-9]|[12][0-9]|3[01]) (?<hour>[01][0-9]|2[0-3]):(?<minute>[0-5][0-9]):(?<second>[0-5][0-9]) (?<year>[1-9][0-9]{3})\z",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, Timeout.Infinite)]
    internal static partial Regex VersionPattern();

    #endregion

    #region Scan

    public async Task<MalwareScan> ScanAsync(Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        ct.ThrowIfCancellationRequested();
        if (!_settings.Configured)
            return MalwareScan.Unavailable(MalwareScanReasons.NotConfigured);
        if (content.CanSeek && content.Length - content.Position > _settings.MaxStreamBytes)
            return MalwareScan.Unavailable(MalwareScanReasons.TooLarge);

        using var scan = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scan.CancelAfter(_timeout);
        var stage = Stage.Connecting;
        try
        {
            using var client = new TcpClient { NoDelay = true };
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(scan.Token))
            {
                connect.CancelAfter(_connectTimeout);
                try
                {
                    await client.ConnectAsync(_settings.Host.Trim(), _settings.Port, connect.Token);
                }
                catch (OperationCanceledException) when (!scan.IsCancellationRequested)
                {
                    return MalwareScan.Unavailable(MalwareScanReasons.ConnectTimedOut);
                }
            }

            stage = Stage.Sending;
            var network = client.GetStream();
            await network.WriteAsync(Command, scan.Token);
            var buffer = new byte[4 + ChunkBytes];
            long sent = 0;
            while (true)
            {
                stage = Stage.ReadingContent;
                var read = await FillAsync(content, buffer.AsMemory(4, ChunkBytes), scan.Token);
                stage = Stage.Sending;
                if (read == 0)
                    break;
                // Never a byte beyond the limit; the connection is dropped without the end mark, so no verdict on part of the file.
                if (sent + read > _settings.MaxStreamBytes)
                    return MalwareScan.Unavailable(MalwareScanReasons.TooLarge);
                BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)read);
                await network.WriteAsync(buffer.AsMemory(0, 4 + read), scan.Token);
                sent += read;
            }

            await network.WriteAsync(EndMark, scan.Token);
            await network.FlushAsync(scan.Token);
            stage = Stage.Reading;
            var reply = await ReadReplyAsync(network, scan.Token);
            return reply is null ? MalwareScan.Unavailable(MalwareScanReasons.ReplyTooLong) : Verdict(reply);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return MalwareScan.Unavailable(MalwareScanReasons.TimedOut);
        }
        catch (SocketException) when (stage == Stage.Connecting)
        {
            return MalwareScan.Unavailable(MalwareScanReasons.ConnectFailed);
        }
        catch (Exception e) when (e is SocketException or IOException && stage is Stage.Sending or Stage.Reading)
        {
            // Closed or reset by clamd (its own size limit closes the connection while the chunks still arrive).
            return MalwareScan.Unavailable(MalwareScanReasons.ConnectionLost);
        }
    }

    /// <summary>The verdict a reply gives: only the two well-formed answers are verdicts; anything else is a fixed reason.</summary>
    internal static MalwareScan Verdict(string reply)
    {
        if (string.IsNullOrEmpty(reply))
            return MalwareScan.Unavailable(MalwareScanReasons.NoReply);
        var text = reply.EndsWith('\n') ? reply[..^1] : reply;
        const string prefix = "stream: ";
        const string found = " FOUND";
        if (string.Equals(text, prefix + "OK", StringComparison.Ordinal))
            return MalwareScan.Clean;
        if (text.Length > prefix.Length + found.Length && text.StartsWith(prefix, StringComparison.Ordinal) && text.EndsWith(found, StringComparison.Ordinal))
            return MalwareScan.Infected(text[prefix.Length..^found.Length]);
        if (text.EndsWith("size limit exceeded. ERROR", StringComparison.Ordinal))
            return MalwareScan.Unavailable(MalwareScanReasons.SizeLimitExceeded);
        return text.EndsWith(" ERROR", StringComparison.Ordinal)
            ? MalwareScan.Unavailable(MalwareScanReasons.ScannerError)
            : MalwareScan.Unavailable(MalwareScanReasons.ReplyNotUnderstood);
    }

    /// <summary>
    /// The reply up to its first NUL or the end of the stream, each byte one character (Latin-1, so nothing is lost or merged
    /// before the strict checks); null when it runs past <see cref="MaxReplyBytes"/>.
    /// </summary>
    private static async Task<string> ReadReplyAsync(NetworkStream network, CancellationToken ct)
    {
        var reply = new byte[MaxReplyBytes];
        var length = 0;
        while (true)
        {
            if (length == reply.Length)
                return null;
            var read = await network.ReadAsync(reply.AsMemory(length), ct);
            if (read == 0)
                break;
            var end = Array.IndexOf(reply, (byte)0, length, read);
            if (end >= 0)
            {
                length = end;
                break;
            }

            length += read;
        }

        return Encoding.Latin1.GetString(reply, 0, length);
    }

    /// <summary>Reads until the chunk is full or the content ends: every chunk but the last is exactly <see cref="ChunkBytes"/>.</summary>
    private static async Task<int> FillAsync(Stream content, Memory<byte> chunk, CancellationToken ct)
    {
        var total = 0;
        while (total < chunk.Length)
        {
            var read = await content.ReadAsync(chunk[total..], ct);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }

    private enum Stage
    {
        Connecting,
        Sending,
        ReadingContent,
        Reading
    }

    #endregion
}
