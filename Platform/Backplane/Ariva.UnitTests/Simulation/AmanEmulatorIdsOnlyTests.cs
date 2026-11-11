using System.Security.Cryptography;
using System.Text.Json;
using Ariva.Business.Contracts.Aman.V1;
using Ariva.Simulation.Api.Emulators.Aman;
using Ariva.Simulation.Api.Scenarios.Engine;
using FluentAssertions;

namespace Ariva.UnitTests.Simulation;

/// <summary>
/// ARV-279c: the change the owner approved to <c>aman-emulator-golden.json</c> on 2026-11-11 is the source event ids
/// alone. The fingerprint of <see cref="AmanEmulatorGoldenTests"/> is computed again over the same day (seeds 9303 and 2026,
/// every minute, both border sides, the same JSON) with each id written as it was before ARV-279c (the anchor's <c>T0000</c>
/// taken out of the tag): it is the value approved on 2026-10-10, held here as it was approved, so the figures (sessions, desk
/// intervals, e-gate counts, lane demand), the flight keys and the times are byte for byte what they were, and every id carries
/// the anchor (an id without it fails the test).
/// </summary>
public sealed class AmanEmulatorIdsOnlyTests(ITestOutputHelper output)
{
    #region Fields

    private static readonly DateTime DayStart = new(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);

    #endregion

    #region Methods

    /// <summary>The fingerprint of <see cref="AmanEmulatorGoldenTests.Fingerprint"/>, with every source event id rewritten by <paramref name="rewrite"/>.</summary>
    internal static string Fingerprint(uint seed, Func<string, string> rewrite)
    {
        var day = ScenarioDay.Run(ScenarioConfig.Reference(seed) with { Lite = true });
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var minute = 0; minute < ScenarioModel.Day; minute++)
        {
            var published = AmanFeed.Build(day, minute, m => DayStart.AddMinutes(m), ScenarioModel.SiteCode, BorderSides.Both, minute == 0);
            List<DeskSessionChanged> sessions = [.. published.Sessions.Select(s => s with { SourceEventId = rewrite(s.SourceEventId) })];
            List<DeskIntervalStats> desks = [.. published.Desks.Select(d => d with { SourceEventId = rewrite(d.SourceEventId) })];
            List<EGateIntervalStats> gates = [.. published.Gates.Select(g => g with { SourceEventId = rewrite(g.SourceEventId) })];
            List<InboundFlightLaneDemand> demand = [.. published.Demand.Select(d => d with { SourceEventId = rewrite(d.SourceEventId) })];
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { published.Minute, Sessions = sessions, Desks = desks, Gates = gates, Demand = demand }));
            hash.AppendData([0]);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// An id as the emulator wrote it before ARV-279c: <c>aman-&lt;seed&gt;-20261003-...</c> where it now writes
    /// <c>aman-&lt;seed&gt;-20261003T0000-...</c> for a day laid on midnight. An id in any other form fails the test.
    /// </summary>
    private static string BeforeTheAnchor(uint seed, string id)
    {
        var anchored = "aman-" + seed.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-20261003T0000-";
        if (id is null || !id.StartsWith(anchored, StringComparison.Ordinal))
            throw new InvalidOperationException("An id without the anchor of the day laid on 2026-10-03 00:00.");
        return anchored[..^6] + "-" + id[anchored.Length..];
    }

    [Theory]
    [InlineData(9303u, "328e5473fcf4ae26f175aead2942db212ca849ef30d8457d8f36f4006c226b2e")]
    [InlineData(2026u, "b5db7a2a6b63d5fce71a743a51df0d1f3cdf8d11543d678e49f79ff6020c9adc")]
    public void AmanFeed_Should_PublishTheDayApprovedOn20261010_When_TheAnchorIsTakenOutOfTheIds(uint seed, string approved)
    {
        // The values of aman-emulator-golden.json as the owner approved them on 2026-10-10 (ARV-149a), before ARV-279c.
        var actual = Fingerprint(seed, id => BeforeTheAnchor(seed, id));
        output.WriteLine($"Seed {seed}, ids as before ARV-279c: {actual}");

        actual.Should().Be(approved, "ARV-279c changed the ids alone: written as before, the day is the one approved on 2026-10-10");
    }

    #endregion
}
