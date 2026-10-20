using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
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
/// into a verdict.
/// </summary>
internal sealed class ClamdScanner : IMalwareScanner
{
    #region Fields

    /// <summary>The largest chunk INSTREAM sends.</summary>
    public const int ChunkBytes = 64 * 1024;

    /// <summary>The longest reply read; a longer one is no verdict.</summary>
    public const int MaxReplyBytes = 1024;

    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(5);

    private static readonly byte[] Command = "zINSTREAM\0"u8.ToArray();

    private static readonly byte[] EndMark = new byte[4];

    private readonly MalwareScanSettings _settings;

    private readonly TimeSpan _timeout;

    private readonly TimeSpan _connectTimeout;

    #endregion

    #region Constructors

    public ClamdScanner(IOptions<MalwareScanSettings> options)
        : this(options?.Value, null, null)
    {
    }

    /// <summary>For tests: timeouts shorter than the settings allow.</summary>
    internal ClamdScanner(MalwareScanSettings settings, TimeSpan? timeout, TimeSpan? connectTimeout)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _timeout = timeout ?? TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, MalwareScanSettings.MinTimeoutSeconds, MalwareScanSettings.MaxTimeoutSeconds));
        var connect = connectTimeout ?? DefaultConnectTimeout;
        _connectTimeout = connect < _timeout ? connect : _timeout;
    }

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
            return await ReplyAsync(network, scan.Token);
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

    /// <summary>The reply up to its first NUL or the end of the stream; no verdict when it runs past <see cref="MaxReplyBytes"/>.</summary>
    private static async Task<MalwareScan> ReplyAsync(NetworkStream network, CancellationToken ct)
    {
        var reply = new byte[MaxReplyBytes];
        var length = 0;
        while (true)
        {
            if (length == reply.Length)
                return MalwareScan.Unavailable(MalwareScanReasons.ReplyTooLong);
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

        // Latin-1 maps each byte to one character, so nothing is lost or merged before the strict checks above.
        return Verdict(Encoding.Latin1.GetString(reply, 0, length));
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
