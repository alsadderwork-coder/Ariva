namespace Ariva.UnitTests.Contracts;

/// <summary>
/// ADR-0010's data boundary as the tests check it on the wire (the AsyncAPI schemas, ARV-067, and the AMAN pacts, ARV-068):
/// no member of an AMAN record may be named like a person, officer, traveller or document identifier. LayeringTests checks
/// the same on the contract types with a shorter list.
/// </summary>
public static class DataBoundary
{
    /// <summary>Fragments of a member name (lower case) that would carry a person or a document across the boundary.</summary>
    public static readonly IReadOnlyList<string> Identifiers =
    [
        "officer", "badge", "staff", "employee", "user", "person", "traveller", "traveler", "passenger", "passport", "document",
        "visa", "name", "nationality", "birth", "gender", "sex", "email", "phone", "address", "biometric", "face", "finger", "photo"
    ];

    /// <summary>Counts whose names contain one of those fragments without being an identifier; a new one is added here on review.</summary>
    public static readonly IReadOnlyList<string> Aggregates = ["documentsprocessed", "passengersbylane"];

    /// <summary>The identifier fragment a member name holds, or null when it is an aggregate or holds none.</summary>
    public static string IdentifierIn(string member)
    {
        var name = member?.ToLowerInvariant() ?? string.Empty;
        return Aggregates.Contains(name) ? null : Identifiers.FirstOrDefault(i => name.Contains(i, StringComparison.Ordinal));
    }
}
