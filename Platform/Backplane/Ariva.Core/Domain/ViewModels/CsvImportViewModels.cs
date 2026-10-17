namespace Ariva.Core.Domain.ViewModels;

/// <summary>A refused cell or row of a CSV import (ARV-123a): its row (the header is row 1), its column (from 1; none for the whole row) and a fixed text.</summary>
public sealed record CsvRowErrorViewModel(int Row, int? Column, string Reason);

/// <summary>A device row that passed every check of the preview, by the values it will be registered with.</summary>
public sealed record DeviceImportRowViewModel(int Row, string Code, string Family, string Model, string Terminal, string Level, string QueueZone);

/// <summary>
/// A device import's preview: the file's SHA-256, how many rows it had, the rows that pass, the refused cells (the first
/// 100, with the total) and, only when nothing was refused, the token its commit needs and when that token expires.
/// </summary>
public sealed record DeviceImportPreviewViewModel(
    string FileSha256,
    int Rows,
    IReadOnlyList<DeviceImportRowViewModel> Devices,
    IReadOnlyList<CsvRowErrorViewModel> Errors,
    int ErrorCount,
    string PreviewToken,
    DateTime? ExpiresUtc);

/// <summary>A registered device's new credential, shown this once (only its prefix and hash are kept).</summary>
public sealed record ImportedDeviceCredentialViewModel(string Code, string Credential);

/// <summary>A device import's commit: the file's SHA-256, the devices registered and their credentials. Never cached.</summary>
public sealed record DeviceImportCommitViewModel(string FileSha256, int Devices, IReadOnlyList<ImportedDeviceCredentialViewModel> Credentials, IReadOnlyList<string> Notes);

/// <summary>A desk code mapping row that passed every check of the preview.</summary>
public sealed record DeskCodeMappingImportRowViewModel(int Row, string System, string ExternalCode, string Checkpoint, string Desk);

/// <summary>A desk code mapping import's preview, as for devices.</summary>
public sealed record DeskCodeMappingImportPreviewViewModel(
    string FileSha256,
    int Rows,
    IReadOnlyList<DeskCodeMappingImportRowViewModel> Mappings,
    IReadOnlyList<CsvRowErrorViewModel> Errors,
    int ErrorCount,
    string PreviewToken,
    DateTime? ExpiresUtc);

/// <summary>A desk code mapping import's commit: the file's SHA-256 and the mappings created.</summary>
public sealed record DeskCodeMappingImportCommitViewModel(string FileSha256, int Mappings);
