using Ariva.Core.Domain.Enums;
using Ariva.Core.Services.Imports;
using Ariva.Core.Services.Topology;

namespace Ariva.Infra.Imports;

/// <summary>A device row as the registration takes it, with its level by terminal and level code.</summary>
internal sealed record DeviceImportRow(
    int Row,
    string Code,
    string Family,
    string Model,
    string Transport,
    string Dialect,
    string ClockSource,
    string MappingName,
    string Terminal,
    string Level,
    double X,
    double Y,
    double MountingHeightMetres,
    double OrientationDegrees,
    string QueueZone,
    double? FootprintLengthMetres,
    double? FootprintWidthMetres,
    double? FootprintRadiusMetres);

/// <summary>A desk code mapping row, its desk by checkpoint and desk code.</summary>
internal sealed record MappingImportRow(int Row, string System, string ExternalCode, string Checkpoint, string Desk);

/// <summary>
/// The columns of the device import (ARV-123a): the registration request's fields, the level by terminal and level code;
/// number ranges are the registration request's (the entity then applies its own, such as 2 to 20 m of mounting height).
/// </summary>
internal static class DeviceImportRows
{
    public static readonly CsvSchema Schema = new(
    [
        new("code", true), new("family", true), new("model", true), new("transport", true), new("dialect", true), new("clockSource", true),
        new("mappingName", false), new("terminal", true), new("level", true),
        new("x", true, CsvCellKind.Number, -1, 2001), new("y", true, CsvCellKind.Number, -1, 2001),
        new("mountingHeightMetres", true, CsvCellKind.Number, 0, 100), new("orientationDegrees", true, CsvCellKind.Number, 0, 360),
        new("queueZone", true),
        new("footprintLengthMetres", false, CsvCellKind.Number, 0, 1000), new("footprintWidthMetres", false, CsvCellKind.Number, 0, 1000),
        new("footprintRadiusMetres", false, CsvCellKind.Number, 0, 1000)
    ], CsvImportLimits.MaxDeviceRows, CsvImportErrors.DeviceHeaderNotValid, CsvImportErrors.TooManyDeviceRows);

    /// <summary>
    /// The rows whose codes and kinds the registration would read (a code by the topology pattern, kinds by their exact
    /// names), each device code once in the file; every other row's refused cells are added to the table's errors.
    /// </summary>
    public static IReadOnlyList<DeviceImportRow> Check(CsvTable table)
    {
        var rows = new List<DeviceImportRow>();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in table.Valid)
        {
            var refused = false;
            void Refuse(string column, string reason)
            {
                table.Errors.Add(row.Row, table.Column(column), reason);
                refused = true;
            }

            string Text(string column) => row.Text.GetValueOrDefault(column);
            if (!TopologyCodes.IsValid(Text("code")))
                Refuse("code", CsvImportErrors.Code);
            else if (!codes.Add(Text("code")))
                Refuse("code", CsvImportErrors.DeviceCodeRepeated);
            if (!SiteConfigurationRules.IsKind<DeviceFamily>(Text("family")))
                Refuse("family", Ariva.Core.Services.Sensing.DeviceErrors.UnknownFamily);
            if (!SiteConfigurationRules.IsKind<DeviceTransport>(Text("transport")))
                Refuse("transport", Ariva.Core.Services.Sensing.DeviceErrors.UnknownTransport);
            if (!SiteConfigurationRules.IsKind<DeviceDialect>(Text("dialect")))
                Refuse("dialect", Ariva.Core.Services.Sensing.DeviceErrors.UnknownDialect);
            if (!SiteConfigurationRules.IsKind<ClockSource>(Text("clockSource")))
                Refuse("clockSource", Ariva.Core.Services.Sensing.DeviceErrors.UnknownClockSource);
            if (!TopologyCodes.IsValid(Text("terminal")))
                Refuse("terminal", CsvImportErrors.Code);
            if (!TopologyCodes.IsValid(Text("level")))
                Refuse("level", CsvImportErrors.Code);
            if (refused)
                continue;

            rows.Add(new DeviceImportRow(row.Row, Text("code"), Text("family"), Text("model"), Text("transport"), Text("dialect"), Text("clockSource"),
                Text("mappingName"), Text("terminal"), Text("level"), row.Numbers["x"].GetValueOrDefault(), row.Numbers["y"].GetValueOrDefault(), row.Numbers["mountingHeightMetres"].GetValueOrDefault(),
                row.Numbers["orientationDegrees"].GetValueOrDefault(), Text("queueZone"), row.Numbers.GetValueOrDefault("footprintLengthMetres"),
                row.Numbers.GetValueOrDefault("footprintWidthMetres"), row.Numbers.GetValueOrDefault("footprintRadiusMetres")));
        }

        return rows;
    }
}

/// <summary>The columns of the desk code mapping import (ARV-123a): the mapping request's fields, the desk by checkpoint and desk code.</summary>
internal static class MappingImportRows
{
    public static readonly CsvSchema Schema = new(
        [new("externalSystem", true), new("externalCode", true), new("checkpoint", true), new("desk", true)],
        CsvImportLimits.MaxMappingRows, CsvImportErrors.MappingHeaderNotValid, CsvImportErrors.TooManyMappingRows);

    /// <summary>
    /// The rows the mapping service would read (the system by its exact name, the external code by the mapping's shape,
    /// checkpoint and desk codes by the topology pattern), each code and each desk once per system in the file.
    /// </summary>
    public static IReadOnlyList<MappingImportRow> Check(CsvTable table)
    {
        var rows = new List<MappingImportRow>();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var desks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in table.Valid)
        {
            var refused = false;
            void Refuse(string column, string reason)
            {
                table.Errors.Add(row.Row, table.Column(column), reason);
                refused = true;
            }

            string Text(string column) => row.Text.GetValueOrDefault(column);
            var system = Text("externalSystem");
            var code = Ariva.Core.Domain.Entities.DeskCodeMapping.NormalizeCode(Text("externalCode"));
            if (!SiteConfigurationRules.IsKind<ExternalSystem>(system))
                Refuse("externalSystem", CsvImportErrors.UnknownSystem);
            if (code is null)
                Refuse("externalCode", CsvImportErrors.ExternalCode);
            if (!TopologyCodes.IsValid(Text("checkpoint")))
                Refuse("checkpoint", CsvImportErrors.Code);
            if (!TopologyCodes.IsValid(Text("desk")))
                Refuse("desk", CsvImportErrors.Code);
            if (refused)
                continue;
            // One live mapping per system and code at a site, and one per system and desk (the service's rule).
            if (!codes.Add($"{system}|{code}") | !desks.Add($"{system}|{Text("checkpoint")}|{Text("desk")}"))
            {
                Refuse("externalCode", CsvImportErrors.MappingRepeated);
                continue;
            }

            rows.Add(new MappingImportRow(row.Row, system, code, Text("checkpoint"), Text("desk")));
        }

        return rows;
    }
}
