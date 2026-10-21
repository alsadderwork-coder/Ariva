using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ariva.UnitTests.Setup;

/// <summary>
/// A clamd stand-in on a loopback port (ARV-092b) that speaks INSTREAM: it reads the command up to its NUL, reassembles
/// the length-prefixed chunks up to the zero-length end mark and records each session (the command, every chunk's
/// length, the bytes and whether the end mark came), then answers as <see cref="Mode"/> says. <see cref="StreamMaxLength"/>
/// plays clamd's own limit: past it the answer is clamd's size limit error, after the stream is read whole. Also answers
/// <c>zPING</c> with <c>PONG</c>, and <c>zVERSION</c> (ARV-092c) with <see cref="VersionReply"/> as <see cref="Mode"/> allows
/// (<see cref="Behaviour.Silent"/>, <see cref="Behaviour.Reset"/>, <see cref="Behaviour.Overlong"/> and <see cref="Behaviour.Delay"/>
/// as for a scan; any other mode answers), counted in <see cref="VersionQueries"/> and not in <see cref="Connections"/> or
/// <see cref="Sessions"/>, so the signature check of a host never shows as a scan. Linked into Ariva.IntegrationTests for the
/// upload services' tests.
/// </summary>
public sealed class FakeClamd : IAsyncDisposable
{
    #region Fields

    /// <summary>clamd's answer to a stream longer than its StreamMaxLength.</summary>
    public const string SizeLimitReply = "INSTREAM size limit exceeded. ERROR";

    /// <summary>A name that tries to forge a log line.</summary>
    public const string HostileName = "Evil\r\n2026-10-20T08:00:00Z [ERR] forged line\u0007";

    /// <summary>The pinned ClamAV image's answer to VERSION (clamav/clamav:1.4.6, its daily.cvd 28157 of 18 October 2026), without its NUL.</summary>
    public const string PinnedImageVersionReply = "ClamAV 1.4.6/28157/Sun Oct 18 06:24:14 2026";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<Session> _sessions = new();
    private readonly Task _accepting;
    private int _connections;
    private int _versionQueries;

    #endregion

    #region Constructors

    public FakeClamd()
    {
        _listener.Start();
        _accepting = AcceptAsync();
    }

    #endregion

    #region Properties

    /// <summary>The answers the fake gives.</summary>
    public enum Behaviour
    {
        /// <summary>"stream: OK".</summary>
        Ok,

        /// <summary>"stream: <see cref="FoundName"/> FOUND".</summary>
        Found,

        /// <summary>As clamd with its signatures: the EICAR test file is found as Eicar-Test-Signature, anything else is OK.</summary>
        Detect,

        /// <summary>An error line.</summary>
        Error,

        /// <summary>FOUND with <see cref="HostileName"/>.</summary>
        Hostile,

        /// <summary>2 KB without a NUL.</summary>
        Overlong,

        /// <summary>The connection closed without an answer.</summary>
        Silent,

        /// <summary>The connection reset as soon as it is accepted.</summary>
        Reset,

        /// <summary>"stream: OK" after <see cref="Delay"/>.</summary>
        Delay
    }

    public string Host => "127.0.0.1";

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public Behaviour Mode { get; set; } = Behaviour.Detect;

    public string FoundName { get; set; } = "Win.Test.EICAR_HDB-1";

    public long StreamMaxLength { get; set; } = long.MaxValue;

    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The answer to VERSION, without its NUL; null to close the connection without one.</summary>
    public string VersionReply { get; set; } = PinnedImageVersionReply;

    /// <summary>Connections accepted so far, but those that asked VERSION.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>VERSION commands read so far.</summary>
    public int VersionQueries => Volatile.Read(ref _versionQueries);

    /// <summary>The INSTREAM sessions read so far, in the order they ended.</summary>
    public IReadOnlyList<Session> Sessions => [.. _sessions];

    #endregion

    #region Methods

    /// <summary>Waits until at least <paramref name="count"/> sessions were recorded (a client that drops its connection ends one after it returns).</summary>
    public async Task<IReadOnlyList<Session>> WaitForSessionsAsync(int count, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (_sessions.Count < count)
            await Task.Delay(20, timeout.Token);
        return Sessions;
    }

    /// <summary>Forgets the sessions and connections seen so far.</summary>
    public void Clear()
    {
        _sessions.Clear();
        Interlocked.Exchange(ref _connections, 0);
        Interlocked.Exchange(ref _versionQueries, 0);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            Interlocked.Increment(ref _connections);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var mode = Mode;
            try
            {
                var ct = _stop.Token;
                var stream = client.GetStream();
                var command = await ReadCommandAsync(stream, ct);
                // A reset once the command has arrived, as clamd drops a connection mid-stream: resetting at accept raced the
                // client's connect, which then failed as ConnectFailed about once in six runs instead of ConnectionLost.
                if (mode == Behaviour.Reset)
                {
                    if (command == "zVERSION")
                    {
                        Interlocked.Decrement(ref _connections);
                        Interlocked.Increment(ref _versionQueries);
                    }

                    client.Client.LingerState = new LingerOption(true, 0);
                    client.Client.Close();
                    return;
                }

                if (command == "zPING")
                {
                    await stream.WriteAsync("PONG\0"u8.ToArray(), ct);
                    return;
                }

                if (command == "zVERSION")
                {
                    Interlocked.Decrement(ref _connections);
                    Interlocked.Increment(ref _versionQueries);
                    var version = await VersionAsync(mode, ct);
                    if (version is not null)
                        await stream.WriteAsync(Encoding.Latin1.GetBytes(version), ct);
                    return;
                }

                var chunks = new List<int>();
                using var content = new MemoryStream();
                var ended = false;
                var header = new byte[4];
                while (await ReadExactlyAsync(stream, header, ct))
                {
                    var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
                    if (length == 0)
                    {
                        ended = true;
                        break;
                    }

                    var data = new byte[length];
                    if (!await ReadExactlyAsync(stream, data, ct))
                        break;
                    chunks.Add(length);
                    content.Write(data);
                }

                var bytes = content.ToArray();
                _sessions.Enqueue(new Session(command, chunks, bytes, ended));
                if (!ended)
                    return;
                var reply = bytes.LongLength > StreamMaxLength ? SizeLimitReply + "\0" : await ReplyAsync(mode, bytes, ct);
                if (reply is not null)
                    await stream.WriteAsync(Encoding.Latin1.GetBytes(reply), ct);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The client went away, or the fake is stopping.
            }
        }
    }

    private async Task<string> ReplyAsync(Behaviour mode, byte[] content, CancellationToken ct)
    {
        switch (mode)
        {
            case Behaviour.Ok:
                return "stream: OK\0";
            case Behaviour.Found:
                return $"stream: {FoundName} FOUND\0";
            case Behaviour.Detect:
                return Eicar.IsIn(content) ? $"stream: {Eicar.Signature} FOUND\0" : "stream: OK\0";
            case Behaviour.Error:
                return "stream: Can't create temporary file ERROR\0";
            case Behaviour.Hostile:
                return $"stream: {HostileName} FOUND\0";
            case Behaviour.Overlong:
                return new string('A', 2048);
            case Behaviour.Delay:
                await Task.Delay(Delay, ct);
                return "stream: OK\0";
            default:
                return null;
        }
    }

    private async Task<string> VersionAsync(Behaviour mode, CancellationToken ct)
    {
        switch (mode)
        {
            case Behaviour.Silent:
                return null;
            case Behaviour.Overlong:
                return new string('A', 2048);
            case Behaviour.Delay:
                await Task.Delay(Delay, ct);
                break;
        }

        return VersionReply is null ? null : VersionReply + "\0";
    }

    private static async Task<string> ReadCommandAsync(NetworkStream stream, CancellationToken ct)
    {
        var command = new StringBuilder();
        var one = new byte[1];
        while (command.Length < 64 && await stream.ReadAsync(one, ct) == 1 && one[0] != 0)
            command.Append((char)one[0]);
        return command.ToString();
    }

    /// <summary>Fills the buffer, or false when the connection ends first.</summary>
    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
                return false;
            total += read;
        }

        return true;
    }

    #endregion

    /// <summary>One INSTREAM session as the fake read it.</summary>
    public sealed record Session(string Command, IReadOnlyList<int> Chunks, byte[] Content, bool Ended);
}
