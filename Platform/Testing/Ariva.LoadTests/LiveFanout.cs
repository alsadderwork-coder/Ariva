using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;

namespace Ariva.LoadTests;

/// <summary>
/// Live fan-out (ARV-071): <see cref="LoadSettings.Screens"/> dashboards hold a WebSocket to the live hub, each joined to
/// the zone, while the displays poll their board every 10 seconds; a publisher stands in for Ariva.Api.Stream and
/// announces the zone's snapshot once a second through Redis. Measured: connect and join time, delivery latency from
/// publication to each screen, delivered share, and the boards' answers. The hub is spoken in SignalR's JSON protocol
/// over a plain WebSocket (no client package), as the web app connects: WebSockets only, the token as access_token.
/// </summary>
public sealed class LiveFanout(LoadSettings settings, IDisplayApi displays)
{
    private const char Separator = '\u001e';

    public sealed record Result(Measure Connects, Measure Deliveries, Measure Boards, int Published, int Screens, long Delivered, int Dropped);

    public async Task<Result> RunAsync(CancellationToken ct)
    {
        var connects = new Measure("screen connect and join");
        var deliveries = new Measure("snapshot delivery");
        var boards = new Measure("display board");
        long delivered = 0;
        var dropped = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        connects.Start();
        var screens = await Task.WhenAll(Enumerable.Range(0, settings.Screens).Select(i => ScreenAsync(i, connects, stop.Token)));
        connects.Stop();
        var open = screens.Where(s => s is not null).ToList();

        deliveries.Start();
        boards.Start();
        var receivers = open.Select(s => ReceiveAsync(s, deliveries, () => Interlocked.Increment(ref delivered), () => Interlocked.Increment(ref dropped), stop.Token)).ToList();
        var pollers = settings.DisplayKeys.Select((key, i) => PollAsync(settings.DisplayCodes[i], key, i, boards, stop.Token)).ToList();
        var published = await PublishAsync(stop.Token);
        // Let the last snapshot arrive, then close everything.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        await stop.CancelAsync();
        await Task.WhenAll(receivers.Concat(pollers).Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
        deliveries.Stop();
        boards.Stop();
        foreach (var s in open)
            s.Dispose();
        return new Result(connects, deliveries, boards, published, open.Count, delivered, dropped);
    }

    private async Task<ClientWebSocket> ScreenAsync(int index, Measure measure, CancellationToken ct)
    {
        // Spread the connects over two seconds, as screens coming up after a restart would.
        await Task.Delay(index * 2000 / Math.Max(1, settings.Screens), ct);
        var scheme = settings.Main.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        var uri = new UriBuilder(settings.Main) { Scheme = scheme, Path = "/hubs/live", Query = "access_token=" + Uri.EscapeDataString(settings.AccessTokens[index % settings.AccessTokens.Count]) }.Uri;
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        var clock = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(uri, ct);
            await SendAsync(socket, """{"protocol":"json","version":1}""", ct);
            var handshake = await ReadAsync(socket, ct);
            if (handshake is null || handshake.Contains("\"error\"", StringComparison.Ordinal))
                throw new HubRefusedException("handshake refused");
            await SendAsync(socket, JsonSerializer.Serialize(new { type = 1, invocationId = "1", target = "JoinZone", arguments = new[] { settings.ZoneKey } }), ct);
            // The completion of the join (type 3); a snapshot may arrive before it.
            for (var guard = 0; guard < 10; guard++)
            {
                var message = await ReadAsync(socket, ct);
                if (message is null)
                    throw new HubRefusedException("closed during the join");
                if (message.Contains("\"type\":3", StringComparison.Ordinal))
                {
                    if (message.Contains("\"error\"", StringComparison.Ordinal))
                        throw new HubRefusedException("join refused");
                    break;
                }
            }

            measure.Record(clock.Elapsed.TotalMilliseconds, "joined");
            return socket;
        }
        catch (Exception e) when (e is HubRefusedException or WebSocketException or HttpRequestException or OperationCanceledException)
        {
            // Messages of the harness's own exceptions only: a runtime message could carry the URI, and the URI carries the token.
            measure.Record(clock.Elapsed.TotalMilliseconds, "error " + (e is HubRefusedException ? e.Message : e.GetType().Name));
            socket.Dispose();
            return null;
        }
    }

    private static Task SendAsync(ClientWebSocket socket, string json, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json + Separator), WebSocketMessageType.Text, true, ct);

    /// <summary>One hub message (the protocol frames each with a record separator); null when the socket closed.</summary>
    private static async Task<string> ReadAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            var text = new StringBuilder();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (result.EndOfMessage)
                    return text.ToString().TrimEnd(Separator);
                if (text.Length > 256 * 1024)
                    throw new HubRefusedException("message too long");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ReceiveAsync(ClientWebSocket socket, Measure measure, Action delivered, Action dropped, CancellationToken ct)
    {
        try
        {
            var lastPing = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                var text = await ReadAsync(socket, ct);
                if (text is null)
                {
                    dropped();
                    return;
                }

                foreach (var frame in text.Split(Separator, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!frame.Contains("\"target\":\"zone\"", StringComparison.Ordinal))
                        continue;
                    using var json = JsonDocument.Parse(frame);
                    var snapshot = json.RootElement.GetProperty("arguments")[0];
                    if (snapshot.TryGetProperty("publishedUtc", out var published) && published.TryGetDateTime(out var at))
                    {
                        measure.Record((DateTime.UtcNow - at.ToUniversalTime()).TotalMilliseconds, "delivered");
                        delivered();
                    }
                }

                // The client side of the keep-alive: the hub drops a client silent for 30 seconds.
                if (lastPing.Elapsed > TimeSpan.FromSeconds(10))
                {
                    await SendAsync(socket, """{"type":6}""", ct);
                    lastPing.Restart();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is WebSocketException or HubRefusedException)
        {
            dropped();
        }
    }

    private async Task PollAsync(string code, string key, int index, Measure measure, CancellationToken ct)
    {
        await Task.Delay(index * settings.DisplayPollSeconds * 1000 / Math.Max(1, settings.DisplayKeys.Count), ct).ContinueWith(_ => { }, TaskScheduler.Default);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.DisplayPollSeconds));
        do
        {
            if (ct.IsCancellationRequested)
                return;
            var clock = Stopwatch.StartNew();
            try
            {
                using var response = await displays.BoardAsync(code, key, ct);
                var bytes = (await response.Content.ReadAsByteArrayAsync(ct)).LongLength;
                measure.Record(clock.Elapsed.TotalMilliseconds, response.StatusCode, bytes);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is HttpRequestException or Refit.ApiException)
            {
                measure.Record(clock.Elapsed.TotalMilliseconds, "error " + e.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(ct).AsTask().ContinueWith(t => t.IsCompletedSuccessfully && t.Result, TaskScheduler.Default));
    }

    /// <summary>What Ariva.Api.Stream does after each minute's checkpoint, once a second here: keep the snapshot and announce it.</summary>
    private async Task<int> PublishAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(settings.RedisUrl))
        {
            await Task.Delay(TimeSpan.FromSeconds(settings.FanoutSeconds), ct);
            return 0;
        }

        var redisUri = new Uri(settings.RedisUrl);
        var options = new ConfigurationOptions { AbortOnConnectFail = false, Password = Uri.UnescapeDataString(redisUri.UserInfo.Split(':').Last()) };
        options.EndPoints.Add(redisUri.Host, redisUri.Port);
        using var redis = await ConnectionMultiplexer.ConnectAsync(options);
        var database = redis.GetDatabase();
        var channel = RedisChannel.Literal(settings.RedisInstance + "live:zones");
        var published = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        for (var i = 0; i < settings.FanoutSeconds && await timer.WaitForNextTickAsync(ct); i++)
        {
            var snapshot = JsonSerializer.Serialize(new
            {
                zoneKey = settings.ZoneKey,
                minuteUtc = DateTime.UtcNow.AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:00Z", CultureInfo.InvariantCulture),
                queueLength = 40 + (i % 20),
                lengthFromSensors = true,
                lengthDegraded = false,
                nowcastMinutes = 9.5 + (i % 7),
                throughputPerMinute = 5,
                noService = (string)null,
                nowcastDegraded = false,
                publishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            });
            await database.StringSetAsync(settings.RedisInstance + "live:zone:" + settings.ZoneKey, snapshot, TimeSpan.FromHours(1));
            await redis.GetSubscriber().PublishAsync(channel, snapshot);
            published++;
        }

        return published;
    }

    public static bool Ok(HttpStatusCode status) => status == HttpStatusCode.OK;
}

/// <summary>What the harness itself concluded about a hub conversation; its message never holds the URI or the token.</summary>
public sealed class HubRefusedException(string message) : Exception(message);
