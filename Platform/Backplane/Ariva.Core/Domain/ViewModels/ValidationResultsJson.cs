namespace Ariva.Core.Domain.ViewModels;

/// <summary>
/// A campaign's validation results as one caller may read them (ARV-104g): the JSON of the per-caller projection of
/// <see cref="ValidationResultsViewModel"/> (<see cref="ValidationResultsViewModel.For"/>), written by the results type itself in
/// its document format, so that the API serves exactly those bytes and no other type holds the results (the shadow exposure
/// tests of the Architecture suite). <see cref="Revision"/> is the frozen revision served (null for a campaign not closed) and
/// <see cref="ContentSha256"/> its stored document's hash.
/// </summary>
public sealed record ValidationResultsJson(ReadOnlyMemory<byte> Utf8, int? Revision, string ContentSha256);
