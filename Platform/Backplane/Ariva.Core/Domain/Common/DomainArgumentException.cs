using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Common;

/// <summary>
/// ARV-282: a domain rule an argument breaks (an <see cref="ArgumentException"/>, as before). <see cref="Rule"/> is the text
/// without the parameter name .NET appends to the message.
/// </summary>
public sealed class DomainArgumentException : ArgumentException, IDomainRule
{
    #region Constructors

    public DomainArgumentException(string rule, string paramName = null) : base(rule, paramName) => Rule = rule;

    #endregion

    #region Properties

    public string Rule { get; }

    public DomainRuleKind Kind => DomainRuleKind.Rule;

    #endregion
}
