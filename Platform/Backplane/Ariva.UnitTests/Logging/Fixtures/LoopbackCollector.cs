using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ariva.UnitTests.Logging.Fixtures;

/// <summary>
/// A plain HTTP listener on loopback for the log and telemetry shipping tests (ARV-091a): it keeps the request line and the
/// headers of each request a sink or an exporter sends, answers 200 with an empty body (an accepted Loki push, an empty OTLP
/// export response) and closes the connection. Plain HTTP only, so the tests that use it run as vm-local, where a sink may use http. It reads at most
/// <see cref="MaxHeadBytes"/> of each request's head and never its body.
/// </summary>
public sealed class LoopbackCollector : IAsyncDisposable
{
    #region Fields

    /// <summary>The largest request head kept; a longer one ends the connection.</summary>
    public const int MaxHeadBytes = 64 * 1024;

    private static readonly byte[] Accepted = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<CapturedRequest> _received = [];
    private readonly List<(Func<CapturedRequest, bool> Match, TaskCompletionSource<CapturedRequest> Done)> _waiting = [];
    private readonly Lock _gate = new();
    private readonly Task _loop;

    #endregion

    #region Constructors

    /// <summary>Starts listening on a free loopback port.</summary>
    public LoopbackCollector()
    {
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    #endregion

    #region Properties

    /// <summary>The listener's address, for example <c>http://127.0.0.1:41234</c>.</summary>
    public string Address => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    /// <summary>The request lines received so far, for a failure's message.</summary>
    public IReadOnlyList<string> RequestLines
    {
        get
        {
            lock (_gate)
                return _received.Select(r => r.RequestLine).ToList();
        }
    }

    /// <summary>Completes with the first request received.</summary>
    public Task<CapturedRequest> FirstRequest => RequestMatching(_ => true);

    #endregion

    #region Methods

    /// <summary>Completes with the first request, received already or later, that the predicate accepts.</summary>
    /// <param name="match">The predicate.</param>
    /// <returns>The request.</returns>
    public Task<CapturedRequest> RequestMatching(Func<CapturedRequest, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        lock (_gate)
        {
            var received = _received.FirstOrDefault(match);
            if (received is not null)
                return Task.FromResult(received);
            var done = new TaskCompletionSource<CapturedRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Add((match, done));
            return done.Task;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Stopped while waiting for a connection.
        }

        lock (_gate)
        {
            foreach (var (_, done) in _waiting)
                done.TrySetCanceled();
            _waiting.Clear();
        }

        _stop.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var head = await ReadHeadAsync(stream, _stop.Token);
                    if (head is null)
                        continue;
                    await stream.WriteAsync(Accepted, _stop.Token);
                    Keep(CapturedRequest.Parse(head));
                }
                catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    // The sender went away, or the collector is stopping: nothing to keep.
                }
            }
        }
    }

    private void Keep(CapturedRequest request)
    {
        lock (_gate)
        {
            _received.Add(request);
            foreach (var waiter in _waiting.Where(w => w.Match(request)).ToList())
            {
                waiter.Done.TrySetResult(request);
                _waiting.Remove(waiter);
            }
        }
    }

    /// <summary>The bytes up to the blank line that ends the head, as ASCII; null when the connection ends first or the head is too long.</summary>
    private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeadBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), ct);
            if (read == 0)
                return null;
            length += read;
            var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
                return Encoding.ASCII.GetString(buffer, 0, end);
        }

        return null;
    }

    #endregion
}
