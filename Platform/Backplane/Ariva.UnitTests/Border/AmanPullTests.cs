using System.Text;
using System.Text.Json;
using Ariva.Business.Contracts.Aman.V1;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Integration;
using Ariva.Infra.Border;
using FluentAssertions;

namespace Ariva.UnitTests.Border;

/// <summary>
/// ARV-050: a page of AMAN's feed from its Integration API is read as strictly as the Kafka values, its envelope and
/// position checked, never echoing AMAN's text; an AmanFeed endpoint pulls with TOTP client credentials only, below a
/// feed path, for one site.
/// </summary>
public sealed class AmanPullTests
{
    private static readonly DateTimeOffset Minute = new(2026, 10, 3, 18, 30, 0, TimeSpan.Zero);

    private static string Item(string id) =>
        JsonSerializer.Serialize(new DeskIntervalStats("DMO", "IN09", Minute, 60, 2, 3, 41, 60, 30, "VIS", id), AmanFeedJson<DeskIntervalStats>.Options);

    private static byte[] Page(string items, long next, bool hasErrors = false, string messages = "[]") =>
        Encoding.UTF8.GetBytes($$"""{"data":{"items":[{{items}}],"next":{{next}}},"hasErrors":{{(hasErrors ? "true" : "false")}},"errorMessages":{{messages}}}""");

    [Fact]
    public void Read_Should_TakeEachReadableRecordAndCountTheRest_When_APageArrives()
    {
        var unknownMember = Item("a-2").Replace("\"siteCode\"", "\"officerId\":\"OFFICER-7\",\"siteCode\"", StringComparison.Ordinal);
        var noOffset = Item("a-3").Replace("\\u002B00:00", "", StringComparison.Ordinal);
        noOffset.Should().Contain("\"2026-10-03T18:30:00\"");
        var (page, error) = AmanFeedPages.Read<DeskIntervalStats>(Page(string.Join(',', Item("a-1"), unknownMember, noOffset, "42"), 14), 10);

        error.Should().BeNull();
        page.Items.Should().ContainSingle().Which.SourceEventId.Should().Be("a-1");
        page.Unreadable.Should().Be(3, "an unknown member, a time without an offset and a number are not the contract");
        page.Next.Should().Be(14);
        AmanFeedPages.Read<DeskIntervalStats>(Page("", 10), 10).Page.Items.Should().BeEmpty("an empty page stays where it is");
    }

    [Theory]
    [InlineData("errors", "AMAN answered with errors.")]
    [InlineData("back", "did not move forward")]
    [InlineData("stuck", "did not move forward")]
    [InlineData("jumped", "did not move forward")]
    [InlineData("envelope", "not its feed envelope")]
    [InlineData("json", "not JSON")]
    [InlineData("many", "more than 1000 records")]
    public void Read_Should_RefuseThePageWithArivasOwnReason_When_ItIsNotAMovingFeedPage(string what, string reason)
    {
        var body = what switch
        {
            "errors" => Page("", 10, hasErrors: true, messages: "[\"<script>secret detail</script>\"]"),
            "back" => Page(Item("a-1"), 9),
            "stuck" => Page(Item("a-1"), 10),
            "jumped" => Page("", 11),
            "envelope" => Encoding.UTF8.GetBytes("""{"items":[],"next":10}"""),
            "json" => Encoding.UTF8.GetBytes("<html>not json</html>"),
            _ => Page(string.Join(',', Enumerable.Repeat(Item("a-1"), 1001)), 2000)
        };

        var (page, error) = AmanFeedPages.Read<DeskIntervalStats>(body, 10);

        page.Should().BeNull();
        error.Should().Contain(reason).And.NotContain("secret detail").And.NotContain("<");
    }

    private static OutboundConnectionRequest Connection(string authKind = "TotpClientCredentials", string pullPath = "/feed/", int poll = 30) =>
        new("https://aman.example.test/aman/api/v1/", ["10.0.0.0/8"], authKind, TokenPath: authKind == "TotpClientCredentials" ? "/auth" : null,
            ClientId: authKind == "TotpClientCredentials" ? "ariva" : null, HeaderName: authKind == "ApiKeyHeader" ? "X-Api-Key" : null, TotpPerRequest: true,
            PullPath: pullPath, PollSeconds: poll);

    [Fact]
    public void Rules_Should_AcceptAnAmanPullOnlyWithTotpCredentialsBelowAFeedPath_When_Checked()
    {
        var empty = new HashSet<string>();
        var (good, errors) = OutboundRules.Check(Connection(), OutboundEndpointPurpose.AmanFeed, empty, false);
        errors.Should().BeEmpty();
        good.Should().Match<OutboundConnection>(c => c.PullPath == "/feed/" && c.PollSeconds == 30 && c.TotpPerRequest && c.AuthKind == OutboundAuthKind.TotpClientCredentials);

        OutboundRules.Check(Connection("ApiKeyHeader"), OutboundEndpointPurpose.AmanFeed, empty, false).Errors.Should().Contain(e => e.Contains("TotpClientCredentials"));
        OutboundRules.Check(Connection(pullPath: "/feed"), OutboundEndpointPurpose.AmanFeed, empty, false).Errors.Should().Contain(e => e.Contains("ending in a slash"));
        OutboundRules.Check(Connection(pullPath: "/feed/?all=1"), OutboundEndpointPurpose.AmanFeed, empty, false).Errors.Should().Contain(e => e.Contains("ending in a slash"));
        OutboundRules.Check(Connection(pullPath: "http://169.254.169.254/"), OutboundEndpointPurpose.AmanFeed, empty, false).Errors.Should().NotBeEmpty();
        OutboundRules.Check(Connection(pullPath: null), OutboundEndpointPurpose.AmanFeed, empty, false).Errors.Should().NotBeEmpty();
        OutboundRules.Check(Connection(poll: 10), OutboundEndpointPurpose.AmanFeed, empty, false).Errors.Should().Contain(e => e.Contains("pollSeconds"));
        OutboundRules.Check(Connection() with { TotpPerRequest = false }, OutboundEndpointPurpose.AmanFeed, empty, false).Errors.Should().Contain(e => e.Contains("X-TOTP-Code"));
        Ariva.Core.Border.ImmigrationScope.ForPull("DMO").Should().Match<Ariva.Core.Border.ImmigrationScope>(s => s.SiteCode == "DMO" && !s.AnyKnownSite && s.Transport == "pull");
        OutboundRules.Check(Connection(), OutboundEndpointPurpose.Generic, empty, false).Errors.Should().Contain(e => e.Contains("AcrisFlights and AmanFeed purposes only"));
    }
}
