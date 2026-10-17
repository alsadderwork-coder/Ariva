using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using FluentAssertions;

namespace Ariva.UnitTests.Domain;

/// <summary>
/// ARV-142a: an advertisement starts as a draft, plays only once approved and within its dates, and is approved only by a
/// person other than its uploader and the last person who changed it; a change to an approved item sends it back to draft;
/// a retired item stays retired. Its details are bounded plain text, its file a generated key of its own type.
/// </summary>
public sealed class AdvertisingMediaTests
{
    private static readonly Guid Uploader = Guid.Parse("0199a000-0000-7000-8000-000000000001");
    private static readonly Guid Changer = Guid.Parse("0199a000-0000-7000-8000-000000000002");
    private static readonly Guid Approver = Guid.Parse("0199a000-0000-7000-8000-000000000003");
    private static readonly DateTime Now = new(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly First = new(2026, 11, 1);
    private static readonly DateOnly Last = new(2026, 11, 30);

    private static AdvertisingMediaFile File(string key = "0123456789abcdef0123456789abcdef.png", string type = "image/png", long size = 1024,
        string sha = null, int width = 1920, int height = 1080, string name = "summer.png") =>
        new(key, type, size, sha ?? new string('a', 64), width, height, name);

    private static AdvertisingMediaDetails Details(string advertiser = "Gulf Air", string campaign = "Winter sun", DateOnly? first = null, DateOnly? last = null) =>
        new(advertiser, campaign, first ?? First, last ?? Last);

    private static AdvertisingMedia Item() => new("AUH-TA", File(), Details(), Uploader);

    [Fact]
    public void Constructor_Should_MakeADraftThatTheUploaderChanged_When_TheValuesAreValid()
    {
        var item = Item();

        item.Status.Should().Be(AdvertisingMediaStatus.Draft);
        item.UploadedById.Should().Be(Uploader);
        item.ChangedById.Should().Be(Uploader);
        item.StartsOn.Should().Be("2026-11-01");
        item.EndsOn.Should().Be("2026-11-30");
        item.ApprovedById.Should().BeNull();
        item.CanPlayOn(First).Should().BeFalse("a draft never plays");
    }

    [Theory]
    [InlineData("", "a blank advertiser")]
    [InlineData("   ", "a blank advertiser")]
    [InlineData("Gulf\u202EriA", "a right-to-left override")]
    [InlineData("Gulf\nAir", "a line break")]
    [InlineData("Gulf\u200BAir", "a zero-width space")]
    public void Details_Should_BeRefused_When_TheAdvertiserIsNotPlainText(string advertiser, string reason)
    {
        Details(advertiser).Problems().Should().ContainSingle(reason);
        var create = () => new AdvertisingMedia("AUH-TA", File(), Details(advertiser), Uploader);
        create.Should().Throw<ArgumentException>(reason);
    }

    [Fact]
    public void Details_Should_BoundNamesAndDates_When_Checked()
    {
        Details(new string('a', AdvertisingMedia.MaxNameLength)).Problems().Should().BeEmpty();
        Details(new string('a', AdvertisingMedia.MaxNameLength + 1)).Problems().Should().ContainSingle();
        Details(campaign: new string('c', AdvertisingMedia.MaxNameLength + 1)).Problems().Should().ContainSingle();
        Details(first: Last, last: First).Problems().Should().ContainSingle("the last day is before the first");
        Details(first: First, last: First).Problems().Should().BeEmpty("one day is a span");
        Details(first: First, last: First.AddDays(AdvertisingMedia.MaxDays - 1)).Problems().Should().BeEmpty();
        Details(first: First, last: First.AddDays(AdvertisingMedia.MaxDays)).Problems().Should().ContainSingle("two years at the most");
        new AdvertisingMediaDetails("Gulf Air", "Winter sun", default, Last).Problems().Should().ContainSingle("no first day");
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef.jpg", "image/png", "the key's extension is not the type's")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF.png", "image/png", "upper-case hex")]
    [InlineData("../23456789abcdef0123456789abcdef.png", "image/png", "a path")]
    [InlineData("0123456789abcdef0123456789abcdef.svg", "image/svg+xml", "SVG is not an advertisement type")]
    [InlineData("0123456789abcdef0123456789abcdef.png.html", "image/png", "a second extension")]
    [InlineData("0123456789abcdef0123456789abcdef.gif", "image/gif", "GIF is not taken")]
    public void File_Should_BeRefused_When_TheKeyOrTypeIsNotAGeneratedImageKey(string key, string type, string reason) =>
        File(key, type).Problems().Should().NotBeEmpty(reason);

    [Fact]
    public void File_Should_BoundSizeHashPixelsAndName_When_Checked()
    {
        File().Problems().Should().BeEmpty();
        File(key: "0123456789abcdef0123456789abcdef.webp", type: "image/webp").Problems().Should().BeEmpty();
        File(size: 0).Problems().Should().ContainSingle();
        File(size: AdvertisingMedia.MaxImageBytes + 1).Problems().Should().ContainSingle();
        File(sha: new string('A', 64)).Problems().Should().ContainSingle("lower-case hex only");
        File(sha: new string('a', 63)).Problems().Should().ContainSingle();
        File(width: AdvertisingMedia.MaxSide + 1, height: 10).Problems().Should().ContainSingle();
        File(width: 3840, height: 2161).Problems().Should().ContainSingle("over 8,294,400 pixels");
        File(width: 3840, height: 2160).Problems().Should().BeEmpty();
        File(width: 2160, height: 3840).Problems().Should().BeEmpty("a portrait 4K screen");
        File(name: "..\\boot.png").Problems().Should().ContainSingle("a path in the name");
        File(name: "ad\u202Egnp.exe").Problems().Should().ContainSingle("a right-to-left override in the name");
        File(name: null).Problems().Should().BeEmpty("a name is optional");
    }

    [Fact]
    public void Approve_Should_BeRefused_When_TheApproverUploadedTheItem()
    {
        var item = Item();

        item.CanBeApprovedBy(Uploader).Should().BeFalse();
        var approve = () => item.Approve(Uploader, "e2e.uploader", Now, item.ReviewToken());
        approve.Should().Throw<InvalidOperationException>().WithMessage(AdvertisingMediaRules.SamePersonMessage);
        item.Status.Should().Be(AdvertisingMediaStatus.Draft);
    }

    [Fact]
    public void Approve_Should_BeRefused_When_TheApproverLastChangedTheItem()
    {
        var item = Item();
        item.ChangeDetails(Details(campaign: "Spring sun"), Changer);

        item.CanBeApprovedBy(Changer).Should().BeFalse("the person who last changed it saw only their own change");
        item.CanBeApprovedBy(Uploader).Should().BeFalse("the uploader never approves");
        item.CanBeApprovedBy(Approver).Should().BeTrue();
    }

    [Fact]
    public void Approve_Should_LetTheItemPlayWithinItsDates_When_AnotherPersonApproves()
    {
        var item = Item();

        item.Approve(Approver, "e2e.approver", Now, item.ReviewToken());

        item.Status.Should().Be(AdvertisingMediaStatus.Approved);
        (item.ApprovedById, item.ApprovedBy, item.ApprovedOn).Should().Be((Approver, "e2e.approver", Now));
        item.CanPlayOn(First.AddDays(-1)).Should().BeFalse("before its first day");
        item.CanPlayOn(First).Should().BeTrue();
        item.CanPlayOn(Last).Should().BeTrue();
        item.CanPlayOn(Last.AddDays(1)).Should().BeFalse("after its last day");
        item.CanBeApprovedBy(Changer).Should().BeFalse("an approved item is not approved again");
        var again = () => item.Approve(Changer, "someone", Now, item.ReviewToken());
        again.Should().Throw<InvalidOperationException>().WithMessage(AdvertisingMediaRules.NotDraftMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void CanBeApprovedBy_Should_BeFalse_When_TheApproverIsUnknown(string approver) =>
        Item().CanBeApprovedBy(approver is null ? null : Guid.Parse(approver)).Should().BeFalse();

    [Fact]
    public void Approve_Should_BeRefused_When_TheItemChangedSinceItsApproverWasShownIt()
    {
        var item = Item();
        var shown = item.ReviewToken();
        item.ReviewToken().Should().Be(shown, "the same item gives the same token");

        item.ChangeDetails(Details(last: Last.AddDays(400)), Changer);

        item.ReviewToken().Should().NotBe(shown, "the dates changed");
        var approve = () => item.Approve(Approver, "e2e.approver", Now, shown);
        approve.Should().Throw<InvalidOperationException>().WithMessage(AdvertisingMediaRules.ChangedSinceReviewMessage);
        item.Status.Should().Be(AdvertisingMediaStatus.Draft);
        item.Approve(Approver, "e2e.approver", Now, item.ReviewToken());
        item.Status.Should().Be(AdvertisingMediaStatus.Approved);
    }

    [Fact]
    public void ReviewToken_Should_ChangeWithEveryDetailAndTheLastChanger_When_TheItemChanges()
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var item = Item();
        tokens.Add(item.ReviewToken());
        foreach (var details in new[] { Details(advertiser: "Emirates"), Details(campaign: "Spring"), Details(first: First.AddDays(1)), Details(last: Last.AddDays(1)) })
        {
            item.ChangeDetails(details, Uploader);
            tokens.Add(item.ReviewToken()).Should().BeTrue($"{details} gives a new token");
        }

        item.ChangeDetails(item.Details(), Changer);
        tokens.Add(item.ReviewToken()).Should().BeTrue("the same details saved by another person");
        item.ReviewToken().Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ChangeDetails_Should_SendAnApprovedItemBackToDraft_When_ItsDetailsChange()
    {
        var item = Item();
        item.Approve(Approver, "e2e.approver", Now, item.ReviewToken());

        var withdrawn = item.ChangeDetails(Details(last: Last.AddDays(30)), Changer);

        withdrawn.Should().BeTrue();
        item.Status.Should().Be(AdvertisingMediaStatus.Draft);
        (item.ApprovedById, item.ApprovedBy, item.ApprovedOn).Should().Be((null, null, null));
        item.ChangedById.Should().Be(Changer);
        item.CanPlayOn(First).Should().BeFalse("nothing plays until a second person approves the change");
        item.CanBeApprovedBy(Approver).Should().BeTrue("the earlier approver may approve the change, being neither its author nor the uploader");
    }

    [Fact]
    public void Retire_Should_StopTheItemForGood_When_Retired()
    {
        var item = Item();
        item.Approve(Approver, "e2e.approver", Now, item.ReviewToken());

        item.Retire(Now);

        item.Status.Should().Be(AdvertisingMediaStatus.Retired);
        item.RetiredOn.Should().Be(Now);
        item.CanPlayOn(First).Should().BeFalse();
        item.CanBeApprovedBy(Changer).Should().BeFalse();
        ((Action)(() => item.Retire(Now))).Should().Throw<InvalidOperationException>().WithMessage(AdvertisingMediaRules.RetiredMessage);
        ((Action)(() => item.ChangeDetails(Details(), Changer))).Should().Throw<InvalidOperationException>().WithMessage(AdvertisingMediaRules.RetiredMessage);
        ((Action)(() => item.Approve(Changer, "someone", Now, item.ReviewToken()))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AuditSummary_Should_FitAnAuditEntryWhole_When_EveryValueIsAtItsLongest()
    {
        // Quotes are written escaped (two characters each), and characters outside the basic plane as two escapes (twelve).
        var name = string.Concat(Enumerable.Repeat("\U0001F600", AdvertisingMedia.MaxNameLength / 2));
        var item = new AdvertisingMedia("XXXXXXXX-XXXXXXXX", File(name: string.Concat(Enumerable.Repeat("\U0001F600", FloorPlan.MaxFileNameLength))), Details(name, name), Uploader);
        item.Approve(Approver, new string('u', 200), Now, item.ReviewToken());

        item.AuditSummary().Length.Should().BeLessThanOrEqualTo(AuditEntry.SummaryLength, "every detail of what plays stays in the audit trail");
        new AdvertisingMedia("XXXXXXXX-XXXXXXXX", File(), Details(new string('"', AdvertisingMedia.MaxNameLength), new string('"', AdvertisingMedia.MaxNameLength)), Uploader)
            .AuditSummary().Length.Should().BeLessThanOrEqualTo(AuditEntry.SummaryLength, "quotes too");
    }

    [Fact]
    public void Constructor_Should_BeRefused_When_TheUploaderOrSiteIsUnknown()
    {
        ((Action)(() => new AdvertisingMedia("AUH-TA", File(), Details(), Guid.Empty))).Should().Throw<ArgumentException>();
        ((Action)(() => new AdvertisingMedia("<script>", File(), Details(), Uploader))).Should().Throw<ArgumentException>();
        ((Action)(() => new AdvertisingMedia("AUH-TA", File(type: "image/jpeg"), Details(), Uploader))).Should().Throw<ArgumentException>();
    }
}
