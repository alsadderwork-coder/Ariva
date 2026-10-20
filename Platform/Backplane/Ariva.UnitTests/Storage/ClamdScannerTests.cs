using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Services.Storage;
using Ariva.Infra.Storage;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Storage;

/// <summary>
/// ARV-092b: the clamd INSTREAM client against a fake clamd on loopback. Only "stream: OK" is clean and only "stream: name
/// FOUND" a detection, its name kept only when it matches the strict pattern; an error line, clamd's size limit, an
/// over-long reply, no reply, a reset or refused connection and a timeout are no verdict, each with its fixed reason. The
/// content arrives whole, in chunks of at most 64 KB; content beyond MaxStreamBytes is never sent past the limit and gets no
/// verdict; the caller's own cancellation is thrown.
/// </summary>
public sealed class ClamdScannerTests : IAsyncDisposable
{
    #region Fields

    private readonly FakeClamd _clamd = new();

    #endregion

    #region Helpers

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _clamd.DisposeAsync();

    private ClamdScanner Scanner(long maxStreamBytes = MalwareScanSettings.DefaultMaxStreamBytes, TimeSpan? timeout = null, string host = null, int? port = null) =>
        new(new MalwareScanSettings { Host = host ?? _clamd.Host, Port = port ?? _clamd.Port, MaxStreamBytes = maxStreamBytes },
            timeout ?? TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));

    /// <summary>Content whose bytes all differ from their neighbours' (a pattern of period 251), so a misplaced chunk shows.</summary>
    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)((i * 31 + 7) % 251);
        return bytes;
    }

    /// <summary>A port nothing listens on: bound, then released.</summary>
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    #endregion

    #region Verdicts

    [Fact]
    public async Task ScanAsync_Should_BeClean_When_ClamdAnswersOk()
    {
        _clamd.Mode = FakeClamd.Behaviour.Ok;

        var scan = await Scanner().ScanAsync(new MemoryStream("a plain text file"u8.ToArray()), Ct);

        scan.Should().Be(MalwareScan.Clean);
    }

    [Fact]
    public async Task ScanAsync_Should_FindTheTestFile_When_ClamdKnowsItsSignature()
    {
        _clamd.Mode = FakeClamd.Behaviour.Detect;

        var infected = await Scanner().ScanAsync(new MemoryStream(Eicar.Bytes()), Ct);
        var clean = await Scanner().ScanAsync(new MemoryStream("<svg/>"u8.ToArray()), Ct);

        infected.Verdict.Should().Be(MalwareVerdict.Infected);
        infected.Signature.Should().Be(Eicar.Signature);
        clean.Verdict.Should().Be(MalwareVerdict.Clean);
    }

    [Theory]
    [InlineData("Win.Test.EICAR_HDB-1")]
    [InlineData("Heuristics.Limits.Exceeded.MaxFileSize")]
    [InlineData("Doc.Dropper.Agent-1:0/x")]
    public async Task ScanAsync_Should_KeepTheSignatureName_When_ItMatchesThePattern(string name)
    {
        _clamd.Mode = FakeClamd.Behaviour.Found;
        _clamd.FoundName = name;

        var scan = await Scanner().ScanAsync(new MemoryStream([1, 2, 3]), Ct);

        scan.Should().Be(MalwareScan.Infected(name));
        scan.Signature.Should().Be(name);
    }

    [Theory]
    [InlineData("name with spaces")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("Eicar' OR '1'='1")]
    [InlineData("Eicar;rm")]
    [InlineData("Eicar(44d88612fea8a8f36de82e1278abb02f:68)")]
    public async Task ScanAsync_Should_CallTheSignatureUnrecognised_When_ItsNameIsOutsideThePattern(string name)
    {
        _clamd.Mode = FakeClamd.Behaviour.Found;
        _clamd.FoundName = name;

        var scan = await Scanner().ScanAsync(new MemoryStream([1, 2, 3]), Ct);

        scan.Verdict.Should().Be(MalwareVerdict.Infected, "clamd found something; only its name is not trusted");
        scan.Signature.Should().Be(MalwareScan.UnrecognisedSignature);
    }

    [Fact]
    public async Task ScanAsync_Should_NeverKeepAHostileName_When_ItForgesALogLine()
    {
        _clamd.Mode = FakeClamd.Behaviour.Hostile;

        var scan = await Scanner().ScanAsync(new MemoryStream([1, 2, 3]), Ct);

        scan.Should().Be(MalwareScan.Infected(MalwareScan.UnrecognisedSignature));
        (scan.Signature + scan.Reason).Should().NotContain("forged").And.NotContain("\n");
    }

    [Fact]
    public async Task ScanAsync_Should_NameATooLongSignatureUnrecognised_When_ItExceeds128Characters()
    {
        _clamd.Mode = FakeClamd.Behaviour.Found;
        _clamd.FoundName = new string('A', 129);

        (await Scanner().ScanAsync(new MemoryStream([1]), Ct)).Signature.Should().Be(MalwareScan.UnrecognisedSignature);

        _clamd.FoundName = new string('A', 128);
        (await Scanner().ScanAsync(new MemoryStream([1]), Ct)).Signature.Should().Be(new string('A', 128));
    }

    public static TheoryData<FakeClamd.Behaviour, string> NoVerdictCases => new()
    {
        { FakeClamd.Behaviour.Error, MalwareScanReasons.ScannerError },
        { FakeClamd.Behaviour.Overlong, MalwareScanReasons.ReplyTooLong },
        { FakeClamd.Behaviour.Silent, MalwareScanReasons.NoReply },
        { FakeClamd.Behaviour.Reset, MalwareScanReasons.ConnectionLost }
    };

    [Theory]
    [MemberData(nameof(NoVerdictCases))]
    public async Task ScanAsync_Should_GiveNoVerdictWithAFixedReason_When_ClamdDoesNotAnswerAVerdict(FakeClamd.Behaviour mode, string reason)
    {
        _clamd.Mode = mode;

        var scan = await Scanner().ScanAsync(new MemoryStream(Pattern(70_000)), Ct);

        scan.Should().Be(MalwareScan.Unavailable(reason));
        scan.Signature.Should().BeNull();
    }

    [Fact]
    public async Task ScanAsync_Should_GiveNoVerdict_When_ClamdRefusesTheStreamAsOverItsOwnLimit()
    {
        _clamd.Mode = FakeClamd.Behaviour.Ok;
        _clamd.StreamMaxLength = 100_000;

        var scan = await Scanner().ScanAsync(new MemoryStream(Pattern(100_001)), Ct);

        scan.Should().Be(MalwareScan.Unavailable(MalwareScanReasons.SizeLimitExceeded));
    }

    [Fact]
    public async Task ScanAsync_Should_GiveNoVerdict_When_NothingListens()
    {
        var scan = await Scanner(port: ClosedPort()).ScanAsync(new MemoryStream([1, 2, 3]), Ct);

        scan.Should().Be(MalwareScan.Unavailable(MalwareScanReasons.ConnectFailed));
    }

    [Fact]
    public async Task ScanAsync_Should_GiveNoVerdict_When_TheHostDoesNotResolve()
    {
        var scan = await Scanner(host: "clamd.invalid").ScanAsync(new MemoryStream([1, 2, 3]), Ct);

        // A resolver that answers refuses the name at once; one that does not answer runs into the connect timeout.
        scan.Verdict.Should().Be(MalwareVerdict.Unavailable);
        scan.Reason.Should().BeOneOf(MalwareScanReasons.ConnectFailed, MalwareScanReasons.ConnectTimedOut);
    }

    [Fact]
    public async Task ScanAsync_Should_GiveNoVerdictWithoutConnecting_When_NoScannerIsConfigured()
    {
        var scanner = new ClamdScanner(new MalwareScanSettings { Host = " " }, null, null);

        var scan = await scanner.ScanAsync(new MemoryStream([1, 2, 3]), Ct);

        scan.Should().Be(MalwareScan.Unavailable(MalwareScanReasons.NotConfigured));
        _clamd.Connections.Should().Be(0);
    }

    [Fact]
    public async Task ScanAsync_Should_StopWithNoVerdict_When_TheVerdictTakesLongerThanTheTimeout()
    {
        _clamd.Mode = FakeClamd.Behaviour.Delay;
        _clamd.Delay = TimeSpan.FromSeconds(30);
        var watch = Stopwatch.StartNew();

        var scan = await Scanner(timeout: TimeSpan.FromMilliseconds(500)).ScanAsync(new MemoryStream([1, 2, 3]), Ct);

        scan.Should().Be(MalwareScan.Unavailable(MalwareScanReasons.TimedOut));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the scan's own timeout, not clamd's delay");
    }

    [Fact]
    public async Task ScanAsync_Should_Throw_When_TheCallerCancels()
    {
        _clamd.Mode = FakeClamd.Behaviour.Delay;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        caller.CancelAfter(TimeSpan.FromMilliseconds(300));

        var scan = () => Scanner().ScanAsync(new MemoryStream([1, 2, 3]), caller.Token);

        await scan.Should().ThrowAsync<OperationCanceledException>("the request went away; that is not a verdict");
    }

    #endregion

    #region Framing and limits

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScanAsync_Should_SendTheWholeContentInChunksOfAtMost64KB_When_ItIsScanned(bool seekable)
    {
        _clamd.Mode = FakeClamd.Behaviour.Ok;
        var content = Pattern(200_000);

        var scan = await Scanner().ScanAsync(seekable ? new MemoryStream(content) : new TrickleStream(content), Ct);

        scan.Should().Be(MalwareScan.Clean);
        var session = (await _clamd.WaitForSessionsAsync(1, Ct)).Single();
        session.Command.Should().Be("zINSTREAM");
        session.Ended.Should().BeTrue("the zero-length chunk ends the stream");
        session.Chunks.Should().Equal(65_536, 65_536, 65_536, 3_392);
        session.Content.Should().Equal(content, "the reassembled chunks are the content");
    }

    [Fact]
    public async Task ScanAsync_Should_ScanFromTheCurrentPosition_When_TheStreamWasPartlyRead()
    {
        _clamd.Mode = FakeClamd.Behaviour.Ok;
        var content = new MemoryStream(Pattern(1_000));
        content.Position = 400;

        await Scanner().ScanAsync(content, Ct);

        (await _clamd.WaitForSessionsAsync(1, Ct)).Single().Content.Length.Should().Be(600);
    }

    [Fact]
    public async Task ScanAsync_Should_ScanContentOfExactlyTheLimit_When_ItFits()
    {
        _clamd.Mode = FakeClamd.Behaviour.Ok;

        var seekable = await Scanner(maxStreamBytes: 1_000).ScanAsync(new MemoryStream(Pattern(1_000)), Ct);
        var streamed = await Scanner(maxStreamBytes: 1_000).ScanAsync(new TrickleStream(Pattern(1_000)), Ct);

        seekable.Should().Be(MalwareScan.Clean);
        streamed.Should().Be(MalwareScan.Clean);
    }

    [Fact]
    public async Task ScanAsync_Should_RefuseWithoutConnecting_When_AStreamOfKnownLengthIsOverTheLimit()
    {
        _clamd.Mode = FakeClamd.Behaviour.Ok;

        var scan = await Scanner(maxStreamBytes: 100).ScanAsync(new MemoryStream(Pattern(101)), Ct);

        scan.Should().Be(MalwareScan.Unavailable(MalwareScanReasons.TooLarge));
        _clamd.Connections.Should().Be(0, "nothing of a file over the limit is sent");
    }

    [Fact]
    public async Task ScanAsync_Should_NeverSendPastTheLimitOrTakeAVerdict_When_AStreamGrowsOverIt()
    {
        _clamd.Mode = FakeClamd.Behaviour.Ok;

        var scan = await Scanner(maxStreamBytes: 150_000).ScanAsync(new TrickleStream(Pattern(200_000)), Ct);

        scan.Should().Be(MalwareScan.Unavailable(MalwareScanReasons.TooLarge), "never a verdict on part of a file");
        var session = (await _clamd.WaitForSessionsAsync(1, Ct)).Single();
        session.Ended.Should().BeFalse("the connection is dropped without the end mark, so clamd never scans part of the file");
        session.Content.Length.Should().BeLessThanOrEqualTo(150_000, "no byte past the limit is sent");
    }

    #endregion

    #region Reply parsing

    public static TheoryData<string, MalwareVerdict, string, string> Replies => new()
    {
        { "stream: OK", MalwareVerdict.Clean, null, null },
        { "stream: OK\n", MalwareVerdict.Clean, null, null },
        { "stream: Eicar-Test-Signature FOUND", MalwareVerdict.Infected, "Eicar-Test-Signature", null },
        { "stream: Eicar-Test-Signature FOUND\n", MalwareVerdict.Infected, "Eicar-Test-Signature", null },
        { "stream:  FOUND", MalwareVerdict.Unavailable, null, MalwareScanReasons.ReplyNotUnderstood },
        { "INSTREAM size limit exceeded. ERROR", MalwareVerdict.Unavailable, null, MalwareScanReasons.SizeLimitExceeded },
        { "stream: Can't allocate memory ERROR", MalwareVerdict.Unavailable, null, MalwareScanReasons.ScannerError },
        { "stream: OK ", MalwareVerdict.Unavailable, null, MalwareScanReasons.ReplyNotUnderstood },
        { "STREAM: OK", MalwareVerdict.Unavailable, null, MalwareScanReasons.ReplyNotUnderstood },
        { "PONG", MalwareVerdict.Unavailable, null, MalwareScanReasons.ReplyNotUnderstood },
        { "OK", MalwareVerdict.Unavailable, null, MalwareScanReasons.ReplyNotUnderstood },
        { "", MalwareVerdict.Unavailable, null, MalwareScanReasons.NoReply }
    };

    [Theory]
    [MemberData(nameof(Replies))]
    public void Verdict_Should_TakeOnlyWellFormedAnswersAsVerdicts_When_AReplyIsRead(string reply, MalwareVerdict verdict, string signature, string reason)
    {
        var scan = ClamdScanner.Verdict(reply);

        scan.Verdict.Should().Be(verdict);
        scan.Signature.Should().Be(signature);
        scan.Reason.Should().Be(reason);
    }

    #endregion

    /// <summary>A stream that cannot seek and gives at most 1,000 bytes a read, as a network body does.</summary>
    private sealed class TrickleStream(byte[] content) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(Math.Min(count, 1_000), content.Length - _position);
            Array.Copy(content, _position, buffer, offset, read);
            _position += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
