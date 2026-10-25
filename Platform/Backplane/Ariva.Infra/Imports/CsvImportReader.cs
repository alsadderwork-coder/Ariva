using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Imports;
using CsvHelper;
using CsvHelper.Configuration;

namespace Ariva.Infra.Imports;

/// <summary>What a column holds: text, or a number within a range.</summary>
internal enum CsvCellKind
{
    Text,
    Number
}

/// <summary>A column of an import: its exact header name, whether a row must fill it, its kind and, for a number, its range.</summary>
internal sealed record CsvColumn(string Name, bool Required, CsvCellKind Kind = CsvCellKind.Text, double Min = 0, double Max = 0);

/// <summary>The columns of one kind of import, its row bound and its file-level texts.</summary>
internal sealed record CsvSchema(IReadOnlyList<CsvColumn> Columns, int MaxRows, string HeaderNotValid, string TooManyRows);

/// <summary>A row whose every cell passed the generic checks: text cells as given (an empty cell is null), numbers parsed.</summary>
internal sealed record CsvRow(int Row, IReadOnlyDictionary<string, string> Text, IReadOnlyDictionary<string, double?> Numbers);

/// <summary>
/// The refused cells of an import, all counted and kept (a file holds at most 10,001 rows of at most 17 cells), listed by
/// row and column, the first <see cref="CsvImportLimits.MaxErrorsListed"/> of them: the reader's, the rows' and the
/// services' refusals come at different times, and the list reads as the file does.
/// </summary>
internal sealed class CsvErrors
{
    private readonly List<CsvRowErrorViewModel> _all = [];

    public IReadOnlyList<CsvRowErrorViewModel> Listed => [.. _all.OrderBy(e => e.Row).ThenBy(e => e.Column ?? 0).Take(CsvImportLimits.MaxErrorsListed)];
    public int Count => _all.Count;

    public void Add(int row, int? column, string reason) => _all.Add(new CsvRowErrorViewModel(row, column, reason));
}

/// <summary>A file read against a schema: the data rows it had, the rows that passed, the refused cells and each column's number.</summary>
internal sealed record CsvTable(int Rows, IReadOnlyList<CsvRow> Valid, CsvErrors Errors, IReadOnlyDictionary<string, int> ColumnNumbers)
{
    /// <summary>The column number of a schema column (1 for the first header cell).</summary>
    public int Column(string name) => ColumnNumbers[name];

    /// <summary>
    /// The column number of a refusal's column, or null when it names none or a column the file does not carry (an optional
    /// one left out of the header, ARV-155): never 0, which is no column of the file.
    /// </summary>
    public int? ColumnOrNone(string name) => name is not null && ColumnNumbers.TryGetValue(name, out var number) ? number : null;
}

/// <summary>
/// Reads a CSV import file (ARV-123a, CWE-120, CWE-501, CWE-79) as a stream with CsvHelper's low-level
/// <see cref="CsvParser"/> only (no record mapping, dynamic types or class maps): RFC 4180 quoting (a quoted cell may hold
/// commas, quotes and line breaks), CRLF or LF, UTF-8 with or without a byte order mark (UTF-16 and UTF-32 refused). The
/// header names every column once by its exact name; each data row has one cell per header column. A cell is at most 200
/// characters and holds no control character (a line break inside quotes is CSV, but no column takes one); a text cell
/// may not start with =, +, -, @, a tab or a carriage return (a formula to a spreadsheet: CSV injection); a number is
/// digits with an optional leading minus and decimal point, nothing else (no +, exponent, spaces, NaN or infinity), within
/// its column's range, so a minus is only ever a sign. Refusals name a row and a column and a fixed text, never the cell.
/// </summary>
internal static partial class CsvImportReader
{
    /// <summary>The characters that make a spreadsheet read a cell as a formula (OWASP CSV injection).</summary>
    private const string FormulaPrefixes = "=+-@\t\r";

    /// <summary>The file's SHA-256 (lower-case hex), or null when it is longer than <see cref="CsvImportLimits.MaxFileBytes"/>.</summary>
    public static async Task<string> Sha256Async(Stream input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > CsvImportLimits.MaxFileBytes)
                return null;
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// The rows of the file against the schema, or the reason the file as a whole is refused (not UTF-8, no header, a header
    /// that is not the schema's, no rows, more rows than the bound, a cell beyond the parser's bound). The caller has
    /// already bounded the file's size (<see cref="Sha256Async"/>).
    /// </summary>
    public static async Task<(CsvTable Table, string Error)> ReadAsync(Stream input, CsvSchema schema, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(schema);
        var badData = false;
        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = ",",
            DetectDelimiter = false,
            Quote = '"',
            Mode = CsvMode.RFC4180,
            AllowComments = false,
            IgnoreBlankLines = false,
            TrimOptions = TrimOptions.None,
            LineBreakInQuotedFieldIsBadData = false,
            MaxFieldSize = CsvImportLimits.MaxParserFieldSize,
            // A parser exception never carries the file's text (CWE-532, CWE-79).
            ExceptionMessagesContainRawData = false,
            CacheFields = false,
            CountBytes = false,
            // Bad quoting is never ignored: it refuses the row it is on (the parser carries on with the next one).
            BadDataFound = _ => badData = true
        };

        // UTF-8 without decoding errors; the byte order mark is detected here, and one for UTF-16 or UTF-32 refuses the file.
        using var reader = new StreamReader(input, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: true);
        using var parser = new CsvParser(reader, configuration, leaveOpen: true);
        var errors = new CsvErrors();
        var valid = new List<CsvRow>();
        Dictionary<string, int> columns = null;
        CsvColumn[] byPosition = null;
        var row = 0;
        var dataRows = 0;
        try
        {
            while (await parser.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.CurrentEncoding is not UTF8Encoding)
                    return (null, CsvImportErrors.NotUtf8);
                row++;
                var cells = parser.Record ?? [];
                var rowHasBadData = badData;
                badData = false;
                if (columns is null)
                {
                    (columns, byPosition) = Header(cells, schema);
                    if (columns is null || rowHasBadData)
                        return (null, schema.HeaderNotValid);
                    continue;
                }

                // A blank line is no row (it still counts for the row numbers, as a spreadsheet shows them).
                if (cells.Length == 1 && cells[0].Length == 0 && !rowHasBadData)
                    continue;
                if (++dataRows > schema.MaxRows)
                    return (null, schema.TooManyRows);
                if (rowHasBadData)
                {
                    errors.Add(row, null, CsvImportErrors.BadQuotes);
                    continue;
                }

                if (cells.Length != byPosition.Length)
                {
                    errors.Add(row, null, CsvImportErrors.WrongCellCount);
                    continue;
                }

                if (Row(row, cells, byPosition, errors) is { } checkedRow)
                    valid.Add(checkedRow);
            }
        }
        catch (DecoderFallbackException)
        {
            return (null, CsvImportErrors.NotUtf8);
        }
        catch (MaxFieldSizeException)
        {
            return (null, CsvImportErrors.CellBeyondParserBound);
        }

        if (columns is null)
            return (null, CsvImportErrors.NoHeader);
        return dataRows == 0 ? (null, CsvImportErrors.NoRows) : (new CsvTable(dataRows, valid, errors, columns), null);
    }

    /// <summary>The header's columns by name and position, or null when a name is unknown, twice, or a required one missing.</summary>
    private static (Dictionary<string, int> Columns, CsvColumn[] ByPosition) Header(string[] cells, CsvSchema schema)
    {
        var known = schema.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        var byPosition = new CsvColumn[cells.Length];
        for (var index = 0; index < cells.Length; index++)
        {
            if (!known.TryGetValue(cells[index], out var column) || !columns.TryAdd(column.Name, index + 1))
                return (null, null);
            byPosition[index] = column;
        }

        return schema.Columns.Any(c => c.Required && !columns.ContainsKey(c.Name)) ? (null, null) : (columns, byPosition);
    }

    /// <summary>The row with its cells checked, or null after adding each refused cell.</summary>
    private static CsvRow Row(int row, string[] cells, CsvColumn[] byPosition, CsvErrors errors)
    {
        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        var numbers = new Dictionary<string, double?>(StringComparer.Ordinal);
        var refused = false;
        for (var index = 0; index < cells.Length; index++)
        {
            var column = byPosition[index];
            var (value, reason) = Cell(cells[index], column);
            if (reason is not null)
            {
                errors.Add(row, index + 1, reason);
                refused = true;
                continue;
            }

            if (column.Kind == CsvCellKind.Number)
                numbers[column.Name] = value is null ? null : double.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            else
                text[column.Name] = value;
        }

        return refused ? null : new CsvRow(row, text, numbers);
    }

    /// <summary>A cell's value (null when empty) or the fixed reason it is refused.</summary>
    internal static (string Value, string Reason) Cell(string cell, CsvColumn column)
    {
        if (cell.Length > CsvImportLimits.MaxCellLength)
            return (null, CsvImportErrors.TooLong);
        if (cell.Length == 0 || (string.IsNullOrWhiteSpace(cell) && !cell.Any(char.IsControl)))
            return column.Required ? (null, CsvImportErrors.Required) : (null, null);
        if (column.Kind == CsvCellKind.Number)
        {
            if (!Number().IsMatch(cell))
                return (null, StartsLikeAFormula(cell) ? CsvImportErrors.FormulaPrefix : CsvImportErrors.NotANumber);
            var number = double.Parse(cell, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            return number < column.Min || number > column.Max ? (null, CsvImportErrors.OutOfRange) : (cell, null);
        }

        if (StartsLikeAFormula(cell))
            return (null, CsvImportErrors.FormulaPrefix);
        return cell.Any(char.IsControl) ? (null, CsvImportErrors.ControlCharacter) : (cell, null);
    }

    /// <summary>A formula prefix at the start, or after leading spaces (some spreadsheets trim them).</summary>
    private static bool StartsLikeAFormula(string cell) =>
        FormulaPrefixes.Contains(cell[0], StringComparison.Ordinal) || (cell.TrimStart(' ') is { Length: > 0 } trimmed && FormulaPrefixes.Contains(trimmed[0], StringComparison.Ordinal));

    /// <summary>
    /// Digits with an optional leading minus and an optional decimal part: nothing a spreadsheet or a parser reads otherwise.
    /// Linear (NonBacktracking), so no timeout that a loaded node could fire (ARV-163).
    /// </summary>
    [GeneratedRegex("^-?[0-9]{1,9}(\\.[0-9]{1,9})?\\z", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, Timeout.Infinite)]
    internal static partial Regex Number();
}
