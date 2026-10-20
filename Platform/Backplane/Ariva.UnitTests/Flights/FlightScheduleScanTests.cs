using Ariva.Core.Services;
using Ariva.Core.Services.Storage;
using Ariva.Infra.Flights.Ssim;
using Ariva.Infra.Services.Flights;
using Ariva.Infra.Storage;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Ariva.UnitTests.Flights;

/// <summary>
/// ARV-092b: the SSIM service's contract with its one caller, the controller, which hands it the buffered form file. The
/// file is scanned, then read again from the same position, so a stream that cannot seek is a caller's error, refused at
/// the entry before anything reads it; a file over 20 MB is refused without a scan; a detection ends the call before the
/// database or the reader is reached. The scanned bytes being the parsed ones is shown against PostgreSQL
/// (Ariva.IntegrationTests UploadScanTests: a clean file previews with its legs and imports under the preview's token).
/// </summary>
public sealed class FlightScheduleScanTests
{
    #region Fields

    private readonly Mock<IMalwareScanner> _scanner = new(MockBehavior.Strict);

    // Strict: a call that reaches the database fails the test.
    private readonly Mock<IUnitOfWork> _unitOfWork = new(MockBehavior.Strict);

    #endregion

    #region Helpers

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SvcFlightSchedules Service()
    {
        var settings = Options.Create(new MalwareScanSettings { Host = "127.0.0.1" });
        var services = new ServiceCollection();
        services.AddScoped<IUnitOfWork>(_ => new Mock<IUnitOfWork>().Object);
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var caller = new Mock<ICurrentUser>().Object;
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));
        var screening = new UploadScreening(_scanner.Object, settings, UploadScanPolicy.For("k8s-dev"), caller, clock, scopes, NullLogger<UploadScreening>.Instance);
        return new SvcFlightSchedules(_unitOfWork.Object, caller, clock, scopes, new EphemeralDataProtectionProvider(), screening, NullLogger<SvcFlightSchedules>.Instance);
    }

    #endregion

    #region Tests

    [Fact]
    public async Task PreviewAndImport_Should_RefuseTheStreamBeforeReadingIt_When_ItCannotSeek()
    {
        using var preview = new OnceOnly(Eicar.Bytes());
        using var import = new OnceOnly(Eicar.Bytes());

        var previewing = () => Service().PreviewAsync("XUSA", preview, 7, Ct);
        var importing = () => Service().ImportAsync("XUSA", import, 7, "token", Ct);

        (await previewing.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("file");
        (await importing.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("file");
        preview.Reads.Should().Be(0, "nothing reads a stream the service cannot read twice");
        import.Reads.Should().Be(0);
        _scanner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PreviewAndImport_Should_RefuseWithoutAScan_When_TheFileIsOver20MB()
    {
        using var file = new Huge(SsimReader.MaxBytes + 1L);

        var preview = await Service().PreviewAsync("XUSA", file, 7, Ct);
        var import = await Service().ImportAsync("XUSA", file, 7, "token", Ct);

        preview.ErrorMessages.Should().Equal(SsimReader.TooLarge);
        import.ErrorMessages.Should().Equal(SsimReader.TooLarge);
        _scanner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PreviewAndImport_Should_StopBeforeTheDatabase_When_TheScannerFindsAThreat()
    {
        _scanner.Setup(s => s.ScanAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(MalwareScan.Infected(Eicar.Signature));

        var preview = await Service().PreviewAsync("XUSA", new MemoryStream(Eicar.Bytes()), 7, Ct);
        var import = await Service().ImportAsync("XUSA", new MemoryStream(Eicar.Bytes()), 7, "token", Ct);

        preview.ErrorMessages.Should().Equal(UploadScanErrors.Malware);
        import.ErrorMessages.Should().Equal(UploadScanErrors.Malware);
        _unitOfWork.VerifyNoOtherCalls();
    }

    [Fact]
    public void RequireSeekable_Should_TakeTheFormFilesStream_When_ItCanSeek()
    {
        var take = () => SvcFlightSchedules.RequireSeekable(new MemoryStream([1]));
        var refuseNone = () => SvcFlightSchedules.RequireSeekable(null);

        take.Should().NotThrow();
        refuseNone.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Streams

    /// <summary>A network-like stream that cannot seek and counts its reads.</summary>
    private sealed class OnceOnly(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        public int Reads { get; private set; }

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
            Reads++;
            return _inner.Read(buffer, offset, count);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>A seekable stream of the given length that holds nothing (no 20 MB array for a size test).</summary>
    private sealed class Huge(long length) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("A file over the limit is never read.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => length + offset
        };

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    #endregion
}
