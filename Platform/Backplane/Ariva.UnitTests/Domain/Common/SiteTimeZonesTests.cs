using Ariva.Core.Domain.Common;
using FluentAssertions;
using TimeZoneConverter;

namespace Ariva.UnitTests.Domain.Common;

/// <summary>
/// Airport time zones (bug fix 2026-10-08): the hosts run with invariant globalization, where .NET on Windows cannot map
/// an IANA id without ICU, so an airport could not be created on a Windows machine and its reports fell back to UTC.
/// The resolver carries the mapping itself. These tests run on Linux; the Windows half is the mapping table, checked
/// here for the zones of the markets Ariva sells into.
/// </summary>
public sealed class SiteTimeZonesTests
{
    [Theory]
    [InlineData("Asia/Amman")]
    [InlineData("Asia/Dubai")]
    [InlineData("Africa/Luanda")]
    [InlineData("Europe/London")]
    [InlineData("UTC")]
    [InlineData("Asia/Kolkata")]
    [InlineData("Asia/Calcutta")]
    public void IsIana_Should_AcceptTheId_When_ItIsAnIanaZone(string id)
    {
        SiteTimeZones.IsIana(id).Should().BeTrue("Chrome lists some zones under their older IANA names, Asia/Calcutta among them");
    }

    [Theory]
    [InlineData("Arabian Standard Time")]
    [InlineData("asia/amman")]
    [InlineData("Asia/Amman ")]
    [InlineData("Mars/Olympus")]
    [InlineData("../../etc/passwd")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void IsIana_Should_Refuse_When_TheIdIsNotAnIanaZone(string id)
    {
        SiteTimeZones.IsIana(id).Should().BeFalse("an airport stores the IANA id only, which the web and every host read alike");
    }

    [Fact]
    public void TryFind_Should_RefuseWithoutLookup_When_TheIdIsTooLong()
    {
        SiteTimeZones.TryFind(new string('A', SiteTimeZones.MaxLength + 1), out var zone).Should().BeFalse();
        zone.Should().BeNull();
    }

    [Theory]
    [InlineData("Asia/Dubai", 4)]
    [InlineData("Arabian Standard Time", 4)]
    [InlineData("Africa/Luanda", 1)]
    public void TryFind_Should_ResolveTheZone_When_StoredAsIanaOrWindows(string id, int offsetHours)
    {
        SiteTimeZones.TryFind(id, out var zone).Should().BeTrue();
        zone!.BaseUtcOffset.Should().Be(TimeSpan.FromHours(offsetHours));
    }

    [Theory]
    [InlineData("Asia/Amman", "Jordan Standard Time")]
    [InlineData("Asia/Dubai", "Arabian Standard Time")]
    [InlineData("Asia/Riyadh", "Arab Standard Time")]
    [InlineData("Asia/Baghdad", "Arabic Standard Time")]
    [InlineData("Asia/Beirut", "Middle East Standard Time")]
    [InlineData("Africa/Cairo", "Egypt Standard Time")]
    [InlineData("Africa/Luanda", "W. Central Africa Standard Time")]
    [InlineData("Europe/London", "GMT Standard Time")]
    public void Mapping_Should_GiveTheWindowsZone_When_AHostRunsOnWindows(string iana, string windows)
    {
        TZConvert.IanaToWindows(iana).Should().Be(windows);
    }
}
