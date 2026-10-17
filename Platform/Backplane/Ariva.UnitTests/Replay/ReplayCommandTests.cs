using Ariva.Di;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Ariva.UnitTests.Replay;

/// <summary>
/// ARV-036: the replay command's arguments. They never reach the host's configuration; a request is a valid site, UTC
/// times ending in Z, a range of at most 31 days, at most 64 printable zone names and a whole profile version.
/// </summary>
public sealed class ReplayCommandTests
{
    private static readonly string[] Good =
    [
        "--replay", "--replay-site=DMO", "--replay-zone=A-VIS", "--replay-zone=CI-C", "--replay-from=2026-09-28T17:00:00Z",
        "--replay-to=2026-09-28T20:30:00Z", "--replay-profile-version=12", "--replay-output=/tmp/x.jsonl"
    ];

    [Fact]
    public void Command_Should_BeRequestedAndRemoved_When_ItsFlagsArePresent()
    {
        ReplayCommand.IsRequested(Good).Should().BeTrue();
        ReplayCommand.IsRequested(["--verify-replay=/tmp/x.jsonl"]).Should().BeTrue();
        ReplayCommand.IsRequested(["--environment=vm-local"]).Should().BeFalse();
        ReplayCommand.WithoutFlags([.. Good, "--verify-replay=/tmp/x", "--environment=vm-local"]).Should().Equal("--environment=vm-local");
    }

    [Fact]
    public void Parse_Should_GiveTheRequest_When_TheArgumentsAreValid()
    {
        var (request, output, problems) = ReplayCommand.Parse(Good, "ops@host");

        problems.Should().BeEmpty();
        request.SiteCode.Should().Be("DMO");
        request.Zones.Should().Equal("A-VIS", "CI-C");
        request.FromUtc.Should().Be(new DateTime(2026, 9, 28, 17, 0, 0, DateTimeKind.Utc));
        request.FromUtc.Kind.Should().Be(DateTimeKind.Utc);
        request.ToUtc.Should().Be(new DateTime(2026, 9, 28, 20, 30, 0, DateTimeKind.Utc));
        request.ProfileVersion.Should().Be(12);
        request.RequestedBy.Should().Be("ops@host");
        output.Should().Be("/tmp/x.jsonl");
    }

    [Theory]
    [InlineData("--replay-site=dmo")]
    [InlineData("--replay-from=2026-09-28T17:00:00")]
    [InlineData("--replay-from=2026-09-28T17:00:00+04:00")]
    [InlineData("--replay-from=yesterday")]
    [InlineData("--replay-to=2026-09-28T16:00:00Z")]
    [InlineData("--replay-to=2026-11-28T16:00:00Z")]
    [InlineData("--replay-profile-version=-1")]
    [InlineData("--replay-profile-version=0")]
    [InlineData("--replay-zone=A\u0001B")]
    public void Parse_Should_RefuseTheRequest_When_AnArgumentIsNotValid(string bad)
    {
        var name = bad[..(bad.IndexOf('=', StringComparison.Ordinal) + 1)];
        var args = Good.Where(a => !a.StartsWith(name, StringComparison.Ordinal) || name == "--replay-zone=").Append(bad).ToArray();

        ReplayCommand.Parse(args, "ops").Problems.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_Should_RefuseTheRequest_When_ItNamesTooManyZones()
    {
        var args = Good.Concat(Enumerable.Range(0, 64).Select(i => $"--replay-zone=Z{i}")).ToArray();

        ReplayCommand.Parse(args, "ops").Problems.Should().Contain(p => p.Contains("64", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Stream:Sites:dmo:LatenessSeconds=90")]
    [InlineData("Stream:LatenessSeconds=30")]
    [InlineData("Stream:Sites:DMO:PushIntervalSeconds=180", "Stream:Sites:DMO:LatenessSeconds=300")]
    public async Task RunAsync_Should_RefuseToReplay_When_TheStreamSettingsAreNotValid(params string[] settings)
    {
        // ARV-148: a replay runs a site under the stream's lateness and push interval (Stream:Sites), so it refuses what the
        // stream refuses (exit code 2, bad arguments) before it reads or writes anything.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => s.Split('=', 2)).Select(p => new KeyValuePair<string, string>(p[0], p[1])))
            .Build();
        using var output = new StringWriter();
        string[] args = [.. Good.Where(a => !a.StartsWith("--replay-output=", StringComparison.Ordinal))];

        var code = await ReplayCommand.RunAsync(configuration, args, output, TestContext.Current.CancellationToken);

        code.Should().Be(2);
        output.ToString().Should().BeEmpty("nothing was replayed");
    }

    [Fact]
    public void Export_Should_BeANewOwnerOnlyFile_When_Created()
    {
        var folder = Directory.CreateTempSubdirectory("ariva-replay-");
        try
        {
            var path = Path.Combine(folder.FullName, "export.jsonl");
            using (var writer = ReplayCommand.CreateExport(path))
                writer.Write("x");

            var again = () => ReplayCommand.CreateExport(path).Dispose();
            again.Should().Throw<IOException>("an existing file is never overwritten");
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var target = Path.Combine(folder.FullName, "target");
                File.WriteAllText(target, "keep");
                var link = Path.Combine(folder.FullName, "link.jsonl");
                File.CreateSymbolicLink(link, target);
                var throughLink = () => ReplayCommand.CreateExport(link).Dispose();
                throughLink.Should().Throw<IOException>("a link in the export's place is refused");
                File.ReadAllText(target).Should().Be("keep");
            }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void Summary_Should_PrintTheRowHash_When_ARunIsRecorded()
    {
        var run = new Ariva.Infra.Streaming.ReplayRun(Guid.CreateVersion7(),
            new Ariva.Core.Queueing.Replay.ReplayManifest("ariva-replay/1", "DMO", ["A-VIS"], DateTime.UnixEpoch, DateTime.UnixEpoch.AddHours(1), 12, "1", new string('a', 64)),
            new Ariva.Core.Queueing.Replay.ReplayHashes(new string('b', 64), 1, 2, new string('c', 64), new string('d', 64), new string('e', 64)),
            new Dictionary<string, Ariva.Core.Queueing.ZoneProcessorCounters>(), new string('f', 64));

        using var printed = System.Text.Json.JsonDocument.Parse(ReplayCommand.Summary(run));

        printed.RootElement.GetProperty("rowHash").GetString().Should().Be(new string('f', 64), "the operator keeps the row hash outside the database");
        printed.RootElement.GetProperty("hashes").GetProperty("outputHead").GetString().Should().Be(new string('d', 64));
    }
}
