using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Services.Topology;
using FluentAssertions;

namespace Ariva.UnitTests.Domain.Topology;

/// <summary>
/// ARV-122a (CWE-501, CWE-120): a bundle's configuration parts are read strictly. A member the request rows do not have
/// (an id, a site code), a member twice (also when only its case differs), a number written as a string, a comment, a
/// trailing comma or more than 32 levels of nesting makes the part unreadable: one fixed refusal, before any rule runs.
/// ARV-122b: a device, alert rule or display row has no member for a credential, an access setting, a state, a calibration
/// or an escalation contact, so a bundle that carries one is refused whole.
/// </summary>
public sealed class SiteConfigurationPartsTests
{
    private const string Topology =
        """{"terminals":[{"airport":"AAA","code":"T1","name":"Terminal 1"}],"levels":[{"terminal":"T1","code":"L0","name":"Arrivals","floorNumber":0,"widthMetres":60,"depthMetres":40}],"checkpoints":[],"desks":[]}""";

    private static OpenedSiteBundle Bundle(string topology = Topology, string mappings = "[]", string devices = "[]", string rules = "[]", string displays = "[]") =>
        new(1, "DMO", "2026-10-11T08:00:00Z", new string('0', 64), new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [SiteConfigurationBundles.Topology] = Encoding.UTF8.GetBytes(topology),
            [SiteConfigurationBundles.DeskCodeMappings] = Encoding.UTF8.GetBytes(mappings),
            [SiteConfigurationBundles.ZoneProfile] = "[]"u8.ToArray(),
            [SiteConfigurationBundles.FloorPlans] = "[]"u8.ToArray(),
            [SiteConfigurationBundles.Devices] = Encoding.UTF8.GetBytes(devices),
            [SiteConfigurationBundles.AlertRules] = Encoding.UTF8.GetBytes(rules),
            [SiteConfigurationBundles.Displays] = Encoding.UTF8.GetBytes(displays)
        });

    private static readonly string DeviceRows = Encoding.UTF8.GetString(SiteConfigurationParts.Write(new[]
    {
        new DevicePartRow("S01", "StereoVision", "Xovis", "HttpsPush", "Xovis", "Ntp", null, "T1", "L0", 20, 16, 5, 0, "Snake A", null, null, null, true)
    }));

    private static readonly string RuleRows = Encoding.UTF8.GetString(SiteConfigurationParts.Write(new[]
    {
        new AlertRulePartRow("R-001", "Long wait", ["Snake A"], "Nowcast", "GreaterThan", 20, null, null, 5, 5, "Warning", null, 15, null, true, false, true, null)
    }));

    private static readonly string DisplayRows = Encoding.UTF8.GetString(SiteConfigurationParts.Write(new[]
    {
        new DisplayPartRow("DSP1", "Hall board", null, "Landscape", ["en"], 5, 1, 300, [new DisplayEntryPartRow("Snake A", new Dictionary<string, string> { ["en"] = "All" })],
            new Dictionary<string, string> { ["en"] = "Ask staff" }, true)
    }));

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
    public void Read_Should_ReadDevicesRulesAndDisplays_When_TheirRowsAreStrictJson()
    {
        var content = SiteConfigurationParts.Read(Bundle(devices: DeviceRows, rules: RuleRows, displays: DisplayRows));

        content.HasErrors.Should().BeFalse(string.Join(", ", content.ErrorMessages ?? []));
        content.Data.Devices.Should().ContainSingle().Which.Should().Match<DevicePartRow>(d => d.QueueZone == "Snake A" && d.AccessSettingsWithheld);
        content.Data.AlertRules.Should().ContainSingle().Which.EscalationContactWithheld.Should().BeTrue();
        content.Data.Displays.Should().ContainSingle().Which.Entries.Should().ContainSingle();
    }

    public static TheoryData<string, string> SecretMembers => new()
    {
        { "devices", "credential" },
        { "devices", "credentialPrefix" },
        { "devices", "credentialHash" },
        { "devices", "credentialIssuedOn" },
        { "devices", "allowedSources" },
        { "devices", "clientCertificateSha256" },
        { "devices", "state" },
        { "devices", "calibrations" },
        { "devices", "id" },
        { "alertRules", "escalationContact" },
        { "alertRules", "siteCode" },
        { "displays", "credentialPrefix" },
        { "displays", "credentialHash" },
        { "displays", "pairingSecret" },
        { "displays", "siteCode" }
    };

    [Theory]
    [MemberData(nameof(SecretMembers))]
    public void Read_Should_RefuseThePart_When_ARowCarriesAMemberThatIsNeverExported(string part, string member)
    {
        string Adding(string rows) => rows.StartsWith("[{", StringComparison.Ordinal)
            ? "[{\"" + member + "\":\"x\"," + rows[2..]
            : throw new InvalidOperationException("A row list starts with an object.");
        var bundle = part switch
        {
            "devices" => Bundle(devices: Adding(DeviceRows)),
            "alertRules" => Bundle(rules: Adding(RuleRows)),
            _ => Bundle(displays: Adding(DisplayRows))
        };

        SiteConfigurationParts.Read(bundle).ErrorMessages.Should().Equal([SiteConfigurationErrors.PartNotReadable], $"{part} {member}");
    }

    public static TheoryData<string, string> MissingMembers => new()
    {
        { "devices", "accessSettingsWithheld" },
        { "devices", "footprintRadiusMetres" },
        { "devices", "mappingName" },
        { "alertRules", "escalationContactWithheld" },
        { "alertRules", "ownerRole" },
        { "displays", "location" },
        { "displays", "enabled" }
    };

    [Theory]
    [MemberData(nameof(MissingMembers))]
    public void Read_Should_RefuseThePart_When_ARowLeavesOutAMember(string part, string member)
    {
        // Every member is written by an export (null when empty), so a row without one is not of its kind: a bundle cannot
        // drop a withheld flag and have it read as false.
        string Without(string rows)
        {
            var row = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(rows)!;
            row[0].Remove(member).Should().BeTrue($"{member} is a member of a {part} row");
            return JsonSerializer.Serialize(row);
        }

        var bundle = part switch
        {
            "devices" => Bundle(devices: Without(DeviceRows)),
            "alertRules" => Bundle(rules: Without(RuleRows)),
            _ => Bundle(displays: Without(DisplayRows))
        };

        SiteConfigurationParts.Read(bundle).ErrorMessages.Should().Equal([SiteConfigurationErrors.PartNotReadable], $"{part} without {member}");
    }

    [Fact]
    public void Read_Should_RefuseTheTopology_When_ATerminalLeavesOutItsName()
    {
        var nameless = With(",\"name\":\"Terminal 1\"", string.Empty);

        SiteConfigurationParts.Read(Bundle(nameless)).ErrorMessages.Should().Equal([SiteConfigurationErrors.PartNotReadable], "every member is required in the ARV-122a parts too");
    }

    [Theory]
    [InlineData("devices")]
    [InlineData("alertRules")]
    [InlineData("displays")]
    public void Read_Should_RefuseThePart_When_ItIsNotAList(string part)
    {
        var bundle = part switch
        {
            "devices" => Bundle(devices: "null"),
            "alertRules" => Bundle(rules: "{}"),
            _ => Bundle(displays: "\"[]\"")
        };

        SiteConfigurationParts.Read(bundle).ErrorMessages.Should().Equal([SiteConfigurationErrors.PartNotReadable], part);
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
        options.RespectRequiredConstructorParameters.Should().BeTrue();
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
