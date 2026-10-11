using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ariva.Core.Services.Topology;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Topology;

/// <summary>
/// ARV-122a (CWE-501, CWE-120): a bundle's configuration parts are read strictly. A member the request rows do not have
/// (an id, a site code), a member twice (also when only its case differs), a number written as a string, a comment, a
/// trailing comma or more than 32 levels of nesting makes the part unreadable: one fixed refusal, before any rule runs.
/// </summary>
public sealed class SiteConfigurationPartsTests
{
    private const string Topology =
        """{"terminals":[{"airport":"AAA","code":"T1","name":"Terminal 1"}],"levels":[{"terminal":"T1","code":"L0","name":"Arrivals","floorNumber":0,"widthMetres":60,"depthMetres":40}],"checkpoints":[],"desks":[]}""";

    private static OpenedSiteBundle Bundle(string topology = Topology, string mappings = "[]") =>
        new(1, "DMO", "2026-10-11T08:00:00Z", new string('0', 64), new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [SiteConfigurationBundles.Topology] = Encoding.UTF8.GetBytes(topology),
            [SiteConfigurationBundles.DeskCodeMappings] = Encoding.UTF8.GetBytes(mappings),
            [SiteConfigurationBundles.ZoneProfile] = "[]"u8.ToArray(),
            [SiteConfigurationBundles.FloorPlans] = "[]"u8.ToArray()
        });

    [Fact]
    public void Read_Should_ReadTheRows_When_ThePartsAreStrictJson()
    {
        var content = SiteConfigurationParts.Read(Bundle());

        content.HasErrors.Should().BeFalse(string.Join(", ", content.ErrorMessages ?? []));
        content.Data.Topology.Levels.Should().ContainSingle().Which.FloorNumber.Should().Be(0);
    }

    private static string With(string from, string to)
    {
        Topology.Should().Contain(from, "the case must change the part");
        return Topology.Replace(from, to, StringComparison.Ordinal);
    }

    public static TheoryData<string, string, string> NotStrict => new()
    {
        { "an id member in a terminal row", With("{\"airport\":\"AAA\",", "{\"id\":\"0199a000-0000-7000-8000-00000000a1a1\",\"airport\":\"AAA\","), "[]" },
        { "a siteCode member in a terminal row", With("{\"airport\":\"AAA\",", "{\"siteCode\":\"OTHER\",\"airport\":\"AAA\","), "[]" },
        { "a duplicate name", With("\"name\":\"Terminal 1\"", "\"name\":\"Terminal 1\",\"name\":\"Terminal 2\""), "[]" },
        { "a duplicate that differs only in case", With("\"name\":\"Terminal 1\"", "\"name\":\"Terminal 1\",\"Name\":\"Terminal 2\""), "[]" },
        { "a floor number written as a string", With("\"floorNumber\":0", "\"floorNumber\":\"1\""), "[]" },
        { "a comment", With("\"checkpoints\":[]", "\"checkpoints\":[]/* a comment */"), "[]" },
        { "a trailing comma", With("\"desks\":[]}", "\"desks\":[],}"), "[]" },
        { "a part 33 levels deep", Topology, new string('[', 33) + new string(']', 33) }
    };

    [Theory]
    [MemberData(nameof(NotStrict))]
    public void Read_Should_RefuseThePartAsNotReadable_When_ItIsNotStrictJsonOfItsKind(string change, string topology, string mappings)
    {
        (topology != Topology || mappings != "[]").Should().BeTrue("each case changes a part (a replacement that finds nothing would test nothing)");

        var content = SiteConfigurationParts.Read(Bundle(topology, mappings));

        content.ErrorMessages.Should().Equal([SiteConfigurationErrors.PartNotReadable], change);
    }

    [Fact]
    public void Json_Should_KeepTheStrictOptions_When_PartsAreRead()
    {
        // The theory's cases fail on these options; this pins them, including the depth bound, which a part of the known
        // kinds cannot reach without first breaking its type.
        var options = SiteConfigurationParts.Json;
        options.UnmappedMemberHandling.Should().Be(JsonUnmappedMemberHandling.Disallow);
        options.AllowDuplicateProperties.Should().BeFalse();
        options.PropertyNameCaseInsensitive.Should().BeFalse();
        options.NumberHandling.Should().Be(JsonNumberHandling.Strict);
        options.ReadCommentHandling.Should().Be(JsonCommentHandling.Disallow);
        options.AllowTrailingCommas.Should().BeFalse();
        options.MaxDepth.Should().Be(32);

        var atBound = new string('[', 32) + new string(']', 32);
        var beyond = new string('[', 33) + new string(']', 33);
        var read = () => JsonSerializer.Deserialize<JsonElement>(atBound, options);
        var tooDeep = () => JsonSerializer.Deserialize<JsonElement>(beyond, options);
        read.Should().NotThrow("32 levels are the bound");
        tooDeep.Should().Throw<JsonException>();
    }
}
