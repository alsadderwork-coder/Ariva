using Ariva.Infra.Alerting;
using FluentAssertions;

namespace Ariva.UnitTests.Alerting;

/// <summary>
/// ARV-187 (lead decision P3, superseding ARV-181b2's warning per tick): a stored border rule that the evaluation does not
/// judge is warned about once per rule and values version in a host process, again when its values change, and again after
/// it was evaluated in between.
/// </summary>
public sealed class NotEvaluatedWarningsTests
{
    private static readonly Guid Rule = Guid.Parse("0199e9a0-0000-7000-8000-000000187001");
    private static readonly Guid Other = Guid.Parse("0199e9a0-0000-7000-8000-000000187002");

    [Fact]
    public void ShouldWarn_Should_AnswerOncePerValuesVersion_When_TheSameRuleIsSeenEveryTick()
    {
        var warnings = new NotEvaluatedWarnings();

        warnings.ShouldWarn(Rule, "v1").Should().BeTrue("the first tick that finds it");
        warnings.ShouldWarn(Rule, "v1").Should().BeFalse("the same values at the next tick");
        warnings.ShouldWarn(Rule, "v1").Should().BeFalse();
        warnings.ShouldWarn(Other, "v1").Should().BeTrue("per rule");
        warnings.ShouldWarn(Rule, "v2").Should().BeTrue("its values changed and it still fails the checks");
        warnings.ShouldWarn(Rule, "v2").Should().BeFalse();
        warnings.ShouldWarn(Rule, "v1").Should().BeTrue("changed back is another change");
    }

    [Fact]
    public void ShouldWarn_Should_AnswerAgain_When_TheRuleWasEvaluatedInBetween()
    {
        var warnings = new NotEvaluatedWarnings();
        warnings.ShouldWarn(Rule, "v1").Should().BeTrue();

        warnings.Forget(Rule);

        warnings.ShouldWarn(Rule, "v1").Should().BeTrue("repaired, evaluated, then failing again with the same values");
        warnings.ShouldWarn(Rule, "v1").Should().BeFalse();
        warnings.Forget(Other);
        warnings.ShouldWarn(Rule, "v1").Should().BeFalse("forgetting another rule changes nothing");
        warnings.ShouldWarn(Rule, null).Should().BeTrue("a missing version is a version of its own");
        warnings.ShouldWarn(Rule, null).Should().BeFalse();
    }

    [Fact]
    public void Instances_Should_KeepTheirOwnMemory_When_TwoHostsRun()
    {
        var first = new NotEvaluatedWarnings();
        var second = new NotEvaluatedWarnings();

        first.ShouldWarn(Rule, "v1").Should().BeTrue();
        second.ShouldWarn(Rule, "v1").Should().BeTrue("per host process: a restarted host warns once more");
    }
}
