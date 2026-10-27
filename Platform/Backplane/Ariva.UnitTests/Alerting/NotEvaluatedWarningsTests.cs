using Ariva.Infra.Alerting;
using FluentAssertions;

namespace Ariva.UnitTests.Alerting;

/// <summary>
/// ARV-187 (lead decision P3, superseding ARV-181b2's warning per tick): a stored border rule that the evaluation does not
/// judge is warned about once per rule and values version in a host process, again when its values change, and again after
/// it was evaluated in between. ARV-190: a version counts as warned only once it is recorded after the commit, so a failed
/// commit warns again; and a tick keeps only the rules it found, so a deleted rule is forgotten.
/// </summary>
public sealed class NotEvaluatedWarningsTests
{
    private static readonly Guid Rule = Guid.Parse("0199e9a0-0000-7000-8000-000000187001");
    private static readonly Guid Other = Guid.Parse("0199e9a0-0000-7000-8000-000000187002");

    [Fact]
    public void ShouldWarn_Should_AnswerOncePerValuesVersion_When_EachWarningIsRecorded()
    {
        var warnings = new NotEvaluatedWarnings();

        Warn(warnings, Rule, "v1").Should().BeTrue("the first tick that finds it");
        Warn(warnings, Rule, "v1").Should().BeFalse("the same values at the next tick");
        Warn(warnings, Rule, "v1").Should().BeFalse();
        Warn(warnings, Other, "v1").Should().BeTrue("per rule");
        Warn(warnings, Rule, "v2").Should().BeTrue("its values changed and it still fails the checks");
        Warn(warnings, Rule, "v2").Should().BeFalse();
        Warn(warnings, Rule, "v1").Should().BeTrue("changed back is another change");
    }

    [Fact]
    public void ShouldWarn_Should_AnswerAgain_When_TheWarningsCommitFailed()
    {
        var warnings = new NotEvaluatedWarnings();

        warnings.ShouldWarn(Rule, "v1").Should().BeTrue();
        warnings.ShouldWarn(Rule, "v1").Should().BeTrue("the first tick's commit failed, so nothing was recorded: warn and audit again");
        warnings.Warned(Rule, "v1");
        warnings.ShouldWarn(Rule, "v1").Should().BeFalse("recorded after the second tick's commit");
        warnings.ShouldWarn(Rule, "v2").Should().BeTrue("asking records nothing either");
        warnings.ShouldWarn(Rule, "v1").Should().BeFalse("the recorded version is still v1");
    }

    [Fact]
    public void ShouldWarn_Should_AnswerAgain_When_TheRuleWasEvaluatedInBetween()
    {
        var warnings = new NotEvaluatedWarnings();
        Warn(warnings, Rule, "v1").Should().BeTrue();

        warnings.Forget(Rule);

        Warn(warnings, Rule, "v1").Should().BeTrue("repaired, evaluated, then failing again with the same values");
        Warn(warnings, Rule, "v1").Should().BeFalse();
        warnings.Forget(Other);
        Warn(warnings, Rule, "v1").Should().BeFalse("forgetting another rule changes nothing");
        Warn(warnings, Rule, null).Should().BeTrue("a missing version is a version of its own");
        Warn(warnings, Rule, null).Should().BeFalse();
    }

    [Fact]
    public void Keep_Should_ForgetTheRulesTheTickDidNotFind_When_ARuleWasDeletedOrDisabled()
    {
        var warnings = new NotEvaluatedWarnings();
        warnings.Warned(Rule, "v1");
        warnings.Warned(Other, "v1");

        warnings.Keep([Rule]);

        warnings.ShouldWarn(Rule, "v1").Should().BeFalse("still enabled: its warning is kept");
        warnings.ShouldWarn(Other, "v1").Should().BeTrue("deleted or disabled since: forgotten");
        warnings.Keep([]);
        warnings.ShouldWarn(Rule, "v1").Should().BeTrue("no enabled rule left");
        warnings.Invoking(w => w.Keep(null)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Instances_Should_KeepTheirOwnMemory_When_TwoHostsRun()
    {
        var first = new NotEvaluatedWarnings();
        var second = new NotEvaluatedWarnings();

        Warn(first, Rule, "v1").Should().BeTrue();
        Warn(second, Rule, "v1").Should().BeTrue("per host process: a restarted host warns once more");
    }

    /// <summary>Asks, and records as the evaluation does once the warning's commit has succeeded.</summary>
    private static bool Warn(NotEvaluatedWarnings warnings, Guid ruleId, string valuesHash)
    {
        var warn = warnings.ShouldWarn(ruleId, valuesHash);
        if (warn)
            warnings.Warned(ruleId, valuesHash);
        return warn;
    }
}
