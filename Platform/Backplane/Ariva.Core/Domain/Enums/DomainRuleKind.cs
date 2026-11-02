namespace Ariva.Core.Domain.Enums;

/// <summary>
/// What a domain rule refusal is (ARV-282), so a service answers it by kind, never by its message text or the frame that
/// threw it. Rule: any other rule, answered with its text. CodeTaken: a code already in use among its live siblings
/// (topology codes, desk ranges). FloorTaken: a floor number another live level of the terminal holds.
/// </summary>
public enum DomainRuleKind
{
    Rule,
    CodeTaken,
    FloorTaken
}
