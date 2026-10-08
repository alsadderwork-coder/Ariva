namespace Ariva.Infra.Timescale;

/// <summary>
/// Marks the host that runs the validation service (ARV-104g1): Ariva.Api.Main registers it (AddArivaValidationReaderHost),
/// and the start-up guard lets no other host hold the validation reader login outside vm-local (CWE-863).
/// </summary>
internal sealed class ValidationReaderHost;
