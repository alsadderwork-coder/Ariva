using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ariva.IntegrationTests.Setup;

/// <summary>
/// A minimal SMTP server on loopback for the ARV-040 tests (no container image needed): it speaks enough of RFC 5321
/// for MailKit (EHLO, MAIL, RCPT, DATA, RSET, NOOP, QUIT, without STARTTLS) and keeps each message as received on the
/// wire. A recipient in <see cref="Refuse"/> gets a 550 at RCPT; with <see cref="DropAfter"/> set, the connection is
/// closed after that many accepted messages.
/// </summary>
public sealed class SmtpSink : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    public SmtpSink()
    {
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Every message accepted: the envelope and the raw DATA (headers and body, CRLF lines, dot-unstuffed).</summary>
    public ConcurrentQueue<SmtpReceived> Received { get; } = new();

    public ConcurrentDictionary<string, bool> Refuse { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int? DropAfter { get; set; }

    public int Connections => _connections;

    private int _connections;
    private int _accepted;

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            Interlocked.Increment(ref _connections);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 sink.ariva.test ESMTP");
            string from = null;
            var to = new List<string>();
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                var verb = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();
                switch (verb)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-sink.ariva.test");
                        await writer.WriteLineAsync("250 8BITMIME");
                        break;
                    case "HELO":
                        await writer.WriteLineAsync("250 sink.ariva.test");
                        break;
                    case "MAIL":
                        from = Address(line);
                        to.Clear();
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "RCPT":
                        var recipient = Address(line);
                        if (Refuse.ContainsKey(recipient))
                        {
                            await writer.WriteLineAsync("550 5.1.1 No such mailbox");
                        }
                        else
                        {
                            to.Add(recipient);
                            await writer.WriteLineAsync("250 OK");
                        }

                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync(_stop.Token) is { } dataLine && dataLine != ".")
                            data.Append(dataLine.StartsWith("..", StringComparison.Ordinal) ? dataLine[1..] : dataLine).Append("\r\n");
                        Received.Enqueue(new SmtpReceived(from, [.. to], data.ToString()));
                        await writer.WriteLineAsync("250 OK queued");
                        if (DropAfter is { } limit && Interlocked.Increment(ref _accepted) >= limit)
                            return;
                        break;
                    case "RSET":
                        from = null;
                        to.Clear();
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "NOOP":
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 Bye");
                        return;
                    default:
                        await writer.WriteLineAsync("502 Command not implemented");
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away, or the sink is stopping.
        }
    }

    private static string Address(string line)
    {
        var start = line.IndexOf('<', StringComparison.Ordinal);
        var end = line.IndexOf('>', StringComparison.Ordinal);
        return start >= 0 && end > start ? line[(start + 1)..end] : line;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        _stop.Dispose();
    }
}

/// <summary>One message as the sink received it.</summary>
public sealed record SmtpReceived(string From, IReadOnlyList<string> To, string Data)
{
    /// <summary>The header lines (before the first blank line), unfolded.</summary>
    public IReadOnlyList<string> Headers
    {
        get
        {
            var head = Data.Split("\r\n\r\n", 2)[0];
            return head.Replace("\r\n ", " ", StringComparison.Ordinal).Replace("\r\n\t", " ", StringComparison.Ordinal).Split("\r\n");
        }
    }

    public string Body => Data.Split("\r\n\r\n", 2) is [_, var body] ? body : string.Empty;
}
