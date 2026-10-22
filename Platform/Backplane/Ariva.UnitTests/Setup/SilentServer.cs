using System.Net;
using System.Net.Sockets;

namespace Ariva.UnitTests.Setup;

/// <summary>
/// A TCP server on a free loopback port that accepts every connection and never answers (ARV-096e): a database or a scanner
/// that has stalled. Each connection is held open until the server is disposed.
/// </summary>
public sealed class SilentServer : IAsyncDisposable
{
    #region Fields

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpClient> _held = [];
    private readonly Task _accepting;

    #endregion

    #region Constructors

    public SilentServer()
    {
        _listener.Start();
        _accepting = AcceptAsync();
    }

    #endregion

    #region Properties

    /// <summary>The port it listens on.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The connections accepted so far.</summary>
    public int Accepted
    {
        get
        {
            lock (_held)
                return _held.Count;
        }
    }

    #endregion

    #region Methods

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_held)
                    _held.Add(client);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stopped.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
        lock (_held)
        {
            foreach (var client in _held)
                client.Dispose();
            _held.Clear();
        }

        _stop.Dispose();
    }

    #endregion
}
