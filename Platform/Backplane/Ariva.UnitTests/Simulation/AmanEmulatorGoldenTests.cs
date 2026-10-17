using System.Security.Cryptography;
using System.Text.Json;
using Ariva.Simulation.Api.Emulators.Aman;
using Ariva.Simulation.Api.Scenarios.Engine;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Simulation;

/// <summary>
/// ARV-149a (from the ARV-151 security review): what the AMAN emulator publishes is locked, so "the AMAN figures are
/// unchanged" is a test rather than a claim. For the reference day (seed 9303) and the held-out day (seed 2026), every
/// minute's feed (<see cref="AmanFeed.Build"/>, both border sides: desk sessions, the desks' interval statistics of
/// <see cref="AmanDeskProcess"/>, e-gate intervals and inbound lane demand) is serialised as JSON, minute after minute, and
/// fingerprinted with SHA-256 against <c>aman-emulator-golden.json</c>. A change is reviewed and the value updated with the
/// human's approval.
/// </summary>
public sealed class AmanEmulatorGoldenTests(ITestOutputHelper output)
{
    private static readonly DateTime DayStart = new(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The SHA-256 of the AMAN emulator's whole day for a seed.</summary>
    internal static string Fingerprint(uint seed)
    {
        var day = ScenarioDay.Run(ScenarioConfig.Reference(seed) with { Lite = true });
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var minute = 0; minute < ScenarioModel.Day; minute++)
        {
            var published = AmanFeed.Build(day, minute, m => DayStart.AddMinutes(m), ScenarioModel.SiteCode, BorderSides.Both, minute == 0);
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { published.Minute, published.Sessions, published.Desks, published.Gates, published.Demand }));
            hash.AppendData([0]);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    [Theory]
    [InlineData(9303u)]
    [InlineData(2026u)]
    public void AmanFeed_Should_PublishTheLockedFigures_When_TheDayIsPlayed(uint seed)
    {
        var locked = JsonDocument.Parse(File.ReadAllText(RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Simulation/aman-emulator-golden.json")))
            .RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("seed").GetUInt32() == seed).GetProperty("sha256").GetString();
        var actual = Fingerprint(seed);
        output.WriteLine($"Seed {seed}: {actual}");

        actual.Should().Be(locked, "the AMAN emulator's published day is locked; a change is reviewed and the value updated with the human's approval");
    }

    [Fact]
    public void Fingerprint_Should_DifferBetweenSeeds_When_TheDaysDiffer()
    {
        Fingerprint(9303).Should().NotBe(Fingerprint(2026), "another seed draws another day");
    }
}
