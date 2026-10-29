using Ariva.Core.Domain.Entities;
using FluentAssertions;

namespace Ariva.UnitTests.Domain;

/// <summary>
/// ARV-145: a site's advertising policy is a setting, up to 20 excluded queue zones and a reason of 10 to 500 characters on
/// one line, all kept whole in one audit entry; a display may show advertising only where the site allows it, every excluded
/// zone is still a published queue zone of the site, and none of the zones it shows is excluded.
/// </summary>
public sealed class AdvertisingPolicyTests
{
    private const string Reason = "Airport operator letter AO-2026-114";

    /// <summary>The site's published zone profile when the policy was saved.</summary>
    private static readonly Guid Profile = Guid.Parse("0199a000-0000-7000-8000-0000000a0145");

    private static AdvertisingPolicyValues Values(bool allowed = true, string reason = Reason, params string[] excluded) => new(allowed, excluded, reason);

    [Fact]
    public void Policy_Should_KeepItsValuesTrimmedAndAuditThem_When_TheyAreValid()
    {
        var policy = new AdvertisingPolicy("DMO", Values(true, "  " + Reason + "  ", " A-RES ", "A-CIT"), Profile);

        policy.Allowed.Should().BeTrue();
        policy.ExcludedZoneList.Should().Equal("A-RES", "A-CIT");
        policy.Reason.Should().Be(Reason);
        policy.Values().Problems().Should().BeEmpty("what is stored reads back as valid values");
        policy.AuditSummary().Should().Contain("\"siteCode\":\"DMO\"").And.Contain("\"allowed\":true").And.Contain("A-RES").And.Contain(Reason);
    }

    [Theory]
    [InlineData(true, new string[0], new[] { "A-CIT" }, true)]
    [InlineData(true, new[] { "A-RES" }, new[] { "A-CIT", "A-VIS" }, true)]
    [InlineData(true, new[] { "A-RES" }, new[] { "A-CIT", "A-RES" }, false)]
    [InlineData(true, new[] { "A-RES" }, new[] { " A-RES " }, false)]
    [InlineData(true, new[] { "A-RES" }, new[] { "a-res" }, false)]
    [InlineData(true, new string[0], new[] { "A-OLD" }, false)]
    [InlineData(true, new string[0], new string[0], true)]
    [InlineData(true, new string[0], new[] { "A-CIT", null }, false)]
    [InlineData(false, new string[0], new[] { "A-CIT" }, false)]
    [InlineData(false, new string[0], new string[0], false)]
    public void AllowsDisplayOf_Should_NeedTheSiteAndEveryZone_When_ADisplayAsksForAdvertising(bool allowed, string[] excluded, string[] zones, bool expected)
    {
        // Zone names are exact, as a display's entries name them, and every one must be a queue zone of the published profile
        // (A-OLD was dropped by it; security review of ARV-145, round 4). A null zone is none of them (ARV-193).
        var policy = new AdvertisingPolicy("DMO", Values(allowed, Reason, excluded), Profile);
        policy.AllowsDisplayOf(zones, ["A-CIT", "A-RES", "A-VIS"], Profile).Should().Be(expected);
    }

    [Theory]
    [InlineData(new[] { "A-VIS" }, "a display showing a zone that was never excluded")]
    [InlineData(new[] { "A-IMM" }, "a display showing the excluded hall under its new name")]
    [InlineData(new string[0], "a display with no zones")]
    public void AllowsDisplayOf_Should_KeepAdvertisingOffEverywhere_When_AnExcludedZoneIsNoLongerPublished(string[] zones, string reason)
    {
        // The roles that manage zone profiles can rename A-RES (the immigration hall) to A-IMM in a new profile: the exclusion
        // no longer names a published queue zone, so no display of the site shows advertising until an administrator updates
        // the policy (security review of ARV-145).
        var policy = new AdvertisingPolicy("DMO", Values(true, Reason, "A-RES"), Profile);
        policy.AllowsDisplayOf(zones, ["A-CIT", "A-IMM", "A-VIS"], Profile).Should().BeFalse(reason);
        policy.AllowsDisplayOf(zones, null, Profile).Should().BeFalse("nothing known to be published is the same");
        policy.AllowsDisplayOf(["A-VIS"], ["A-RES", "A-VIS"], Profile).Should().BeTrue("both zones are published, the display's is not excluded");
    }

    [Fact]
    public void AllowsDisplayOf_Should_WaitForAnAdministrator_When_TheSitePublishesAnotherZoneProfile()
    {
        // A new profile can hand the excluded name to a decoy zone, move the hall's outline or add a zone over it: none of
        // that is visible in the names, so any new profile keeps advertising off until the policy is saved again (round 2
        // of the security review of ARV-145).
        var policy = new AdvertisingPolicy("DMO", Values(true, Reason, "A-RES"), Profile);
        var next = Guid.Parse("0199a000-0000-7000-8000-0000000a0146");
        policy.ProfileChangedSince(next).Should().BeTrue();
        policy.AllowsDisplayOf(["A-VIS"], ["A-RES", "A-VIS"], next).Should().BeFalse("another profile is published");
        policy.AllowsDisplayOf(["A-VIS"], ["A-RES", "A-VIS"], null).Should().BeFalse("no profile is published any more");

        policy.Change(Values(true, Reason, "A-RES"), next);
        policy.AllowsDisplayOf(["A-VIS"], ["A-RES", "A-VIS"], next).Should().BeTrue("the administrator saved it against the new profile");
        policy.AuditSummary().Should().Contain(next.ToString(), "the audit trail shows which profile the exclusions were judged against");

        new AdvertisingPolicy("ALX", Values(true, Reason), null).AllowsDisplayOf(["A-VIS"], [], null).Should().BeFalse(
            "a policy saved before the site had a profile confirms nothing");
    }

    public static TheoryData<string, AdvertisingPolicyValues> Refused => new()
    {
        { "no reason", Values(reason: null) },
        { "a blank reason", Values(reason: "             ") },
        { "a reason of 9 characters", Values(reason: "123456789") },
        { "a reason of 501 characters", Values(reason: new string('x', 501)) },
        { "a line break in the reason", Values(reason: "Approved by the operator\r\nref 12") },
        { "a control character in the reason", Values(reason: "Approved by the operator\u0007") },
        { "a bidirectional override in the reason", Values(reason: "Approved \u202Eby the operator") },
        { "a zero width space in the reason", Values(reason: "Approved by the\u200Boperator") },
        { "21 zones", Values(true, Reason, [.. Enumerable.Range(1, 21).Select(i => $"Z{i}")]) },
        { "a zone twice", Values(true, Reason, "A-RES", " A-RES") },
        { "a blank zone", Values(true, Reason, " ") },
        { "a zone of 201 characters", Values(true, Reason, new string('Z', 201)) },
        { "a zone with an invisible character", Values(true, Reason, "A-\u200DRES") }
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Values_Should_BeRefused_When_TheyBreakARule(string reason, AdvertisingPolicyValues values)
    {
        values.Problems().Should().NotBeEmpty(reason);
        var create = () => new AdvertisingPolicy("DMO", values, Profile);
        create.Should().Throw<ArgumentException>(reason);
    }

    [Fact]
    public void Values_Should_BeRefused_When_TheAuditTrailCouldNotKeepThemWhole()
    {
        // Characters outside the basic plane are kept escaped (12 for 2): a reason at its limit in emoji does not fit one
        // audit entry, so it is refused rather than audited in part.
        Values(reason: string.Concat(Enumerable.Repeat("\U0001F6C2", 250))).Problems().Should().Equal(AdvertisingPolicyValues.TooLongToAudit);
    }

    [Fact]
    public void AuditSummary_Should_KeepTheReasonWhole_When_EveryValueIsAtItsLimitInPlainText()
    {
        var zones = Enumerable.Range(1, AdvertisingPolicy.MaxExcludedZones).Select(i => $"ARRIVALS-IMMIGRATION-{i:00}").ToArray();
        var reason = new string('"', 20) + new string('ب', AdvertisingPolicy.MaxReasonLength - 20);
        var values = Values(true, reason, zones);
        values.Problems().Should().BeEmpty();
        var summary = new AdvertisingPolicy("AUH-TA", values, Profile).AuditSummary();
        summary.Length.Should().BeLessThanOrEqualTo(AuditEntry.SummaryLength);
        summary.Should().Contain(new string('ب', AdvertisingPolicy.MaxReasonLength - 20)).And.Contain(zones[^1]);
    }

    [Fact]
    public void Markup_Should_BeKeptAsText_When_ItIsInTheReason()
    {
        // The reason is shown as text only (CWE-79); stored as given.
        var policy = new AdvertisingPolicy("DMO", Values(reason: "<script>alert(1)</script> approved"), Profile);
        policy.Reason.Should().Be("<script>alert(1)</script> approved");
    }

    [Fact]
    public void Policy_Should_BeRefused_When_ItsSiteCodeIsNotValid()
    {
        var create = () => new AdvertisingPolicy("dmo'--", Values(), Profile);
        create.Should().Throw<ArgumentException>();
    }
}
