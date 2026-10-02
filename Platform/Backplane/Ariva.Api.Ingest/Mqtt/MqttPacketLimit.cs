using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using Ariva.Infra.Sensing;
using Microsoft.AspNetCore.Connections;

namespace Ariva.Api.Ingest.Mqtt;

/// <summary>
/// Caps what an MQTT client can make the server hold (ARV-024, CWE-400, CWE-770). MQTTnet reads a whole packet before
/// handing it over and trusts the length in its fixed header (up to 256 MB), so without a cap one unauthenticated
/// connection could make the server hold that much. This follows the fixed headers as the bytes arrive and closes the
/// connection, before a body is buffered, when:
/// <list type="bullet">
/// <item>the first packet is not a CONNECT, or is larger than <see cref="MaxConnectBytes"/>, or asks for a will message
/// (a will would be ingested as data after any unclean disconnect);</item>
/// <item>before the broker has accepted the CONNECT, any packet is larger than <see cref="MaxConnectBytes"/>;</item>
/// <item>after it, any packet is larger than the limit given (a push and its topic);</item>
/// <item>a length is malformed (more than four bytes, MQTT 3.1.1 and 5, 2.2.3).</item>
/// </list>
/// </summary>
public sealed class MqttPacketLimit(int maxRemainingLength)
{
    /// <summary>A push body (256 KB) with room for the topic and MQTT 5 properties.</summary>
    public const int MaxPacketBytes = IngestSettings.MaxBodyBytes + 4 * 1024;

    /// <summary>The largest CONNECT, and any packet before the CONNECT is accepted: a code, a credential and a few properties.</summary>
    public const int MaxConnectBytes = 8 * 1024;

    private const byte WillFlag = 0x04;

    private enum State
    {
        Header,
        Length,
        Body
    }

    private State _state = State.Header;
    private long _length;
    private int _lengthBytes;
    private long _bodyLeft;
    private int _packets;
    private readonly byte[] _connectStart = new byte[10];
    private int _connectSeen;
    private volatile bool _accepted;

    /// <summary>The broker accepted the CONNECT: packets up to the full limit from now on.</summary>
    public void Accept() => _accepted = true;

    private long Limit => _accepted ? maxRemainingLength : MaxConnectBytes;

    /// <summary>Follows the bytes of the stream; false as soon as the client breaks a rule above.</summary>
    public bool Feed(ReadOnlySequence<byte> data)
    {
        foreach (var memory in data)
        {
            var span = memory.Span;
            var i = 0;
            while (i < span.Length)
            {
                switch (_state)
                {
                    case State.Header:
                        // The first packet of a connection is a CONNECT (type 1).
                        if (_packets == 0 && span[i] >> 4 != 1)
                            return false;
                        i++;
                        (_state, _length, _lengthBytes) = (State.Length, 0, 0);
                        break;
                    case State.Length:
                        var b = span[i++];
                        _length |= (long)(b & 0x7F) << (7 * _lengthBytes);
                        _lengthBytes++;
                        if ((b & 0x80) != 0)
                        {
                            if (_lengthBytes == 4)
                                return false;
                            continue;
                        }

                        if (_length > Limit)
                            return false;
                        if (_length == 0)
                        {
                            if (_packets++ == 0)
                                return false;
                            _state = State.Header;
                        }
                        else
                        {
                            (_state, _bodyLeft) = (State.Body, _length);
                        }

                        break;
                    default:
                        var take = (int)Math.Min(_bodyLeft, span.Length - i);
                        if (_packets == 0 && !InspectConnect(span.Slice(i, take), _bodyLeft == take))
                            return false;
                        i += take;
                        _bodyLeft -= take;
                        if (_bodyLeft == 0)
                        {
                            _packets++;
                            _state = State.Header;
                        }

                        break;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The CONNECT's variable header: protocol name ("MQTT", or "MQIsdp" for 3.1), level, then the connect flags; a
    /// will is refused. False when the flags ask for a will or the header ends before them.
    /// </summary>
    private bool InspectConnect(ReadOnlySpan<byte> bytes, bool last)
    {
        var copy = Math.Min(bytes.Length, _connectStart.Length - _connectSeen);
        bytes[..copy].CopyTo(_connectStart.AsSpan(_connectSeen));
        _connectSeen += copy;
        if (_connectSeen >= 2)
        {
            var nameLength = (_connectStart[0] << 8) | _connectStart[1];
            if (nameLength is not (4 or 6))
                return false;
            var flagsAt = 2 + nameLength + 1;
            if (_connectSeen > flagsAt)
                return (_connectStart[flagsAt] & WillFlag) == 0;
        }

        return !last;
    }
}

/// <summary>
/// The open MQTT connections of this process with their packet limits, by client end point, so the broker can raise
/// a connection's limit when it accepts its CONNECT (MQTTnet does not expose the connection itself).
/// </summary>
public sealed class MqttConnectionLimits
{
    private readonly ConcurrentDictionary<string, MqttPacketLimit> _limits = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<IPAddress, int> _perAddress = new();

    internal void Add(EndPoint endPoint, MqttPacketLimit limit) => _limits[Key(endPoint)] = limit;

    internal void Remove(EndPoint endPoint) => _limits.TryRemove(Key(endPoint), out _);

    /// <summary>The broker accepted the CONNECT from this end point.</summary>
    public void Accept(EndPoint endPoint)
    {
        if (endPoint is not null && _limits.TryGetValue(Key(endPoint), out var limit))
            limit.Accept();
    }

    /// <summary>Counts a connection from the address; false when the address already has <paramref name="max"/>.</summary>
    internal bool TryOpen(IPAddress address, int max)
    {
        while (true)
        {
            var current = _perAddress.GetOrAdd(address, 0);
            if (current >= max)
                return false;
            if (_perAddress.TryUpdate(address, current + 1, current))
                return true;
        }
    }

    internal void Close(IPAddress address)
    {
        while (_perAddress.TryGetValue(address, out var current))
        {
            if (current <= 1 ? _perAddress.TryRemove(new KeyValuePair<IPAddress, int>(address, current)) : _perAddress.TryUpdate(address, current - 1, current))
                return;
        }
    }

    /// <summary>Open connections from the address, and in all.</summary>
    public int OpenFrom(IPAddress address) => address is not null && _perAddress.TryGetValue(address, out var count) ? count : 0;

    public int Open => _limits.Count;

    private static string Key(EndPoint endPoint) => endPoint?.ToString() ?? string.Empty;
}

/// <summary>The connection middleware that applies <see cref="MqttPacketLimit"/> in front of the MQTT handler.</summary>
public static class MqttPacketLimitExtensions
{
    public static IConnectionBuilder UseMqttPacketLimit(this IConnectionBuilder builder, MqttConnectionLimits limits, int maxRemainingLength = MqttPacketLimit.MaxPacketBytes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(limits);
        return builder.Use(next => async connection =>
        {
            var original = connection.Transport;
            // One packet (and its header) at most between the check and MQTTnet; the pump waits beyond that.
            var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: maxRemainingLength + 64L, resumeWriterThreshold: maxRemainingLength / 2, useSynchronizationContext: false));
            var limit = new MqttPacketLimit(maxRemainingLength);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);
            limits.Add(connection.RemoteEndPoint, limit);
            var pump = PumpAsync(original.Input, pipe.Writer, limit, connection, stop.Token);
            connection.Transport = new Duplex(pipe.Reader, original.Output);
            try
            {
                await next(connection);
            }
            finally
            {
                limits.Remove(connection.RemoteEndPoint);
                // MQTTnet ends a session by completing the pipes only, and the pump would wait on the client forever. Stop
                // the pump and return, so the connection closes gracefully (what MQTTnet wrote, a refusing CONNACK
                // included, is still delivered; aborting here would drop it).
                connection.Transport = original;
                await stop.CancelAsync();
                original.Input.CancelPendingRead();
                await pipe.Reader.CompleteAsync();
                await pump;
            }
        });
    }

    private static async Task PumpAsync(PipeReader input, PipeWriter output, MqttPacketLimit limit, ConnectionContext connection, CancellationToken ct)
    {
        Exception error = null;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(ct);
                if (read.IsCanceled)
                    break;
                var buffer = read.Buffer;
                if (!limit.Feed(buffer))
                {
                    error = new ConnectionAbortedException("An MQTT packet breaks the size or CONNECT rules.");
                    connection.Abort((ConnectionAbortedException)error);
                    break;
                }

                foreach (var segment in buffer)
                    output.Write(segment.Span);
                input.AdvanceTo(buffer.End);
                var flushed = await output.FlushAsync(ct);
                if (read.IsCompleted || flushed.IsCompleted || flushed.IsCanceled)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // The connection closed or the session ended.
        }
        catch (Exception e) when (e is IOException or ConnectionResetException or ConnectionAbortedException or InvalidOperationException)
        {
            error = e;
        }

        await output.CompleteAsync(error);
    }

    private sealed class Duplex(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }
}
