using System.Text;
using Ariva.Core.Services.Imports;
using Ariva.Infra.Imports;
using FluentAssertions;

namespace Ariva.UnitTests.Imports;

/// <summary>
/// ARV-123a (CWE-120, CWE-501, CWE-79): the CSV reader of the imports. RFC 4180 quoting with commas, quotes and line breaks
/// inside quotes, CRLF and LF, a UTF-8 byte order mark; the header by exact names; bounds at the bound and one beyond;
/// formula prefixes refused in text and numbers, a minus read only as a sign; refusals by row and column with fixed texts.
/// </summary>
public sealed class CsvImportReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string DeviceHeader = "code,family,model,transport,dialect,clockSource,terminal,level,x,y,mountingHeightMetres,orientationDegrees,queueZone";

    private static string Device(string code = "S01", string model = "PC2SE", string x = "20", string y = "16", string height = "5", string orientation = "0",
        string zone = "Snake A") => $"{code},StereoVision,{model},HttpsPush,Xovis,Ntp,T1,L0,{x},{y},{height},{orientation},{zone}";

    private static async Task<(CsvTable Table, string Error)> ReadAsync(string text, CsvSchema schema = null, bool bom = false)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bom)
            bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        return await ReadBytesAsync(bytes, schema);
    }

    private static async Task<(CsvTable Table, string Error)> ReadBytesAsync(byte[] bytes, CsvSchema schema = null)
    {
        using var stream = new MemoryStream(bytes);
        return await CsvImportReader.ReadAsync(stream, schema ?? DeviceImportRows.Schema, Ct);
    }

    private static readonly IReadOnlySet<string> FixedTexts = typeof(CsvImportErrors).GetFields()
        .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()).ToHashSet(StringComparer.Ordinal);

    #region Reading

    [Fact]
    public async Task ReadAsync_Should_ReadQuotedCommasQuotesAndCrLf_When_TheFileStartsWithAByteOrderMark()
    {
        var (table, error) = await ReadAsync($"{DeviceHeader}\r\n{Device(model: "\"Xovis, PC2 \"\"SE\"\"\"")}\r\n{Device("S02")}\r\n", bom: true);

        error.Should().BeNull();
        table.Rows.Should().Be(2);
        table.Valid.Select(r => r.Row).Should().Equal(2, 3);
        table.Valid[0].Text["model"].Should().Be("Xovis, PC2 \"SE\"", "a quoted cell keeps its commas and doubled quotes");
        table.Valid[0].Numbers["x"].Should().Be(20);
        table.Column("queueZone").Should().Be(13);
    }

    [Fact]
    public async Task ReadAsync_Should_ReadTheRows_When_LinesEndWithLfAndOptionalColumnsComeInAnyOrder()
    {
        var header = "queueZone,code,family,model,transport,dialect,clockSource,terminal,level,x,y,mountingHeightMetres,orientationDegrees,footprintRadiusMetres";
        var (table, error) = await ReadAsync($"{header}\nSnake A,S01,Lidar,Ouster,HttpsPush,Canonical,Ptp,T1,L0,20,16,5,0,8\n");

        error.Should().BeNull();
        table.Valid.Should().ContainSingle().Which.Numbers["footprintRadiusMetres"].Should().Be(8);
        table.Column("code").Should().Be(2);
        table.ColumnNumbers.Should().NotContainKey("mappingName");
    }

    [Fact]
    public async Task ReadAsync_Should_RefuseALineBreakInsideQuotes_When_NoColumnTakesOneAndCountRowsAsRecords()
    {
        var (table, error) = await ReadAsync($"{DeviceHeader}\n{Device(model: "\"two\nlines\"")}\n\n{Device("S02", height: "x")}\n");

        error.Should().BeNull();
        table.Rows.Should().Be(2, "a blank line is no row");
        table.Errors.Listed.Should().Equal(new Ariva.Core.Domain.ViewModels.CsvRowErrorViewModel(2, 3, CsvImportErrors.ControlCharacter),
            new Ariva.Core.Domain.ViewModels.CsvRowErrorViewModel(4, 11, CsvImportErrors.NotANumber));
    }

    [Fact]
    public async Task ReadAsync_Should_RefuseTheRowAndReadTheNext_When_ARowHasBadQuotesOrTheWrongCellCount()
    {
        var (table, error) = await ReadAsync($"{DeviceHeader}\n{Device(model: "PC\"2")}\n{Device()},extra\nS03,StereoVision\n{Device("S04")}\n");

        error.Should().BeNull();
        table.Errors.Listed.Select(e => (e.Row, e.Column, e.Reason)).Should().Equal((2, null, CsvImportErrors.BadQuotes), (3, null, CsvImportErrors.WrongCellCount),
            (4, null, CsvImportErrors.WrongCellCount));
        table.Valid.Should().ContainSingle().Which.Text["code"].Should().Be("S04");
    }

    #endregion

    #region The file as a whole

    public static TheoryData<string, string> Headers => new()
    {
        { "code,family,model,transport,dialect,clockSource,terminal,level,x,y,mountingHeightMetres,orientationDegrees", "a required column missing" },
        { DeviceHeader + ",colour", "an unknown column" },
        { DeviceHeader + ",code", "a column twice" },
        { DeviceHeader.Replace("code", "Code", StringComparison.Ordinal), "a name in another case" },
        { DeviceHeader.Replace("model", " model", StringComparison.Ordinal), "a name with a space" },
        { DeviceHeader.Replace("model", "=model", StringComparison.Ordinal), "a formula for a name" }
    };

    [Theory]
    [MemberData(nameof(Headers))]
    public async Task ReadAsync_Should_RefuseTheFile_When_TheHeaderIsNotTheSchemas(string header, string because)
    {
        (await ReadAsync($"{header}\n{Device()}\n")).Error.Should().Be(CsvImportErrors.DeviceHeaderNotValid, because);
    }

    [Fact]
    public async Task ReadAsync_Should_RefuseTheFile_When_ItIsEmptyHasNoRowsOrIsNotUtf8()
    {
        (await ReadAsync(string.Empty)).Error.Should().Be(CsvImportErrors.NoHeader);
        (await ReadAsync($"{DeviceHeader}\n\n")).Error.Should().Be(CsvImportErrors.NoRows);
        (await ReadBytesAsync([0xFF, 0xFE, .. Encoding.Unicode.GetBytes($"{DeviceHeader}\n{Device()}\n")])).Error.Should().Be(CsvImportErrors.NotUtf8, "UTF-16 is refused");
        (await ReadBytesAsync([.. Encoding.UTF8.GetBytes($"{DeviceHeader}\n"), 0xC3, 0x28, .. Encoding.UTF8.GetBytes(",x\n")])).Error
            .Should().Be(CsvImportErrors.NotUtf8, "a byte sequence that is not UTF-8");
    }

    [Fact]
    public async Task ReadAsync_Should_TakeEachKindAtItsRowBoundAndRefuseOneMore_When_Counted()
    {
        string Devices(int n) => DeviceHeader + "\n" + string.Join('\n', Enumerable.Range(1, n).Select(i => Device($"S{i}")));
        (await ReadAsync(Devices(CsvImportLimits.MaxDeviceRows))).Table.Valid.Should().HaveCount(CsvImportLimits.MaxDeviceRows);
        (await ReadAsync(Devices(CsvImportLimits.MaxDeviceRows + 1))).Error.Should().Be(CsvImportErrors.TooManyDeviceRows);

        string Mappings(int n) => "externalSystem,externalCode,checkpoint,desk\n" + string.Join('\n', Enumerable.Range(1, n).Select(i => $"Aman,IMM-{i},IMM,D{i}"));
        (await ReadAsync(Mappings(CsvImportLimits.MaxMappingRows), MappingImportRows.Schema)).Table.Valid.Should().HaveCount(CsvImportLimits.MaxMappingRows);
        (await ReadAsync(Mappings(CsvImportLimits.MaxMappingRows + 1), MappingImportRows.Schema)).Error.Should().Be(CsvImportErrors.TooManyMappingRows);
    }

    [Fact]
    public async Task ReadAsync_Should_TakeACellOf200CharactersAndRefuseLongerOnes_When_Read()
    {
        var (table, _) = await ReadAsync($"{DeviceHeader}\n{Device(zone: new string('z', 200))}\n{Device("S02", zone: new string('z', 201))}\n");
        table.Valid.Should().ContainSingle().Which.Text["queueZone"].Should().HaveLength(200);
        table.Errors.Listed.Should().Equal(new Ariva.Core.Domain.ViewModels.CsvRowErrorViewModel(3, 13, CsvImportErrors.TooLong));

        (await ReadAsync($"{DeviceHeader}\n{Device(zone: new string('z', CsvImportLimits.MaxParserFieldSize))}\n")).Table.Errors.Listed
            .Should().ContainSingle().Which.Reason.Should().Be(CsvImportErrors.TooLong, "the parser's own bound is the file's limit only beyond 800");
        (await ReadAsync($"{DeviceHeader}\n{Device(zone: new string('z', CsvImportLimits.MaxParserFieldSize + 1))}\n")).Error
            .Should().Be(CsvImportErrors.CellBeyondParserBound);
    }

    [Fact]
    public async Task Sha256Async_Should_HashAFileOf1MbAndRefuseOneByteMore_When_Read()
    {
        using var atBound = new MemoryStream(new byte[CsvImportLimits.MaxFileBytes]);
        using var beyond = new MemoryStream(new byte[CsvImportLimits.MaxFileBytes + 1]);

        (await CsvImportReader.Sha256Async(atBound, Ct)).Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[CsvImportLimits.MaxFileBytes])));
        (await CsvImportReader.Sha256Async(beyond, Ct)).Should().BeNull();
    }

    #endregion

    #region Cells: formulas, numbers and control characters

    public static TheoryData<string, string, string> Cells => new()
    {
        // A text column (the model).
        { "model", "=HYPERLINK(\"http://x\")", CsvImportErrors.FormulaPrefix },
        { "model", "+SUM(A1)", CsvImportErrors.FormulaPrefix },
        { "model", "-2+3", CsvImportErrors.FormulaPrefix },
        { "model", "@SUM(A1)", CsvImportErrors.FormulaPrefix },
        { "model", "\tcmd", CsvImportErrors.FormulaPrefix },
        { "model", "\rcmd", CsvImportErrors.FormulaPrefix },
        { "model", "  =1+1", CsvImportErrors.FormulaPrefix },
        { "model", "PC2\u0007SE", CsvImportErrors.ControlCharacter },
        { "model", "PC2\0SE", CsvImportErrors.ControlCharacter },
        { "model", "PC2 = SE", null },
        { "model", "<script>alert(1)</script>", null },
        { "code", " ", CsvImportErrors.Required },
        // A number column that takes -1 (x): a minus only as a sign.
        { "x", "-1", null },
        { "x", "-0.5", null },
        { "x", "2001", null },
        { "x", "-1.5", CsvImportErrors.OutOfRange },
        { "x", "2001.1", CsvImportErrors.OutOfRange },
        { "x", "+1", CsvImportErrors.FormulaPrefix },
        { "x", "=1", CsvImportErrors.FormulaPrefix },
        { "x", "@1", CsvImportErrors.FormulaPrefix },
        { "x", "-A1", CsvImportErrors.FormulaPrefix },
        { "x", "--1", CsvImportErrors.FormulaPrefix },
        { "x", "1e3", CsvImportErrors.NotANumber },
        { "x", " 12", CsvImportErrors.NotANumber },
        { "x", "12 ", CsvImportErrors.NotANumber },
        { "x", "1,5", CsvImportErrors.NotANumber },
        { "x", "NaN", CsvImportErrors.NotANumber },
        { "x", "Infinity", CsvImportErrors.NotANumber },
        { "x", "0x10", CsvImportErrors.NotANumber },
        { "x", "", CsvImportErrors.Required },
        // A number column that takes no negative (orientation).
        { "orientationDegrees", "-1", CsvImportErrors.OutOfRange },
        { "orientationDegrees", "360", null },
        { "orientationDegrees", "360.5", CsvImportErrors.OutOfRange },
        { "mountingHeightMetres", "100.5", CsvImportErrors.OutOfRange }
    };

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task ReadAsync_Should_JudgeEachCellByItsColumn_When_ItCouldBeAFormulaOrANumber(string column, string cell, string expected)
    {
        var row = column switch
        {
            "model" => Device(model: Quoted(cell)),
            "code" => Device(code: Quoted(cell)),
            "x" => Device(x: Quoted(cell)),
            "orientationDegrees" => Device(orientation: Quoted(cell)),
            _ => Device(height: Quoted(cell))
        };

        var (table, error) = await ReadAsync($"{DeviceHeader}\n{row}\n");

        error.Should().BeNull();
        if (expected is null)
        {
            table.Errors.Count.Should().Be(0, $"{column} {cell}");
            return;
        }

        var refusal = table.Errors.Listed.Should().ContainSingle().Subject;
        refusal.Reason.Should().Be(expected, $"{column} {cell}");
        refusal.Row.Should().Be(2);
        refusal.Column.Should().Be(table.Column(column));
    }

    /// <summary>A cell as a CSV writer would write it: quoted when it holds a comma, quote, line break or edge space.</summary>
    private static string Quoted(string cell) =>
        cell.Length > 0 && (cell.IndexOfAny([',', '"', '\n', '\r']) >= 0 || cell[0] == ' ' || cell[^1] == ' ' || cell[0] == '\t')
            ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : cell;

    [Fact]
    public async Task ReadAsync_Should_ListAHundredRefusalsCountThemAllAndEchoNoCell_When_ManyCellsAreRefused()
    {
        const string hostile = "=cmd|' /C calc'!A0";
        var rows = Enumerable.Range(1, 150).Select(i => Device($"S{i}", model: Quoted(hostile)));

        var (table, _) = await ReadAsync($"{DeviceHeader}\n{string.Join('\n', rows)}\n");

        table.Errors.Count.Should().Be(150);
        table.Errors.Listed.Should().HaveCount(CsvImportLimits.MaxErrorsListed);
        table.Errors.Listed.Should().OnlyContain(e => FixedTexts.Contains(e.Reason), "every reason is a fixed text");
        table.Errors.Listed.Select(e => e.Reason).Should().NotContain(r => r.Contains("calc", StringComparison.Ordinal));
    }

    #endregion
}
