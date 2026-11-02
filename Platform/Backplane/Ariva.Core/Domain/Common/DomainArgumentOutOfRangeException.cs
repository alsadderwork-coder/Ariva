using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Common;

/// <summary>
/// ARV-282: a domain rule a value outside its range breaks (an <see cref="ArgumentOutOfRangeException"/>, as before).
/// <see cref="Rule"/> is the text without the parameter name and the actual value .NET appends to the message.
/// </summary>
public sealed class DomainArgumentOutOfRangeException : ArgumentOutOfRangeException, IDomainRule
{
    #region Constructors

    public DomainArgumentOutOfRangeException(string paramName, object actualValue, string rule) : base(paramName, actualValue, rule) => Rule = rule;

    public DomainArgumentOutOfRangeException(string paramName, string rule) : base(paramName, rule) => Rule = rule;

    #endregion

    #region Properties

    public string Rule { get; }

    public DomainRuleKind Kind => DomainRuleKind.Rule;

    #endregion
}
