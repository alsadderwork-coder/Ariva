using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ariva.Simulation.Api.Scenarios;
using Ariva.Simulation.Api.Scenarios.Engine;
using Microsoft.Extensions.Options;

namespace Ariva.Simulation.Api.Emulators.Sensors;

/// <summary>One emulated device as the status shows it (never its credential).</summary>
public sealed record EmulatedDeviceStatus(
    string Sensor,
    string Zone,
    string QueueZone,
    EmulatedDialect Dialect,
    SensorRole Role,
    long Pushes,
    long ExpectedEvents,
    long AcceptedEvents,
    long Failures,
    int? LastStatus,
    string LastError);

/// <summary>The emulator: running or paused, the demo clock and speed, where it stops and each device's counters.</summary>
public sealed record SensorEmulatorStatus(
    bool Running,
    double Minute,
    string Clock,
    double Speed,
    int NextMinute,
    int? UntilMinute,
    bool IngestConfigured,
    IReadOnlyList<EmulatedDeviceStatus> Devices);

/// <summary>
/// The sensor emulator (ARV-028): plays the simulated day as device traffic to Ingest, authenticated as registered
/// devices. A demo clock runs at a chosen speed (demo minutes per wall minute); each time a demo minute completes,
/// every configured device pushes that minute (<see cref="SensorTraffic"/>), minute by minute and in order. Controls:
/// start (optionally at a minute, with a speed and a stop minute), pause, speed, jump and, through the scenario, the
/// seed. Sensors offline in the scenario (S-17 from 18:20 to 18:30) send nothing.
/// </summary>
public sealed class SensorEmulator : BackgroundService
{
    public const string HttpClientName = "ariva-ingest";

    /// <summary>Most demo minutes played in one tick, so that a late tick catches up without a flood.</summary>
    private const int MaxMinutesPerTick = 60;

    private readonly Lock _gate = new();
    private readonly ScenarioEngine _engine;
    private readonly IHttpClientFactory _http;
    private readonly IOptionsMonitor<SensorEmulatorSettings> _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<SensorEmulator> _logger;
    private readonly SemaphoreSlim _tick = new(1, 1);

    private Device[] _devices;
    private bool _running;
    private DateTimeOffset _wallAnchor;
    private double _demoAnchor;
    private double _speed = 1;
    private int _nextMinute;
    private int? _untilMinute;

    public SensorEmulator(ScenarioEngine engine, IHttpClientFactory http, IOptionsMonitor<SensorEmulatorSettings> settings, TimeProvider time,
        ILogger<SensorEmulator> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _engine = engine;
        _http = http;
        _settings = settings;
        _time = time;
        _logger = logger;
        _devices = ToDevices(settings.CurrentValue.Devices);
        _wallAnchor = time.GetUtcNow();
    }

    private sealed class Device(EmulatedDeviceSettings settings, SensorDef sensor)
    {
        public SensorDef Sensor { get; } = sensor;
        public EmulatedDialect Dialect { get; } = settings.Dialect;
        public string Credential { get; } = settings.Credential;
        public long PackageId;
        public long Pushes;
        public long Expected;
        public long Accepted;
        public long Failures;
        public int? LastStatus;
        public string LastError;
    }

    private static Device[] ToDevices(IReadOnlyList<EmulatedDeviceSettings> devices) =>
        [.. (devices ?? []).Select(d => new Device(d, SensorTraffic.Sensor(d.Sensor)))];

    #region Controls

    /// <summary>The demo minute now (fractional), from the anchor and the speed.</summary>
    private double DemoNow(DateTimeOffset now) =>
        _running ? _demoAnchor + (now - _wallAnchor).TotalMinutes * _speed : _demoAnchor;

    private void Anchor(DateTimeOffset now, double minute)
    {
        _demoAnchor = minute;
        _wallAnchor = now;
    }

    public SensorEmulatorStatus Status()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var minute = DemoNow(now);
            return new SensorEmulatorStatus(_running, Math.Round(minute, 3), ScenarioMath.Clock(Math.Floor(minute)), _speed, _nextMinute, _untilMinute,
                !string.IsNullOrEmpty(_settings.CurrentValue.IngestUrl),
                [.. _devices.Select(d => new EmulatedDeviceStatus(d.Sensor.Id, d.Sensor.Zone, SensorTraffic.QueueZoneOf(d.Sensor.Zone), d.Dialect,
                    SensorTraffic.RoleOf(d.Sensor), Interlocked.Read(ref d.Pushes), Interlocked.Read(ref d.Expected), Interlocked.Read(ref d.Accepted),
                    Interlocked.Read(ref d.Failures), d.LastStatus, d.LastError))]);
        }
    }

    /// <summary>Starts (or resumes) the demo clock; a minute jumps there first and resets the counters; a stop minute pauses there.</summary>
    public SensorEmulatorStatus Start(int? minute, double? speed, int? untilMinute, string by)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            // Re-anchor at the current speed before a new one applies, so the elapsed time is not rescaled.
            Anchor(now, DemoNow(now));
            if (speed is { } s)
                _speed = s;
            if (minute is { } m)
            {
                Anchor(now, m);
                _nextMinute = m;
                foreach (var d in _devices)
                    Reset(d);
            }

            _untilMinute = untilMinute;
            _running = true;
            _logger.LogInformation("Sensor emulator started by {Operator} at demo minute {Minute}, speed {Speed}, until {Until}", by, _nextMinute, _speed, untilMinute);
        }

        return Status();
    }

    public SensorEmulatorStatus Pause(string by)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Anchor(now, DemoNow(now));
            _running = false;
            _logger.LogInformation("Sensor emulator paused by {Operator} at demo minute {Minute}", by, _nextMinute);
        }

        return Status();
    }

    public SensorEmulatorStatus SetSpeed(double speed, string by)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Anchor(now, DemoNow(now));
            _speed = speed;
            _logger.LogInformation("Sensor emulator speed set to {Speed} by {Operator}", speed, by);
        }

        return Status();
    }

    /// <summary>Jumps to a demo minute; the minutes in between are not played.</summary>
    public SensorEmulatorStatus Jump(int minute, string by)
    {
        lock (_gate)
        {
            Anchor(_time.GetUtcNow(), minute);
            _nextMinute = minute;
            if (_untilMinute is { } until && until <= minute)
                _untilMinute = null;
            _logger.LogInformation("Sensor emulator jumped to demo minute {Minute} by {Operator}", minute, by);
        }

        return Status();
    }

    /// <summary>Replaces the emulated devices (validated by the caller); counters start again.</summary>
    public SensorEmulatorStatus SetDevices(IReadOnlyList<EmulatedDeviceSettings> devices, string by)
    {
        lock (_gate)
        {
            _devices = ToDevices(devices);
            _logger.LogInformation("Sensor emulator devices replaced by {Operator}: {Count} devices", by, _devices.Length);
        }

        return Status();
    }

    private static void Reset(Device d)
    {
        Interlocked.Exchange(ref d.Pushes, 0);
        Interlocked.Exchange(ref d.Expected, 0);
        Interlocked.Exchange(ref d.Accepted, 0);
        Interlocked.Exchange(ref d.Failures, 0);
        d.LastStatus = null;
        d.LastError = null;
    }

    #endregion

    #region Playing

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Sensor emulator tick failed");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(_settings.CurrentValue.TickMilliseconds), _time, stoppingToken)
                .ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>Plays every demo minute completed since the last tick (at most 60), in order.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        if (!await _tick.WaitAsync(0, ct))
            return; // the previous tick is still sending
        try
        {
            for (var played = 0; played < MaxMinutesPerTick; played++)
            {
                int minute;
                Device[] devices;
                DateTimeOffset wallAnchor;
                double demoAnchor, speed;
                lock (_gate)
                {
                    var now = _time.GetUtcNow();
                    if (!_running)
                        return;
                    if (_nextMinute >= ScenarioModel.Day || (_untilMinute is { } until && _nextMinute >= until))
                    {
                        Anchor(now, _nextMinute);
                        _running = false;
                        _logger.LogInformation("Sensor emulator paused at demo minute {Minute}: end of the run", _nextMinute);
                        return;
                    }

                    if (DemoNow(now) < _nextMinute + 1)
                        return;
                    minute = _nextMinute;
                    devices = _devices;
                    wallAnchor = _wallAnchor;
                    demoAnchor = _demoAnchor;
                    speed = _speed;
                }

                DateTime WallOf(double demoMinute) => (wallAnchor + TimeSpan.FromMinutes((demoMinute - demoAnchor) / speed)).UtcDateTime;
                await PlayMinuteAsync(minute, devices, WallOf, ct);

                lock (_gate)
                {
                    // A jump while the minute was sending wins; otherwise move on.
                    if (_nextMinute == minute)
                        _nextMinute = minute + 1;
                }
            }
        }
        finally
        {
            _tick.Release();
        }
    }

    private async Task PlayMinuteAsync(int minute, Device[] devices, Func<double, DateTime> wallOf, CancellationToken ct)
    {
        var settings = _settings.CurrentValue;
        if (string.IsNullOrEmpty(settings.IngestUrl) || devices.Length == 0)
            return;
        var day = _engine.CurrentDay;
        var sent = _time.GetUtcNow().UtcDateTime;
        var client = _http.CreateClient(HttpClientName);
        await Parallel.ForEachAsync(devices, new ParallelOptions { MaxDegreeOfParallelism = settings.Concurrency, CancellationToken = ct }, async (device, token) =>
        {
            var push = SensorTraffic.Build(day, device.Sensor, device.Dialect, minute, Interlocked.Increment(ref device.PackageId), wallOf, sent);
            if (push is null)
                return;
            await SendAsync(client, device, push, token);
        });
    }

    private async Task SendAsync(HttpClient client, Device device, SensorPush push, CancellationToken ct)
    {
        Interlocked.Increment(ref device.Pushes);
        Interlocked.Add(ref device.Expected, push.ExpectedAccepted);
        using var request = new HttpRequestMessage(HttpMethod.Post, push.Path) { Content = new StringContent(push.Json, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", device.Credential);
        try
        {
            using var response = await client.SendAsync(request, ct);
            device.LastStatus = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
                if (body.RootElement.TryGetProperty("accepted", out var accepted) && accepted.TryGetInt32(out var count))
                    Interlocked.Add(ref device.Accepted, count);
                device.LastError = null;
                return;
            }

            Interlocked.Increment(ref device.Failures);
            device.LastError = $"{(int)response.StatusCode} {response.ReasonPhrase}";
            _logger.LogWarning("Sensor emulator push for {Sensor} answered {Status}", device.Sensor.Id, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref device.Failures);
            device.LastStatus = null;
            device.LastError = e is TaskCanceledException ? "timeout" : "Ingest unreachable";
            _logger.LogWarning("Sensor emulator push for {Sensor} failed: {Reason}", device.Sensor.Id, device.LastError);
        }
    }

    #endregion

    public override void Dispose()
    {
        _tick.Dispose();
        base.Dispose();
    }
}
