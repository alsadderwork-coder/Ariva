using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Ariva.UnitTests.Setup;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace Ariva.IntegrationTests.Setup;

/// <summary>
/// A real clamd for the malware scan's tests (ARV-092b): the pinned ClamAV image, offline (<c>CLAMAV_NO_FRESHCLAMD=true</c>:
/// the image's own signature snapshot, no mirror traffic, the same verdicts in every run) and, when the repository has it,
/// with Ariva's clamd settings (<c>Platform/Cloud/clamav/clamd.conf</c>, as docker-compose and the AppHost mount it). clamd
/// loads its signatures in about 20 seconds and holds about 1 GiB; the fixture waits until a PING on 3310 answers PONG.
/// </summary>
public sealed class ClamdFixture : IAsyncLifetime
{
    #region Fields

    /// <summary>The ClamAV image by tag and digest (ARV-096; scripts/base-images.mjs holds one digest per image everywhere).</summary>
    public const string Image = "clamav/clamav:1.4.3@sha256:75fb5fd95fcbe1d7e6d240c369c1572b686ee2c95949d1042b5148de8eddebb4";

    private const int ClamdPort = 3310;

    private static readonly TimeSpan ReadyWithin = TimeSpan.FromMinutes(3);

    private IContainer _container;

    #endregion

    #region Properties

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(ClamdPort);

    /// <summary>Whether Ariva's own clamd settings were mounted over the image's.</summary>
    public bool ArivaSettings { get; private set; }

    #endregion

    #region Lifetime

    public async ValueTask InitializeAsync()
    {
        var builder = new ContainerBuilder(Image)
            .WithEnvironment("CLAMAV_NO_FRESHCLAMD", "true")
            .WithPortBinding(ClamdPort, true);
        var settings = RepositoryPaths.Resolve("Platform/Cloud/clamav/clamd.conf");
        if (File.Exists(settings))
        {
            builder = builder.WithBindMount(settings, "/etc/clamav/clamd.conf", AccessMode.ReadOnly);
            ArivaSettings = true;
        }

        _container = builder.Build();
        await _container.StartAsync();
        await WaitForPongAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private async Task WaitForPongAsync()
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(Host, Port, attempt.Token);
                var stream = client.GetStream();
                await stream.WriteAsync("zPING\0"u8.ToArray(), attempt.Token);
                var reply = new byte[16];
                var read = await stream.ReadAsync(reply, attempt.Token);
                if (Encoding.ASCII.GetString(reply, 0, read).TrimEnd('\0', '\n') == "PONG")
                    return;
            }
            catch (Exception e) when (e is SocketException or IOException or OperationCanceledException)
            {
                // Not listening yet: clamd opens its port once the signatures are loaded.
            }

            if (watch.Elapsed > ReadyWithin)
                throw new TimeoutException($"clamd did not answer PING within {ReadyWithin.TotalMinutes} minutes.");
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    #endregion
}

[CollectionDefinition(Name)]
public sealed class ClamdCollection : ICollectionFixture<ClamdFixture>
{
    public const string Name = "clamd";
}
