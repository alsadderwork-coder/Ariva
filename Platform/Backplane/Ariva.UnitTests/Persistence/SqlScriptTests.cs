using System.Text.Json;
using Ariva.Infra.Timescale;
using FluentAssertions;

namespace Ariva.UnitTests.Persistence;

/// <summary>ARV-006: script naming, checksums, ordering and the immutability lock.</summary>
public sealed class SqlScriptTests
{
    [Fact]
    public void From_Should_ParseNumberAndChecksum_When_NameIsValid()
    {
        var script = SqlScript.From("0007_create_zone_profile.sql", "CREATE TABLE zone_profile (id uuid);");

        script.Number.Should().Be(7);
        script.Checksum.Should().MatchRegex("^[0-9a-f]{64}$");
        script.RunInTransaction.Should().BeTrue();
    }

    [Theory]
    [InlineData("7_create.sql")]
    [InlineData("0007_Create.sql")]
    [InlineData("0007-create.sql")]
    [InlineData("0007_create.SQL")]
    [InlineData("0007_.sql")]
    [InlineData("../0007_create.sql")]
    public void From_Should_Throw_When_NameBreaksConvention(string name)
    {
        var act = () => SqlScript.From(name, "SELECT 1;");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Checksum_Should_IgnoreLineEndingsAndByteOrderMark_When_ContentIsTheSame()
    {
        var lf = SqlScript.From("0001_a.sql", "SELECT 1;\nSELECT 2;\n");
        var crlf = SqlScript.From("0001_a.sql", "SELECT 1;\r\nSELECT 2;\r\n");
        var bom = SqlScript.From("0001_a.sql", "﻿SELECT 1;\nSELECT 2;\n");
        var edited = SqlScript.From("0001_a.sql", "SELECT 1;\nSELECT 3;\n");

        crlf.Checksum.Should().Be(lf.Checksum, "a Windows checkout must not look like tampering");
        bom.Checksum.Should().Be(lf.Checksum);
        edited.Checksum.Should().NotBe(lf.Checksum);
    }

    [Fact]
    public void RunInTransaction_Should_BeFalse_When_FirstLineIsNoTransactionMarker()
    {
        var script = SqlScript.From("0003_index.sql", "-- ariva:no-transaction\r\nCREATE INDEX CONCURRENTLY ix ON t (c);");

        script.RunInTransaction.Should().BeFalse();
    }

    [Fact]
    public void Order_Should_SortByNumberAndSkipReadme_When_ScriptsAreUnordered()
    {
        var ordered = SqlScriptCatalog.Order(
        [
            SqlScript.From("0010_c.sql", "SELECT 1;"),
            SqlScript.From("0000_readme.sql", "/* docs */"),
            SqlScript.From("0002_b.sql", "SELECT 1;")
        ]);

        ordered.Select(s => s.Name).Should().Equal("0002_b.sql", "0010_c.sql");
    }

    [Fact]
    public void Order_Should_Throw_When_NumbersRepeat()
    {
        var act = () => SqlScriptCatalog.Order([SqlScript.From("0002_a.sql", "SELECT 1;"), SqlScript.From("0002_b.sql", "SELECT 2;")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*0002*");
    }

    [Fact]
    public void Embedded_Should_MatchChecksumsLock_When_ScriptsAreShipped()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "checksums.lock")));
        var locked = document.RootElement.GetProperty("scripts").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());

        var embedded = SqlScriptCatalog.Embedded().ToDictionary(s => s.Name, s => s.Checksum);

        embedded.Keys.Should().BeEquivalentTo(locked.Keys,
            "every shipped script has a checksums.lock entry (add it with the script) and no listed script is removed");
        foreach (var (name, checksum) in embedded)
        {
            checksum.Should().Be(locked[name],
                $"{name} is shipped and immutable; revert the edit and add a new script instead (new checksum would be {checksum})");
        }
    }

    [Fact]
    public void Embedded_Should_StartWithRolesScript_When_Loaded()
    {
        var scripts = SqlScriptCatalog.Embedded();

        scripts.Should().NotBeEmpty();
        scripts[0].Name.Should().Be("0001_roles.sql");
        scripts.Should().NotContain(s => s.Number == 0, "0000 is the folder README and is never applied");
    }
}
