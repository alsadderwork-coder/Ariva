using System.Text.RegularExpressions;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// Maps another system's code for a service point to an Ariva desk (glossary Desk code mapping, ARV-015). Unique per
/// system and site among live mappings, and one live mapping per desk and system. AMAN codes map only to border desks
/// and e-gates; AODB codes only to check-in counters. Feed consumers resolve codes through it and park what they cannot
/// resolve; they never guess.
/// </summary>
public partial class DeskCodeMapping : BaseSoftDeletableEntity<DeskCodeMapping>, ISiteBound
{
    protected DeskCodeMapping()
    {
    }

    public DeskCodeMapping(ExternalSystem system, string externalCode, Desk desk)
    {
        if (!Enum.IsDefined(system))
            throw new ArgumentOutOfRangeException(nameof(system), system, "Unknown external system.");
        ArgumentNullException.ThrowIfNull(desk);
        System = system;
        ExternalCode = NormalizeCode(externalCode) ?? throw new ArgumentException("An external code is 1 to 32 letters, digits or . _ / - characters.", nameof(externalCode));
        Assign(desk);
    }

    public virtual ExternalSystem System { get; protected set; }
    public virtual string ExternalCode { get; protected set; }
    public virtual Desk Desk { get; protected set; }
    public virtual string SiteCode { get; protected set; }

    /// <summary>Points the code at another desk of the same site (a desk was renumbered or replaced).</summary>
    public virtual void Assign(Desk desk)
    {
        ArgumentNullException.ThrowIfNull(desk);
        if (desk.IsDeleted)
            throw new InvalidOperationException("The desk is deleted.");
        if (SiteCode is not null && desk.SiteCode != SiteCode)
            throw new InvalidOperationException("A mapping stays in its site.");
        var allowed = System switch
        {
            ExternalSystem.Aman => desk.Kind is DeskKind.Desk or DeskKind.EGate,
            ExternalSystem.Aodb => desk.Kind is DeskKind.Counter,
            _ => false
        };
        if (!allowed)
            throw new InvalidOperationException($"A {System} code cannot map to a {desk.Kind}.");
        Desk = desk;
        SiteCode = desk.SiteCode;
    }

    /// <summary>Trimmed and upper case, or null when the code cannot be valid.</summary>
    public static string NormalizeCode(string code)
    {
        var normalized = code?.Trim().ToUpperInvariant();
        return normalized is not null && Shape().IsMatch(normalized) ? normalized : null;
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._/-]{0,31}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Shape();
}
