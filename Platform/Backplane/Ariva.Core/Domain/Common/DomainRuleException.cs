using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Common;

/// <summary>
/// ARV-282: a domain rule an entity's state refuses (an <see cref="InvalidOperationException"/>, as before, so code and tests
/// that expect one still hold). Its message is the rule's text; <see cref="Kind"/> tells a code or floor taken apart.
/// </summary>
public sealed class DomainRuleException : InvalidOperationException, IDomainRule
{
    #region Constructors

    public DomainRuleException(string rule, DomainRuleKind kind = DomainRuleKind.Rule) : base(rule) => Kind = kind;

    #endregion

    #region Properties

    public string Rule => Message;

    public DomainRuleKind Kind { get; }

    #endregion
}
