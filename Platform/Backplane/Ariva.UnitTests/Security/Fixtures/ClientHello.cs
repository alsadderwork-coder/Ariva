using System.Net;
using System.Net.Sockets;
using Ariva.Infra.Security;
using FluentAssertions;

namespace Ariva.UnitTests.Security.Fixtures;

/// <summary>
/// The fields of a TLS ClientHello a test reads (RFC 5246 section 7.4.1.2, RFC 8446 section 4.1.2 and 4.2.1): what a client
/// offers any server, whatever the server would accept. Shared by the outbound calls' test (ARV-205) and every other TLS
/// client's (ARV-280, <c>TlsClientTests</c>).
/// </summary>
/// <param name="LegacyVersion">The legacy_version field (0x0303 for TLS 1.2 and 1.3).</param>
/// <param name="CipherSuites">The suites offered, in order.</param>
/// <param name="SupportedVersions">The supported_versions extension's list (0x002b); empty when the client sends none.</param>
public sealed record ClientHello(ushort LegacyVersion, IReadOnlyList<ushort> CipherSuites, IReadOnlyList<ushort> SupportedVersions)
{
    /// <summary>The renegotiation SCSV (RFC 5746), which a client may list among its suites; it names no cipher.</summary>
    public const ushort RenegotiationScsv = 0x00FF;

    /// <summary>Reads one whole TLS record (its 5 byte header and its body) from a stream, at most 16 KB.</summary>
    public static async Task<byte[]> FirstRecordAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, ct);
        var length = (header[3] << 8) | header[4];
        length.Should().BeInRange(1, 16 * 1024, "a TLS record");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return [.. header, .. body];
    }

    /// <summary>The ClientHello in <paramref name="record"/>, a whole handshake record.</summary>
    public static ClientHello Parse(byte[] record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record[0].Should().Be(0x16, "a handshake record");
        var at = 5;
        record[at].Should().Be(0x01, "a ClientHello");
        at += 4;
        ushort Read16() { var value = (ushort)((record[at] << 8) | record[at + 1]); at += 2; return value; }
        var legacy = Read16();
        at += 32;
        at += 1 + record[at];
        var suitesEnd = at + 2 + ((record[at] << 8) | record[at + 1]);
        at += 2;
        var suites = new List<ushort>();
        while (at < suitesEnd)
            suites.Add(Read16());
        at += 1 + record[at];
        var extensionsEnd = at + 2 + ((record[at] << 8) | record[at + 1]);
        at += 2;
        var versions = new List<ushort>();
        while (at < extensionsEnd)
        {
            var type = Read16();
            var length = Read16();
            var next = at + length;
            if (type == 0x002b)
            {
                var listEnd = at + 1 + record[at];
                at += 1;
                while (at < listEnd)
                    versions.Add(Read16());
            }

            at = next;
        }

        return new ClientHello(legacy, suites, versions);
    }

    /// <summary>
    /// Asserts that this hello offers TLS 1.2 and 1.3 only and, on Linux, <see cref="ModernTls.CipherSuites"/> alone (the SCSV
    /// aside), TLS 1.3's first suite among them: what every Ariva TLS client offers (ASVS V12.1.1, V12.1.2).
    /// </summary>
    /// <param name="client">The client, for the assertion's message.</param>
    public void ShouldOfferOnlyModernTls(string client)
    {
        LegacyVersion.Should().Be(0x0303, "{0} puts TLS 1.2 in the legacy field, as TLS 1.3 requires", client);
        SupportedVersions.Should().BeEquivalentTo([(ushort)0x0303, (ushort)0x0304], "{0} offers TLS 1.2 and 1.3 only: never 1.0 or 1.1", client);
        if (!OperatingSystem.IsLinux())
            return;
        var listed = ModernTls.CipherSuites.Select(suite => (ushort)suite).Append(RenegotiationScsv).ToHashSet();
        CipherSuites.Should().OnlyContain(suite => listed.Contains(suite), "{0} offers the pods' suites and the renegotiation SCSV, nothing else", client)
            .And.Contain((ushort)System.Net.Security.TlsCipherSuite.TLS_AES_128_GCM_SHA256, "{0} offers TLS 1.3's suites", client)
            .And.Contain((ushort)System.Net.Security.TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256, "{0} offers TLS 1.2's suites", client);
    }
}

/// <summary>
/// A server on loopback that speaks only what a client says before its TLS handshake (nothing, PostgreSQL's SSLRequest answer,
/// or SMTP up to STARTTLS), then reads the first record of the client's ClientHello and closes: no certificate, no handshake.
/// Raw sockets only, so the capture adds no HTTP client or handler of its own.
/// </summary>
public sealed class HelloCatcher : IAsyncDisposable
{
    #region Fields

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<byte[]> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HelloPreamble _preamble;
    private Task _loop;

    #endregion

    #region Constructors

    private HelloCatcher(HelloPreamble preamble) => _preamble = preamble;

    #endregion

    #region Properties

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    #endregion

    #region Methods

    public static HelloCatcher Start(HelloPreamble preamble = HelloPreamble.None)
    {
        var catcher = new HelloCatcher(preamble);
        catcher._listener.Start();
        catcher._loop = catcher.AcceptAsync();
        return catcher;
    }

    /// <summary>The first ClientHello a client sent here, waited for at most <paramref name="within"/>.</summary>
    public async Task<ClientHello> FirstHelloAsync(TimeSpan within, CancellationToken ct) =>
        ClientHello.Parse(await _first.Task.WaitAsync(within, ct));

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

            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                if (_preamble == HelloPreamble.Postgres)
                {
                    var request = new byte[8];
                    await stream.ReadExactlyAsync(request, _stop.Token);
                    await stream.WriteAsync("S"u8.ToArray(), _stop.Token);
                }
                else if (_preamble == HelloPreamble.SmtpStartTls)
                {
                    await stream.WriteAsync("220 localhost ESMTP\r\n"u8.ToArray(), _stop.Token);
                    await ReadLineAsync(stream, _stop.Token);
                    await stream.WriteAsync("250-localhost\r\n250 STARTTLS\r\n"u8.ToArray(), _stop.Token);
                    await ReadLineAsync(stream, _stop.Token);
                    await stream.WriteAsync("220 Ready to start TLS\r\n"u8.ToArray(), _stop.Token);
                }

                var record = await ClientHello.FirstRecordAsync(stream, _stop.Token).WaitAsync(TimeSpan.FromSeconds(10), _stop.Token);
                _first.TrySetResult(record);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException or SocketException or EndOfStreamException)
            {
                // The client went away before its hello: the next connection may bring it.
            }
            catch (Exception e)
            {
                // Not a TLS record (an assertion of FirstRecordAsync): the waiting test sees it rather than a timeout.
                _first.TrySetException(e);
            }
        }
    }

    /// <summary>Reads one command line byte by byte, so nothing of the TLS handshake after it is consumed; at most 512 bytes.</summary>
    private static async Task ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var one = new byte[1];
        for (var read = 0; read < 512; read++)
        {
            await stream.ReadExactlyAsync(one, ct);
            if (one[0] == (byte)'\n')
                return;
        }
    }

    #endregion
}

/// <summary>What a <see cref="HelloCatcher"/> says before it reads the ClientHello.</summary>
public enum HelloPreamble
{
    None,
    Postgres,
    SmtpStartTls
}
