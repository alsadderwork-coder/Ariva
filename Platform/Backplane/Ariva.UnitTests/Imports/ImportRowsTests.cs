using System.Text;
using Ariva.Core.Services.Imports;
using Ariva.Core.Services.Sensing;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Imports;
using Ariva.Infra.Services.Sensing;
using Ariva.Infra.Services.Topology;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;

namespace Ariva.UnitTests.Imports;

/// <summary>
/// ARV-123a: the rows of each kind (codes by the topology pattern, kinds by exact names, each device code once, each
/// mapping code and desk once per system), the preview token's binding (file, site, user, kind, 15 minutes) and the
/// fixed texts that stand for the registration's and the mapping service's refusals.
/// </summary>
public sealed class ImportRowsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<CsvTable> TableAsync(string text, CsvSchema schema)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var (table, error) = await CsvImportReader.ReadAsync(stream, schema, Ct);
        error.Should().BeNull();
        return table;
    }

    #region Devices

    private const string DeviceHeader = "code,family,model,transport,dialect,clockSource,mappingName,terminal,level,x,y,mountingHeightMetres,orientationDegrees,queueZone,footprintLengthMetres,footprintWidthMetres";

    [Fact]
    public async Task Check_Should_KeepRowsTheRegistrationWouldRead_When_CodesAndKindsAreExact()
    {
        var table = await TableAsync($"""
            {DeviceHeader}
            S-01,StereoVision,PC2SE,HttpsPush,Xovis,Ntp,,T1,L0,20,16,5,0,Snake A,12,9
            s-02,StereoVision,PC2SE,HttpsPush,Xovis,Ntp,,T1,L0,20,16,5,0,Snake A,,
            S-03,stereovision,PC2SE,1,Xovis,Ntp, ,T1,L0,20,16,5,0,Snake A,,
            S-01,Lidar,Ouster,HttpsPush,Canonical,Ptp,,T1,L0,20,16,5,0,Snake A,,
            S-05,Lidar,Ouster,Mqtt,Declarative,Ptp,ouster-v2,t1,L-,20,16,5,0,Snake A,,
            """, DeviceImportRows.Schema);

        var rows = DeviceImportRows.Check(table);

        rows.Should().ContainSingle().Which.Should().Be(new DeviceImportRow(2, "S-01", "StereoVision", "PC2SE", "HttpsPush", "Xovis", "Ntp", null, "T1", "L0", 20, 16, 5, 0,
            "Snake A", 12, 9, null));
        table.Errors.Listed.Select(e => (e.Row, e.Column, e.Reason)).Should().Equal(
            (3, 1, CsvImportErrors.Code),
            (4, 2, DeviceErrors.UnknownFamily), (4, 4, DeviceErrors.UnknownTransport),
            (5, 1, CsvImportErrors.DeviceCodeRepeated),
            (6, 8, CsvImportErrors.Code), (6, 9, CsvImportErrors.Code));
    }

    [Fact]
    public void Refusal_Should_KeepTheRegistrationsFixedTextsAndHideEverythingElse_When_ARowIsRefused()
    {
        SvcDeviceImports.Refusal(DeviceErrors.OutOfReach).Should().Be(("x", DeviceErrors.OutOfReach));
        SvcDeviceImports.Refusal(DeviceErrors.ZoneNotFound).Should().Be(("queueZone", DeviceErrors.ZoneNotFound));
        SvcDeviceImports.Refusal(DeviceErrors.UnknownMapping).Should().Be(("mappingName", DeviceErrors.UnknownMapping));
        SvcDeviceImports.Refusal(DeviceErrors.UnsupportedTransport).Should().Be(("transport", DeviceErrors.UnsupportedTransport));
        SvcDeviceImports.Refusal(TopologyErrors.Duplicate).Should().Be(("code", TopologyErrors.Duplicate));
        SvcDeviceImports.Refusal(TopologyErrors.NotFound).Should().Be(("level", CsvImportErrors.LevelNotFound));
        // An entity's message may repeat a value from the file: it becomes one fixed text.
        SvcDeviceImports.Refusal("The mounting height is from 2 to 20 m. '<script>' was given.").Should().Be((null, CsvImportErrors.RegistrationRefused));
    }

    #endregion

    #region Desk code mappings

    [Fact]
    public async Task Check_Should_KeepEachCodeAndDeskOncePerSystem_When_MappingsAreRead()
    {
        var table = await TableAsync("""
            externalSystem,externalCode,checkpoint,desk
            Aman,imm-01,IMM,D01
            Aodb,IMM-01,CHK,C01
            Aman,IMM-01,IMM,D02
            Aman,IMM-02,IMM,D01
            aman,IMM-03,IMM,D03
            Aman,#1,IMM,D04
            Aman,IMM-05,imm,D05
            """, MappingImportRows.Schema);

        var rows = MappingImportRows.Check(table);

        rows.Select(r => (r.Row, r.System, r.ExternalCode, r.Checkpoint, r.Desk)).Should().Equal(
            (2, "Aman", "IMM-01", "IMM", "D01"), (3, "Aodb", "IMM-01", "CHK", "C01"));
        table.Errors.Listed.Select(e => (e.Row, e.Column, e.Reason)).Should().Equal(
            (4, 2, CsvImportErrors.MappingRepeated), (5, 2, CsvImportErrors.MappingRepeated),
            (6, 1, CsvImportErrors.UnknownSystem), (7, 2, CsvImportErrors.ExternalCode), (8, 3, CsvImportErrors.Code));
    }

    [Fact]
    public void Refusal_Should_KeepTheMappingServicesFixedTextsAndHideEverythingElse_When_ARowIsRefused()
    {
        SvcDeskCodeMappingImports.Refusal(TopologyErrors.Duplicate).Should().Be(("externalCode", CsvImportErrors.MappingTaken));
        SvcDeskCodeMappingImports.Refusal(TopologyErrors.NotFound).Should().Be(("desk", CsvImportErrors.DeskNotFound));
        SvcDeskCodeMappingImports.Refusal("A Aman code cannot map to a Counter.").Should().Be(("desk", CsvImportErrors.MappingDoesNotFitDesk));
        SvcDeskCodeMappingImports.Refusal("Something about <b>X1</b>").Should().Be(("desk", CsvImportErrors.MappingRefused));
    }

    #endregion

    #region The preview token

    private static readonly DateTime Now = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid User = Guid.CreateVersion7();
    private static readonly string Sha = new('a', 64);

    [Fact]
    public void Matches_Should_TakeOnlyTheFileSiteUserAndKindOfThePreview_When_ATokenComesBack()
    {
        var provider = new EphemeralDataProtectionProvider();
        var devices = ImportPreviewTokens.Devices(provider);
        var token = devices.Issue(Sha, "SITE", User, Now);

        devices.Matches(token, Sha, "SITE", User, Now).Should().BeTrue();
        devices.Matches(token, new string('b', 64), "SITE", User, Now).Should().BeFalse("another file");
        devices.Matches(token, Sha, "OTHER", User, Now).Should().BeFalse("another site");
        devices.Matches(token, Sha, "SITE", Guid.CreateVersion7(), Now).Should().BeFalse("another user");
        devices.Matches(token, Sha, "SITE", null, Now).Should().BeFalse("no user");
        ImportPreviewTokens.Mappings(provider).Matches(token, Sha, "SITE", User, Now).Should().BeFalse("a device token never opens a mapping commit");
        ImportPreviewTokens.Devices(new EphemeralDataProtectionProvider()).Matches(token, Sha, "SITE", User, Now).Should().BeFalse("another key ring");
        devices.Matches(token[..^2] + (token[^2] == 'A' ? "B" : "A") + token[^1], Sha, "SITE", User, Now).Should().BeFalse("an altered token");
        devices.Matches(new string('A', CsvImportLimits.MaxTokenLength + 1), Sha, "SITE", User, Now).Should().BeFalse("too long to be one");
        devices.Matches(null, Sha, "SITE", User, Now).Should().BeFalse();
    }

    [Fact]
    public void Matches_Should_TakeATokenForFifteenMinutesOnly_When_TimePasses()
    {
        var devices = ImportPreviewTokens.Devices(new EphemeralDataProtectionProvider());
        var token = devices.Issue(Sha, "SITE", User, Now);

        devices.Matches(token, Sha, "SITE", User, Now + CsvImportLimits.PreviewLifetime).Should().BeTrue("at the bound");
        devices.Matches(token, Sha, "SITE", User, Now + CsvImportLimits.PreviewLifetime + TimeSpan.FromTicks(1)).Should().BeFalse("one tick beyond");
        devices.Matches(token, Sha, "SITE", User, Now - TimeSpan.FromMinutes(5)).Should().BeTrue("another host's clock a little behind");
        devices.Matches(token, Sha, "SITE", User, Now - TimeSpan.FromMinutes(6)).Should().BeFalse("issued in the future");
    }

    #endregion
}
