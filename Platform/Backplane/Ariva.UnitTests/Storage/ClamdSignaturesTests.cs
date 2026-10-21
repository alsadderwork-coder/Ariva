using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Ariva.Core.Services.Storage;
using Ariva.Infra.Storage;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Storage;

/// <summary>
/// ARV-092c: clamd's answer to VERSION, the source of the signatures' staleness alarm. Only
/// <c>ClamAV &lt;engine&gt;/&lt;database&gt;/&lt;asctime build time&gt;</c> exactly is taken (the pinned image's
/// <c>ClamAV 1.4.6/28157/Sun Oct 18 06:24:14 2026</c>; a one-digit day after two spaces and a two-digit day after one), as a
/// real UTC date whose day name matches; a malformed, hostile or over-long reply is null, never text to log. Against a fake
/// clamd: the scanner asks on a connection of its own, and no answer, a reset, an over-long reply, the deadline passed or a
/// refused connection is null; the caller's own cancellation is thrown.
/// </summary>
public sealed class ClamdSignaturesTests : IAsyncDisposable
{
    #region Fields

    private readonly FakeClamd _clamd = new();

    #endregion

    #region Helpers

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _clamd.DisposeAsync();

    private ClamdScanner Scanner(TimeSpan? versionTimeout = null, int? port = null, string host = null) =>
        new(new MalwareScanSettings { Host = host ?? _clamd.Host, Port = port ?? _clamd.Port }, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5),
            versionTimeout ?? TimeSpan.FromSeconds(5));

    private static DateTime Utc(int year, int month, int day, int hour, int minute, int second) => new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    #endregion

    #region Parsing

    public static TheoryData<string, string, int, DateTime> Accepted => new()
    {
        // The pinned image's own answer: daily.cvd 28157, built 18 Oct 2026 06:24 +0000 (as sigtool's info prints it), a two-digit day.
        { FakeClamd.PinnedImageVersionReply, "1.4.6", 28157, Utc(2026, 10, 18, 6, 24, 14) },
        // The image pinned before ARV-092c's review (1.4.3): a one-digit day after two spaces.
        { "ClamAV 1.4.3/27927/Sun Mar  1 07:24:39 2026", "1.4.3", 27927, Utc(2026, 3, 1, 7, 24, 39) },
        { "ClamAV 1.4.3/27512/Mon Mar  2 08:20:00 2026", "1.4.3", 27512, Utc(2026, 3, 2, 8, 20, 0) },
        // A two-digit day after one space.
        { "ClamAV 1.4.3/27940/Sat Mar 14 09:05:01 2026", "1.4.3", 27940, Utc(2026, 3, 14, 9, 5, 1) },
        { "ClamAV 1.4.3/28140/Thu Dec 31 23:59:59 2026", "1.4.3", 28140, Utc(2026, 12, 31, 23, 59, 59) },
        { "ClamAV 1.5.0-rc/1/Wed Oct  7 00:00:00 2026", "1.5.0-rc", 1, Utc(2026, 10, 7, 0, 0, 0) },
        { "ClamAV 0.103.12/27160/Thu Feb 29 12:00:00 2024", "0.103.12", 27160, Utc(2024, 2, 29, 12, 0, 0) },
        { "ClamAV 1/999999999/Fri Jan  5 10:00:00 2024", "1", 999_999_999, Utc(2024, 1, 5, 10, 0, 0) }
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Signatures_Should_TakeTheVersionAndTheBuildTimeAsUtc_When_TheReplyIsClamdsOwnFormat(string reply, string engine, int database, DateTime built)
    {
        var signatures = ClamdScanner.Signatures(reply);

        signatures.Should().Be(new MalwareSignatures(engine, database, built));
        signatures.BuiltUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    public static TheoryData<string> Refused => new()
    {
        null,
        "",
        // clamd without a database loaded, and other commands' answers.
        "ClamAV 1.4.3",
        "PONG",
        "stream: OK",
        // Framing: nothing before or after, not even a newline (the z form ends at its NUL).
        FakeClamd.PinnedImageVersionReply + "\n",
        FakeClamd.PinnedImageVersionReply + " ",
        " " + FakeClamd.PinnedImageVersionReply,
        FakeClamd.PinnedImageVersionReply + "/extra",
        FakeClamd.PinnedImageVersionReply + "\0trailing",
        "clamav 1.4.3/27927/Sun Mar  1 07:24:39 2026",
        "ClamAV  1.4.3/27927/Sun Mar  1 07:24:39 2026",
        "ClamAV 1.4.3/27927/Sun\tMar  1 07:24:39 2026",
        // The day as asctime writes it only: a one-digit day after two spaces, a two-digit day after one.
        "ClamAV 1.4.3/27927/Sun Mar 1 07:24:39 2026",
        "ClamAV 1.4.3/27927/Sun Mar 01 07:24:39 2026",
        "ClamAV 1.4.3/27927/Sat Mar  14 09:05:01 2026",
        "ClamAV 1.4.3/27927/Sun Mar  0 07:24:39 2026",
        "ClamAV 1.4.3/27927/Sun Mar 32 07:24:39 2026",
        // A real date whose day name matches.
        "ClamAV 1.4.3/27927/Mon Mar  1 07:24:39 2026",
        "ClamAV 1.4.3/27927/Mon Feb 30 07:24:39 2026",
        "ClamAV 1.4.3/27927/Sun Feb 29 07:24:39 2026",
        "ClamAV 1.4.3/27927/sun mar  1 07:24:39 2026",
        "ClamAV 1.4.3/27927/Sunday Mar  1 07:24:39 2026",
        "ClamAV 1.4.3/27927/Sun March  1 07:24:39 2026",
        "ClamAV 1.4.3/27927/Sun Mar  1 24:00:00 2026",
        "ClamAV 1.4.3/27927/Sun Mar  1 07:60:39 2026",
        "ClamAV 1.4.3/27927/Sun Mar  1 07:24:60 2026",
        "ClamAV 1.4.3/27927/Sun Mar  1 7:24:39 2026",
        "ClamAV 1.4.3/27927/Sun Mar  1 07:24:39 26",
        "ClamAV 1.4.3/27927/Sun Mar  1 07:24:39 20260",
        "ClamAV 1.4.3/27927/Sun Mar  1 07:24:39 0999",
        "ClamAV 1.4.3/27927/Sun Mar  1 07:24:39 2026 UTC",
        "ClamAV 1.4.3/27927/2026-03-01T07:24:39Z",
        // The database version: 1 to 9 digits, no leading zero, no sign.
        "ClamAV 1.4.3/0/Sun Mar  1 07:24:39 2026",
        "ClamAV 1.4.3/027927/Sun Mar  1 07:24:39 2026",
        "ClamAV 1.4.3/-27927/Sun Mar  1 07:24:39 2026",
        "ClamAV 1.4.3/+27927/Sun Mar  1 07:24:39 2026",
        "ClamAV 1.4.3/1234567890/Sun Mar  1 07:24:39 2026",
        "ClamAV 1.4.3//Sun Mar  1 07:24:39 2026",
        // Only the digits 0 to 9: fullwidth and Arabic-Indic digits are refused.
        "ClamAV 1.4.3/\uFF12\uFF17\uFF19\uFF12\uFF17/Sun Mar  1 07:24:39 2026",
        "ClamAV 1.4.3/27927/Sun Mar  1 07:24:39 \u0662\u0660\u0662\u0666",
        // Hostile engine names and forged log lines.
        "ClamAV <script>alert(1)</script>/27927/Sun Mar  1 07:24:39 2026",
        "ClamAV 1.4.3' OR '1'='1/27927/Sun Mar  1 07:24:39 2026",
        "ClamAV .4.3/27927/Sun Mar  1 07:24:39 2026",
        "ClamAV 1.4.3;rm/27927/Sun Mar  1 07:24:39 2026",
        "ClamAV 1.4.3\r\n2026-10-20T08:00:00Z [ERR] forged line/27927/Sun Mar  1 07:24:39 2026",
        FakeClamd.PinnedImageVersionReply + "\r\n2026-10-20T08:00:00Z [ERR] forged line",
        "ClamAV 1.4.3/27927/Sun Mar  1 07:24:39 2026\u2028forged",
        "ClamAV 1.4.3\u202E/27927/Sun Mar  1 07:24:39 2026",
        "ClamAV 1" + new string('0', 32) + "/27927/Sun Mar  1 07:24:39 2026",
        // Longer than a reply may be.
        "ClamAV 1.4.3/27927/Sun Mar  1 07:24:39 2026" + new string(' ', ClamdScanner.MaxReplyBytes)
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Signatures_Should_BeNull_When_TheReplyIsNotExactlyClamdsFormat(string reply) => ClamdScanner.Signatures(reply).Should().BeNull();

    [Fact]
    public void Signatures_Should_KeepAnEngineOf32Characters_When_ItMatchesThePattern()
    {
        var engine = "1" + new string('a', 31);

        ClamdScanner.Signatures($"ClamAV {engine}/27927/Sun Mar  1 07:24:39 2026").Should().Be(new MalwareSignatures(engine, 27927, Utc(2026, 3, 1, 7, 24, 39)));
    }

    [Fact]
    public void AgeAt_Should_BeTheTimeSinceTheBuild_When_Measured()
    {
        var signatures = new MalwareSignatures("1.4.3", 27927, Utc(2026, 3, 1, 7, 24, 39));

        signatures.AgeAt(new DateTimeOffset(2026, 3, 3, 9, 24, 39, TimeSpan.FromHours(2))).Should().Be(TimeSpan.FromHours(48));
    }

    #endregion

    #region The VERSION call

    [Fact]
    public async Task SignaturesAsync_Should_ReadTheSignatures_When_ClamdAnswersVersion()
    {
        _clamd.Mode = FakeClamd.Behaviour.Ok;

        var signatures = await Scanner().SignaturesAsync(Ct);

        signatures.Should().Be(new MalwareSignatures("1.4.6", 28157, Utc(2026, 10, 18, 6, 24, 14)));
        _clamd.VersionQueries.Should().Be(1, "one connection a check");
        _clamd.Connections.Should().Be(0, "a signature check is not a scan");
        _clamd.Sessions.Should().BeEmpty();
    }

    [Theory]
    [InlineData(FakeClamd.Behaviour.Silent)]
    [InlineData(FakeClamd.Behaviour.Reset)]
    [InlineData(FakeClamd.Behaviour.Overlong)]
    public async Task SignaturesAsync_Should_BeNull_When_ClamdGivesNoUsableAnswer(FakeClamd.Behaviour mode)
    {
        _clamd.Mode = mode;

        (await Scanner().SignaturesAsync(Ct)).Should().BeNull();
    }

    [Theory]
    [InlineData(FakeClamd.HostileName)]
    [InlineData("ClamAV 1.4.3/27927/Sun Mar  1 07:24:39 2026\r\n[ERR] forged")]
    [InlineData("ClamAV 1.4.3")]
    [InlineData(null)]
    public async Task SignaturesAsync_Should_BeNull_When_TheReplyIsNotClamdsFormat(string reply)
    {
        _clamd.Mode = FakeClamd.Behaviour.Ok;
        _clamd.VersionReply = reply;

        (await Scanner().SignaturesAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task SignaturesAsync_Should_BeNullWithinItsDeadline_When_ClamdDoesNotAnswerInTime()
    {
        _clamd.Mode = FakeClamd.Behaviour.Delay;
        _clamd.Delay = TimeSpan.FromSeconds(30);
        var watch = Stopwatch.StartNew();

        var signatures = await Scanner(versionTimeout: TimeSpan.FromMilliseconds(300)).SignaturesAsync(Ct);

        signatures.Should().BeNull();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the check's own deadline, not clamd's delay");
    }

    [Fact]
    public async Task SignaturesAsync_Should_BeNull_When_NothingListens()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        (await Scanner(port: port).SignaturesAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task SignaturesAsync_Should_BeNullWithoutConnecting_When_NoScannerIsConfigured()
    {
        (await Scanner(host: " ").SignaturesAsync(Ct)).Should().BeNull();

        _clamd.VersionQueries.Should().Be(0);
        _clamd.Connections.Should().Be(0);
    }

    [Fact]
    public async Task SignaturesAsync_Should_Throw_When_TheCallerCancels()
    {
        _clamd.Mode = FakeClamd.Behaviour.Delay;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        caller.CancelAfter(TimeSpan.FromMilliseconds(200));

        var check = () => Scanner().SignaturesAsync(caller.Token);

        await check.Should().ThrowAsync<OperationCanceledException>("the host is stopping; that is not an answer");
    }

    #endregion
}
