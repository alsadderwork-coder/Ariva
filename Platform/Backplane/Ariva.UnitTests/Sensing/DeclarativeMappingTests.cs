using System.Text;
using System.Text.Json;
using Ariva.Infra.Sensing.Declarative;
using FluentAssertions;

namespace Ariva.UnitTests.Sensing;

/// <summary>
/// ARV-024 (CWE-94): the restricted path syntax accepts member names, indexes and one wildcard where a mapping
/// enumerates, and refuses every JSONPath feature that filters, recurses, slices, unions or evaluates; evaluation only
/// walks JSON. Mapping documents are strict and checked whole; the shipped catalog loads.
/// </summary>
public sealed class DeclarativeMappingTests
{
    [Theory]
    [InlineData("$")]
    [InlineData("@.id")]
    [InlineData("^.timestamp")]
    [InlineData("$.object_list[0].frame_count")]
    [InlineData("@.position.x")]
    [InlineData("$['zone name'].count")]
    [InlineData("$.a-b._c[12]['x.y']")]
    [InlineData("$.__proto__")]
    [InlineData("$.constructor")]
    public void Parse_Should_AcceptPlainPaths(string text)
    {
        RestrictedPath.Parse(text, allowWildcard: false, "t").Text.Should().Be(text);
    }

    [Theory]
    [InlineData("$..id", "recursive descent")]
    [InlineData("$.objects[?(@.classification=='PERSON')]", "a filter")]
    [InlineData("$.objects[?@.x > 1]", "an RFC 9535 filter")]
    [InlineData("$.objects[(@.length-1)]", "a script expression")]
    [InlineData("$.objects[0:3]", "a slice")]
    [InlineData("$.objects[-1]", "a negative index")]
    [InlineData("$.objects[1e3]", "an exponent")]
    [InlineData("$.objects[ 0 ]", "spaces in an index")]
    [InlineData("$.objects[0,1]", "a union of indexes")]
    [InlineData("$['a','b']", "a union of names")]
    [InlineData("$.objects.length()", "a function")]
    [InlineData("$.count()", "a function call")]
    [InlineData("$.a;drop", "a statement separator")]
    [InlineData("${x}", "an interpolation")]
    [InlineData("$.a+1", "arithmetic")]
    [InlineData("$.a == 1", "a comparison")]
    [InlineData("$.*", "a dot wildcard")]
    [InlineData("$[*]", "a wildcard where none is allowed")]
    [InlineData("$.", "an empty member")]
    [InlineData("$a", "no separator")]
    [InlineData("$.1a", "a name starting with a digit")]
    [InlineData("$.ä", "a non-ASCII name")]
    [InlineData("$['a\\'b']", "an escaped quote")]
    [InlineData("$[\"a\"]", "double quotes")]
    [InlineData("$['']", "an empty quoted name")]
    [InlineData("$.a[0", "an unclosed bracket")]
    [InlineData("$.a]", "a stray bracket")]
    [InlineData("a.b", "no root")]
    [InlineData("", "an empty path")]
    [InlineData("$.a\n", "a line break")]
    [InlineData("$.a\0", "a NUL")]
    [InlineData("$[1234567]", "an index over six digits")]
    public void Parse_Should_RefuseHostilePaths(string text, string what)
    {
        var act = () => RestrictedPath.Parse(text, allowWildcard: false, "t");

        act.Should().Throw<MappingException>(what);
    }

    [Fact]
    public void Parse_Should_RefuseOversizedPathsAndASecondWildcard()
    {
        var tooLong = "$." + new string('a', RestrictedPath.MaxLength);
        var tooDeep = "$" + string.Concat(Enumerable.Repeat(".a", RestrictedPath.MaxSegments + 1));
        var longName = "$." + new string('a', RestrictedPath.MaxNameLength + 1);

        foreach (var text in new[] { tooLong, tooDeep, longName })
            ((Action)(() => RestrictedPath.Parse(text, allowWildcard: false, "t"))).Should().Throw<MappingException>();
        ((Action)(() => RestrictedPath.Parse("$.a[*].b[*]", allowWildcard: true, "t"))).Should().Throw<MappingException>().WithMessage("*at most one wildcard*");
        RestrictedPath.Parse("$.a[*].b", allowWildcard: true, "t").HasWildcard.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_Should_WalkJsonOnly()
    {
        using var document = JsonDocument.Parse("""{ "a": { "b": [10, { "c": "x" }], "n": null, "zone name": 4 } }""");
        var root = document.RootElement;

        RestrictedPath.Parse("$.a.b[0]", false, "t").Single(root, default, default)!.Value.GetInt32().Should().Be(10);
        RestrictedPath.Parse("$.a.b[1].c", false, "t").Single(root, default, default)!.Value.GetString().Should().Be("x");
        RestrictedPath.Parse("$.a['zone name']", false, "t").Single(root, default, default)!.Value.GetInt32().Should().Be(4);
        RestrictedPath.Parse("$.a.b[2]", false, "t").Single(root, default, default).Should().BeNull("an index past the end is missing");
        RestrictedPath.Parse("$.a.b.c", false, "t").Single(root, default, default).Should().BeNull("a member of an array is missing");
        RestrictedPath.Parse("$.a.n", false, "t").Single(root, default, default).Should().BeNull("null is missing");
        RestrictedPath.Parse("$.a.b[0].c", false, "t").Single(root, default, default).Should().BeNull("a member of a number is missing");
        RestrictedPath.Parse("$.a.b[*]", true, "t").Many(root, default, 10).Should().HaveCount(2);
        RestrictedPath.Parse("$.a.b[*].c", true, "t").Many(root, default, 10).Should().ContainSingle("items without the member are skipped");
        RestrictedPath.Parse("$.a[*]", true, "t").Many(root, default, 10).Should().BeEmpty("a wildcard enumerates arrays only");
    }

    [Fact]
    public void Many_Should_StopOnePastTheLimit()
    {
        using var document = JsonDocument.Parse("[" + string.Join(',', Enumerable.Range(0, 100)) + "]");

        RestrictedPath.Parse("$[*]", true, "t").Many(document.RootElement, default, 5).Should().HaveCount(6);
    }

    private const string Minimal = """
        { "name": "test-v1", "title": "Test",
          "occupancy": { "items": "$.zones[*]", "zone": "@.name", "count": "@.n", "time": { "received": true } } }
        """;

    private static DeclarativeMapping Parse(string json, string name = "test-v1") => DeclarativeMapping.Parse(Encoding.UTF8.GetBytes(json), name);

    [Fact]
    public void Mapping_Should_ParseAMinimalDocument()
    {
        var mapping = Parse(Minimal);

        mapping.Name.Should().Be("test-v1");
        mapping.Occupancy.Should().NotBeNull();
        mapping.Occupancy.Time.Received.Should().BeTrue();
        mapping.DeviceFrame.Should().BeFalse();
        mapping.Scale.Should().Be(1);
    }

    [Theory]
    [InlineData("\"title\": \"Test\",", "\"title\": \"Test\", \"script\": \"x\",", "*not valid*")]
    [InlineData("\"title\": \"Test\",", "\"title\": \"Test\", // note\n", "*not valid*")]
    [InlineData("\"name\": \"test-v1\"", "\"name\": \"Test_V1\"", "name:*")]
    [InlineData("\"name\": \"test-v1\"", "\"name\": \"other-v1\"", "*not the expected*")]
    [InlineData("\"title\": \"Test\",", "\"title\": \"\",", "*title*")]
    [InlineData("\"title\": \"Test\",", "\"title\": \"Test\", \"source\": \"http://example.com\",", "*source*")]
    [InlineData("\"$.zones[*]\"", "\"$.zones\"", "*one [*]*")]
    [InlineData("\"$.zones[*]\"", "\"^.zones[*]\"", "*from the group (^) when there are groups*")]
    [InlineData("\"@.name\"", "\"@.names[*]\"", "*wildcard*")]
    [InlineData("\"@.name\"", "\"^.name\"", "*only for sections with groups*")]
    [InlineData("\"@.name\"", "\"$..name\"", "*")]
    [InlineData("\"@.name\"", "\"@.name[?(@.x)]\"", "*no filters*")]
    [InlineData("{ \"received\": true }", "{ \"received\": true, \"path\": \"@.t\" }", "*either received or a path*")]
    [InlineData("{ \"received\": true }", "{ \"path\": \"@.t\", \"unit\": \"days\" }", "*unit*")]
    [InlineData("{ \"received\": true }", "{ \"path\": \"@.t\" }", "*unit*")]
    [InlineData("\"time\": { \"received\": true }", "\"time\": { \"received\": true }, \"where\": [{ \"path\": \"@.k\", \"equals\": [] }]", "*equals*")]
    [InlineData("\"title\": \"Test\",", "\"title\": \"Test\", \"positions\": { \"frame\": \"world\" },", "*frame*")]
    [InlineData("\"title\": \"Test\",", "\"title\": \"Test\", \"positions\": { \"scale\": 0 },", "*scale*")]
    [InlineData("\"title\": \"Test\",", "\"title\": \"Test\", \"positions\": { \"scale\": 1000000 },", "*scale*")]
    [InlineData("\"title\": \"Test\",", "\"title\": \"Test\", \"sentTime\": { \"received\": true },", "*sentTime*")]
    [InlineData("\"title\": \"Test\",", "\"title\": \"Test\", \"package\": \"@.seq\",", "*payload*")]
    [InlineData("\"title\": \"Test\",", "\"title\": \"Test\", \"positions\": { \"scale\": \"1\" },", "*not valid*")]
    public void Mapping_Should_RefuseABrokenDocument(string from, string to, string message)
    {
        var act = () => Parse(Minimal.Replace(from, to, StringComparison.Ordinal));

        act.Should().Throw<MappingException>().WithMessage(message);
    }

    [Fact]
    public void Mapping_Should_RefuseEmptyAndOversizedDocumentsAndSectionRules()
    {
        ((Action)(() => Parse("""{ "name": "test-v1", "title": "Test" }"""))).Should().Throw<MappingException>().WithMessage("*at least one*");
        ((Action)(() => DeclarativeMapping.Parse([], "x"))).Should().Throw<MappingException>();
        ((Action)(() => DeclarativeMapping.Parse(new byte[DeclarativeMapping.MaxDocumentBytes + 1], "x"))).Should().Throw<MappingException>();
        ((Action)(() => Parse("""
            { "name": "test-v1", "title": "Test",
              "intervals": { "items": "$.c[*]", "line": "@.l", "from": { "received": true }, "to": { "path": "@.t", "unit": "s" }, "in": "@.i" } }
            """))).Should().Throw<MappingException>().WithMessage("*from and to come from the payload*");
        ((Action)(() => Parse("""
            { "name": "test-v1", "title": "Test",
              "intervals": { "items": "$.c[*]", "line": "@.l", "from": { "path": "@.f", "unit": "s" }, "to": { "path": "@.t", "unit": "s" } } }
            """))).Should().Throw<MappingException>().WithMessage("*in, out or both*");
        ((Action)(() => Parse("""
            { "name": "test-v1", "title": "Test",
              "crossings": { "items": "$.c[*]", "line": "@.l", "direction": { "path": "@.d", "in": ["A"], "out": ["A"] }, "time": { "received": true } } }
            """))).Should().Throw<MappingException>().WithMessage("*both in and out*");
        ((Action)(() => Parse("""
            { "name": "test-v1", "title": "Test",
              "occupancy": { "items": "$.z[*]", "zone": "@.n", "count": "@.c", "time": { "received": true },
                "where": [{ "path": "@.a", "equals": ["1"] }, { "path": "@.a", "equals": ["1"] }, { "path": "@.a", "equals": ["1"] },
                          { "path": "@.a", "equals": ["1"] }, { "path": "@.a", "equals": ["1"] }] } }
            """))).Should().Throw<MappingException>().WithMessage("*at most 4 filters*");
        ((Action)(() => Parse("{\"name\":\"test-v1\",\"title\":\"Test\",\"tracks\":" + new string('[', 20) + new string(']', 20) + "}")))
            .Should().Throw<MappingException>("nesting beyond the limit is refused");
    }

    [Fact]
    public void Catalog_Should_LoadTheShippedMappings()
    {
        var catalog = DeclarativeMappingCatalog.Embedded;

        catalog.Contains("ouster-detect-v1").Should().BeTrue();
        catalog.Contains("OUSTER-DETECT-V1").Should().BeFalse("names are exact");
        catalog.Contains(null).Should().BeFalse();
        catalog.Find("ouster-detect-v1")!.DeviceFrame.Should().BeTrue();
        catalog.Mappings.Should().ContainSingle(m => m.Name == "ouster-detect-v1")
            .Which.Kinds.Should().Equal("tracks", "occupancy");
        catalog.Mappings.Should().OnlyContain(m => m.Source.StartsWith("https://", StringComparison.Ordinal), "every shipped mapping names the vendor documentation it follows");
    }

    [Fact]
    public void Catalog_Should_RefuseTwoMappingsOfTheSameName()
    {
        var act = () => new DeclarativeMappingCatalog([Parse(Minimal), Parse(Minimal)]);

        act.Should().Throw<MappingException>().WithMessage("*Two mappings*");
    }
}
