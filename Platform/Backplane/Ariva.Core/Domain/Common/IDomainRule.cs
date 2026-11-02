using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Common;

/// <summary>
/// ARV-282 (CWE-209, CWE-20): a refusal by a rule of the domain model, whose text is written for the caller. Services that
/// answer rule refusals catch exceptions by this interface, never by the stack frame that threw them (which inlining in a
/// Release build can drop) or by their message; any other exception, a framework or driver one included, keeps its text to
/// the logs and becomes a 500 without details. Only <see cref="DomainRuleException"/>, <see cref="DomainArgumentException"/>
/// and <see cref="DomainArgumentOutOfRangeException"/> implement it.
/// </summary>
public interface IDomainRule
{
    /// <summary>The rule's text as the caller reads it: the message without the parameter and value .NET appends.</summary>
    string Rule { get; }

    /// <summary>What the refusal is, for a service that answers some kinds with another status.</summary>
    DomainRuleKind Kind { get; }
}
