using Ariva.Api.Ingest.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ariva.UnitTests.Sensing;

/// <summary>
/// ARV-064: a push's receipt time is when it arrived, not when Ingest finished checking the device's credential. The
/// demo showed every device Unreliable for its first ten pushes after a start: the first credential check took over
/// half a second, which F19 read as the device's clock being 500 ms behind.
/// </summary>
public sealed class ReceiptTimeTests
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task ReceiptTime_Should_BeTheArrival_When_LaterSteps_TakeTime()
    {
        var clock = new Clock(new DateTimeOffset(2026, 10, 5, 18, 5, 0, TimeSpan.Zero));
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services => services.AddSingleton<TimeProvider>(clock))
                .Configure(app =>
                {
                    app.UseReceiptTime();
                    // A slow step after arrival (a credential check), as authentication would be.
                    app.Use((context, next) =>
                    {
                        clock.Now = clock.Now.AddMilliseconds(900);
                        return next(context);
                    });
                    app.Run(context => context.Response.WriteAsync(ReceiptTime.Of(context)!.Value.ToString("O"), context.RequestAborted));
                }))
            .StartAsync(TestContext.Current.CancellationToken);
        using var client = host.GetTestClient();

        var first = await client.GetStringAsync("/push", TestContext.Current.CancellationToken);
        var second = await client.GetStringAsync("/push", TestContext.Current.CancellationToken);

        DateTime.Parse(first, null, System.Globalization.DateTimeStyles.RoundtripKind).Should().Be(new DateTime(2026, 10, 5, 18, 5, 0, DateTimeKind.Utc));
        DateTime.Parse(second, null, System.Globalization.DateTimeStyles.RoundtripKind).Should().Be(new DateTime(2026, 10, 5, 18, 5, 0, 900, DateTimeKind.Utc));
        ReceiptTime.Of(new DefaultHttpContext()).Should().BeNull("without the middleware the controller takes its own clock");
    }
}
