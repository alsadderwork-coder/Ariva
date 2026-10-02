using System.Text.Json;
using Ariva.Simulation.Api.Scenarios.Engine;
using Ariva.UnitTests.Setup;
using FluentAssertions;

namespace Ariva.UnitTests.Simulation;

/// <summary>
/// ARV-027: the C# scenario engine reproduces the prototype's sim.js bit for bit. Each case of reference-golden.json
/// (written by scripts/simulation/reference-golden.mjs from sim.js) fingerprints every output family; the port must
/// match all of them, so the same seed gives identical outputs in the browser prototype and in the simulator.
/// </summary>
public sealed class ScenarioParityTests
{
    private static readonly Lazy<JsonElement> Golden = new(() =>
        JsonDocument.Parse(File.ReadAllText(RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Simulation/reference-golden.json"))).RootElement.Clone());

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Golden.Value.GetProperty("cases").EnumerateArray())
            data.Add(c.GetProperty("name").GetString());
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Run_Should_MatchTheReferenceFingerprints_When_GivenTheSameConfiguration(string name)
    {
        var c = Golden.Value.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var expected = c.GetProperty("fingerprints").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) : p.Value.GetString());

        var actual = ScenarioFingerprints.Compute(ScenarioFingerprints.ReadConfig(c.GetProperty("config")), out _);

        actual.Should().Equal(expected, "every output family of case {0} must match sim.js bit for bit", name);
    }

    [Fact]
    public void Run_Should_GiveIdenticalOutputs_When_TheSameSeedRunsTwice()
    {
        var first = ScenarioFingerprints.Compute(ScenarioConfig.Reference(), out _);
        var second = ScenarioFingerprints.Compute(ScenarioConfig.Reference(), out _);

        second.Should().Equal(first);
    }

    [Fact]
    public void Run_Should_GiveDifferentDays_When_SeedsDiffer()
    {
        var a = ScenarioDay.Run(ScenarioConfig.Reference(9303));
        var b = ScenarioDay.Run(ScenarioConfig.Reference(9304));

        a.Schedule.Arrivals.Select(f => f.Code).Should().NotEqual(b.Schedule.Arrivals.Select(f => f.Code));
    }

    [Fact]
    public void Reference_Should_UseSeed9303_When_NoSeedIsGiven()
    {
        ScenarioConfig.Reference().Seed.Should().Be(9303u);
        new ScenarioConfig().Seed.Should().Be(9303u);
        ScenarioModel.DefaultSeed.Should().Be(9303u);
    }
}
