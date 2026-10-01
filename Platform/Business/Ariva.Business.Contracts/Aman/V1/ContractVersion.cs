namespace Ariva.Business.Contracts.Aman.V1;

/// <summary>
/// Version of the AMAN feed contracts in this namespace. Additive changes within V1 bump the minor part;
/// a breaking change creates a new <c>Aman.V2</c> namespace instead.
/// </summary>
public static class ContractVersion
{
    /// <summary>The current V1 contract version.</summary>
    public const string Current = "1.0";
}
