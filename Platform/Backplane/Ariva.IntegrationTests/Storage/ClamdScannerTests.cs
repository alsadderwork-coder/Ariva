using Ariva.Core.Domain.Enums;
using Ariva.Core.Services.Storage;
using Ariva.Infra.Storage;
using Ariva.IntegrationTests.Setup;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.IntegrationTests.Storage;

/// <summary>
/// ARV-092b against the pinned ClamAV image (offline, its own signature snapshot): Ariva's INSTREAM client gets clamd's
/// verdicts, the EICAR test file found as Eicar-Test-Signature and a clean file clean; content over MaxStreamBytes gets no
/// verdict and never reaches clamd; and the largest upload Ariva takes (100 MiB, the default limit) is scanned whole under
/// Ariva's clamd settings.
/// </summary>
[Collection(ClamdCollection.Name)]
public sealed class ClamdScannerTests(ClamdFixture clamd)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClamdScanner Scanner(long maxStreamBytes = MalwareScanSettings.DefaultMaxStreamBytes) =>
        new(new MalwareScanSettings { Host = clamd.Host, Port = clamd.Port, MaxStreamBytes = maxStreamBytes }, TimeSpan.FromMinutes(2), null);

    [Fact]
    public async Task ScanAsync_Should_FindTheEicarTestFile_When_ClamdScansIt()
    {
        var scan = await Scanner().ScanAsync(new MemoryStream(Eicar.Bytes()), Ct);

        scan.Verdict.Should().Be(MalwareVerdict.Infected);
        scan.Signature.Should().Be(Eicar.Signature, "the name the image's signatures give it, through the strict pattern");
    }

    [Fact]
    public async Task ScanAsync_Should_BeClean_When_TheFileHoldsNoKnownThreat()
    {
        var svg = """<svg xmlns="http://www.w3.org/2000/svg" width="100" height="50"><rect width="10" height="10"/></svg>"""u8.ToArray();

        var scan = await Scanner().ScanAsync(new MemoryStream(svg), Ct);

        scan.Should().Be(MalwareScan.Clean);
    }

    [Fact]
    public async Task ScanAsync_Should_GiveNoVerdict_When_TheContentIsOverTheLimit()
    {
        var content = new byte[2_048];
        Eicar.Bytes().CopyTo(content, 0);

        var scan = await Scanner(maxStreamBytes: 1_024).ScanAsync(new MemoryStream(content), Ct);

        scan.Should().Be(MalwareScan.Unavailable(MalwareScanReasons.TooLarge), "never a verdict on part of a file, even one that starts with a threat");
    }

    [Fact]
    public async Task ScanAsync_Should_ScanTheLargestUploadWhole_When_ClamdRunsWithArivasSettings()
    {
        if (!clamd.ArivaSettings)
            Assert.Skip("Platform/Cloud/clamav/clamd.conf is not in this checkout.");
        var largest = new byte[MalwareScanSettings.DefaultMaxStreamBytes];
        "ariva"u8.CopyTo(largest);

        var scan = await Scanner().ScanAsync(new MemoryStream(largest), Ct);

        scan.Should().Be(MalwareScan.Clean, "clamd's StreamMaxLength and MaxFileSize leave room over Ariva's 100 MiB limit");
    }
}
