using Ariva.Core.Domain.ViewModels;

namespace Ariva.Core.Services.Imports;

/// <summary>
/// The device list from the as-built pack as a CSV file (ARV-123a, wiki 07 section 2): a preview that checks every row as
/// the registration would (a dry run, rolled back) and, for a file without a refused row, a token bound to the file's
/// SHA-256, the site and the user for 15 minutes; then a commit of the same file with that token, which checks everything
/// again and registers every device through the registration path in one transaction (all or nothing), each in
/// Commissioning with a new credential returned once. Both are critical actions (registration's permission), scoped to the
/// caller's sites (404 outside) and audited with the file's SHA-256, the counts and the outcome, never a cell.
/// </summary>
public interface ISvcDeviceImports : ISvcScoped
{
    /// <summary>Whether the site exists and the caller may reach it; the controller answers 404 before anything about the upload.</summary>
    Task<bool> SiteReachableAsync(string siteCode, CancellationToken ct = default);

    /// <summary>Checks the file (opened twice: hashed, then parsed) and answers each refused row by row, column and fixed text.</summary>
    Task<Result<DeviceImportPreviewViewModel>> PreviewAsync(string siteCode, Func<Stream> openFile, CancellationToken ct = default);

    /// <summary>Registers every device of the previewed file, or none; the token is checked against the file's hash before anything is parsed.</summary>
    Task<Result<DeviceImportCommitViewModel>> CommitAsync(string siteCode, Func<Stream> openFile, string previewToken, CancellationToken ct = default);
}

/// <summary>
/// Desk code mappings (AMAN desk and gate codes, AODB counter codes) as a CSV file (ARV-123a): preview and commit as for
/// devices, through the desk code mapping service's own rules. Not a critical action, like the single mapping endpoint.
/// </summary>
public interface ISvcDeskCodeMappingImports : ISvcScoped
{
    Task<bool> SiteReachableAsync(string siteCode, CancellationToken ct = default);

    Task<Result<DeskCodeMappingImportPreviewViewModel>> PreviewAsync(string siteCode, Func<Stream> openFile, CancellationToken ct = default);

    Task<Result<DeskCodeMappingImportCommitViewModel>> CommitAsync(string siteCode, Func<Stream> openFile, string previewToken, CancellationToken ct = default);
}

/// <summary>The bounds of a CSV import (ARV-123a, CWE-120), within the file-import class.</summary>
public static class CsvImportLimits
{
    /// <summary>A file is at most 1 MB.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>A cell is at most 200 characters (the longest a column takes: names, zone names).</summary>
    public const int MaxCellLength = 200;

    /// <summary>The parser's own hard bound on a field (CsvHelper MaxFieldSize): beyond it the whole file is refused.</summary>
    public const int MaxParserFieldSize = 4 * MaxCellLength;

    /// <summary>Devices per file: every one is registered in one transaction (measured in DeviceImportTests).</summary>
    public const int MaxDeviceRows = 500;

    /// <summary>Desk code mappings per file: a site's bound (SiteConfigurationLimits.MaxDeskCodeMappings).</summary>
    public const int MaxMappingRows = 10_000;

    /// <summary>Refused cells listed in an answer; the answer also gives the total.</summary>
    public const int MaxErrorsListed = 100;

    /// <summary>How long a preview's token may be used for its commit.</summary>
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(15);

    /// <summary>The longest token a commit takes (a Data Protection payload in base64url).</summary>
    public const int MaxTokenLength = 2048;
}

/// <summary>
/// The fixed texts of the CSV imports (CWE-79, CWE-501): none repeats a cell, a header name from the file or a value the
/// caller sent; rows and columns are given as numbers (the header is row 1, columns count from 1).
/// </summary>
public static class CsvImportErrors
{
    // The file as a whole.
    public const string FileRequired = "A CSV file is required in the form field \"file\".";
    public const string FileTooLarge = "A CSV import file is at most 1 MB.";
    public const string NotUtf8 = "The file is not UTF-8 text (a byte order mark is optional; UTF-16 and UTF-32 are refused).";
    public const string NoHeader = "The file has no header row.";
    public const string NoRows = "The file has no rows after its header.";
    public const string CellBeyondParserBound = "A cell of the file is far longer than any column takes, so the file is refused.";
    public const string DeviceHeaderNotValid =
        "The header names each column once, by its exact name: code, family, model, transport, dialect, clockSource, terminal, level, x, y, mountingHeightMetres, orientationDegrees and queueZone, and optionally mappingName, footprintLengthMetres, footprintWidthMetres and footprintRadiusMetres.";
    public const string MappingHeaderNotValid = "The header names each column once, by its exact name: externalSystem, externalCode, checkpoint and desk.";
    public const string TooManyDeviceRows = "A device import takes at most 500 rows.";
    public const string TooManyMappingRows = "A desk code mapping import takes at most 10,000 rows.";
    public const string PreviewTokenNotValid = "previewToken is the token the preview gave.";
    public const string NotThePreview = "The file, the site or the user is not the preview's, or the preview is older than 15 minutes: preview the file again.";
    public const string RowsRefused = "The file no longer passes its checks: preview it again to see the refused rows.";

    // A cell or a row.
    public const string WrongCellCount = "The row does not have one cell for each column of the header.";
    public const string BadQuotes = "The row is not valid CSV: a quote inside a cell that is not quoted, or text after a closing quote.";
    public const string Required = "This cell is required.";
    public const string TooLong = "A cell is at most 200 characters.";
    public const string FormulaPrefix = "A cell may not start with =, +, -, @, a tab or a carriage return (a spreadsheet would run it as a formula); a minus is read only as the sign of a number.";
    public const string ControlCharacter = "A cell may not hold a line break or another control character.";
    public const string NotANumber = "A number is digits with an optional leading minus sign and an optional decimal point, such as -0.5 or 12.";
    public const string OutOfRange = "The number is outside its column's range: x and y from -1 to 2001 m, mounting height 0 to 100 m (2 to 20 m to register), orientation 0 to 360 degrees, footprints 0 to 1000 m.";
    public const string Code = "A code is 1 to 16 upper case letters or digits, with single hyphens inside.";
    public const string DeviceCodeRepeated = "The device code appears on an earlier row of the file.";
    public const string LevelNotFound = "The site has no live level with this terminal and level code.";
    public const string LevelAmbiguous = "The site has two terminals with this code at different airports, so the level cannot be told apart.";
    public const string RegistrationRefused = "The registration refuses this row (for example a mounting height outside 2 to 20 m, a position outside the level, or a model with an invisible character).";
    public const string UnknownSystem = "Unknown external system: Aman or Aodb.";
    public const string ExternalCode = "An external code is 1 to 32 letters, digits or . _ / - characters.";
    public const string MappingTaken = "A live mapping of this system already uses the external code, or the desk, at the site.";
    public const string MappingRepeated = "The external code, or the desk, appears on an earlier row of the file for the same system.";
    public const string DeskNotFound = "The site has no live desk with this checkpoint and desk code.";
    public const string MappingDoesNotFitDesk = "An Aman code maps an immigration desk or e-gate, an Aodb code a check-in counter.";
    public const string MappingRefused = "The desk code mapping service refuses this row.";

    /// <summary>A commit refused at a row (the row's number and its fixed reason; nothing from the file).</summary>
    public static string AtRow(int row, string reason) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Row {row}: {reason}");

    /// <summary>The note of a device commit.</summary>
    public const string DevicesArriveCommissioning = "Devices arrived in Commissioning, each with a new credential shown this once: install it on the device and calibrate it.";
}
