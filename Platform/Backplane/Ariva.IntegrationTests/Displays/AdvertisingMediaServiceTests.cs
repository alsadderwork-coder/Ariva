using System.Buffers.Binary;
using System.Text;
using Ariva.Core;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Storage;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Security;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SkiaSharp;

namespace Ariva.IntegrationTests.Displays;

/// <summary>
/// ARV-142a against PostgreSQL with script 0058: an upload is written anew and kept as a draft under a generated key, audited
/// with who uploaded it; nothing is stored for a refused upload; only a person other than the uploader and the last person
/// who changed an item approves it, and a change sends an approved item back to draft; another site's item answers like one
/// that does not exist (IDOR); a retired item stays retired; a display fetches only what may play on it now. The database
/// holds the same rules for the runtime role (the two-person check, an approved item's details, the file and the uploader,
/// a retired item, no delete), and an approval waits for a change of the same item in progress and judges what it left.
/// Its own database, so DMO's advertising policy here never reaches the other displays tests.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AdvertisingMediaServiceTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim SeedGate = new(1, 1);
    private static Guid? _admin;

    /// <summary>The host's clock starts on 2026-10-01 08:00 UTC: noon of 2026-10-01 at DMO (Asia/Dubai).</summary>
    private static readonly DateOnly First = new(2026, 9, 30);

    private static readonly DateOnly Last = new(2026, 10, 31);

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.AdvertisingMedia);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ISvcAdvertisingMedia Media(IServiceProvider s) => s.GetRequiredService<ISvcAdvertisingMedia>();

    private static byte[] Png(string text = null)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(16, 8, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(SKColors.Teal);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var png = data.ToArray();
        if (text is null)
            return png;
        // A tEXt chunk before IEND, and the same text after the image's end: a polyglot.
        var payload = Encoding.Latin1.GetBytes("Comment\0" + text);
        var chunk = new byte[12 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(chunk, payload.Length);
        "tEXt"u8.CopyTo(chunk.AsSpan(4));
        payload.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + payload.Length), Crc32(chunk.AsSpan(4, 4 + payload.Length)));
        return [.. png.AsSpan(0, png.Length - 12), .. chunk, .. png.AsSpan(png.Length - 12), .. Encoding.ASCII.GetBytes(text)];
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }

    private static AdvertisingMediaUpload Upload(byte[] bytes, string name = "winter.png", DateOnly? first = null, DateOnly? last = null, long? length = null,
        string advertiser = "Gulf Air") =>
        new(new MemoryStream(bytes), length ?? bytes.Length, name, advertiser, "Winter sun", first ?? First, last ?? Last);

    private static ChangeAdvertisingMediaRequest Change(DateOnly? last = null, string campaign = "Winter sun") => new("Gulf Air", campaign, First, last ?? Last);

    private async Task<Guid> AdminAsync()
    {
        await SeedGate.WaitAsync(Ct);
        try
        {
            if (_admin is { } existing)
                return existing;
            var admin = await _host.CreateUserAsync("it.media.admin", roles: [RoleCodes.SystemAdministrator]);
            await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
            await _host.AsCallerAsync(null, s =>
                new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
            await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("MED2", "Advertising media test site"), Ct));
            await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("MED3", "Advertising library cap test site"), Ct));
            var reviewed = (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAdvertisingPolicies>().GetAsync("DMO", Ct))).Data.PublishedProfileId;
            var allowed = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAdvertisingPolicies>().ChangeAsync("DMO",
                new AdvertisingPolicyRequest(true, [], "Airport operator letter AO-IT-142a, commercial team", reviewed), Ct));
            allowed.HasErrors.Should().BeFalse(string.Join(", ", allowed.ErrorMessages ?? []));
            _admin = admin;
            return admin;
        }
        finally
        {
            SeedGate.Release();
        }
    }

    private async Task<Guid> ManagerAsync(string userName, params string[] sites)
    {
        var admin = await AdminAsync();
        var user = await _host.CreateUserAsync(userName, roles: [RoleCodes.TerminalDutyManager]);
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(user, new SiteAccessRequest(false, sites.Length == 0 ? ["DMO"] : sites), Ct));
        return user;
    }

    private async Task<AdvertisingMediaViewModel> UploadedAsync(Guid caller, string site = "DMO", DateOnly? first = null, DateOnly? last = null)
    {
        var result = await _host.AsCallerAsync(caller, s => Media(s).UploadAsync(site, Upload(Png(), first: first, last: last), Ct));
        result.HasErrors.Should().BeFalse(string.Join(", ", result.ErrorMessages ?? []));
        return result.Data;
    }

    private async Task<AdvertisingMediaViewModel> ApprovedAsync(Guid uploader, Guid approver, string site = "DMO", DateOnly? first = null, DateOnly? last = null)
    {
        var item = await UploadedAsync(uploader, site, first, last);
        var approved = await _host.AsCallerAsync(approver, s => Media(s).ApproveAsync(site, item.Id, new ApproveAdvertisingMediaRequest(item.ReviewToken), Ct));
        approved.HasErrors.Should().BeFalse(string.Join(", ", approved.ErrorMessages ?? []));
        return approved.Data;
    }

    [Fact]
    public async Task Upload_Should_KeepTheImageWrittenAnewAsAnAuditedDraft_When_ItIsAPolyglot()
    {
        var manager = await ManagerAsync("it.media.upload");
        var created = await _host.AsCallerAsync(manager, s => Media(s).UploadAsync("DMO", Upload(Png("<script>alert(1)</script>"), "  summer.png "), Ct));

        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        created.Data.Should().Match<AdvertisingMediaViewModel>(m => m.SiteCode == "DMO" && m.ContentType == "image/png" && m.WidthPixels == 16 &&
            m.HeightPixels == 8 && m.OriginalFileName == "summer.png" && m.Status == "Draft" && !m.Playable && !m.CanApprove && m.UploadedBy != null);
        var key = await _host.ReadAsync<string>("SELECT storage_key FROM advertising_media WHERE id = @id", created.Data.Id);
        key.Should().MatchRegex("^[0-9a-f]{32}\\.png$", "a generated key, never the file name");
        (await _host.ReadAsync<string>("SELECT uploaded_by_id::text || ' ' || changed_by_id::text FROM advertising_media WHERE id = @id", created.Data.Id))
            .Should().Be($"{manager} {manager}");

        var stored = await _host.AsCallerAsync(manager, s => Media(s).OpenAsync("DMO", created.Data.Id, Ct));
        using var buffer = new MemoryStream();
        await stored.Data.Content.CopyToAsync(buffer, Ct);
        await stored.Data.Content.DisposeAsync();
        Encoding.Latin1.GetString(buffer.ToArray()).Should().NotContain("script").And.NotContain("tEXt", "only the picture is written");
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer.ToArray())).Should().Be(created.Data.Sha256);

        (await _host.ReadAsync<string>(
            "SELECT actor_id::text || ' ' || target_name || ' ' || after_summary FROM audit_entry WHERE action = 'AdvertisingMedia.Uploaded' AND target_id = @id",
            created.Data.Id)).Should().StartWith($"{manager} DMO ").And.Contain("\"status\":\"Draft\"").And.Contain("Gulf Air");
    }

    public static TheoryData<string, string, byte[], string, long?> Refused => new()
    {
        { "an SVG", AdvertisingMediaErrors.UnsupportedFile, Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"), "a.png", null },
        { "a page", AdvertisingMediaErrors.UnsupportedFile, Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"), "a.png", null },
        { "declared larger than 10 MB", AdvertisingMediaErrors.TooLarge, Png(), "a.png", AdvertisingMedia.MaxImageBytes + 1 },
        { "more bytes than declared", AdvertisingMediaErrors.TooLarge, [.. Png(), .. new byte[AdvertisingMedia.MaxImageBytes]], "a.png", 100 },
        { "a path in the name", AdvertisingMediaErrors.UnsafeFileName, Png(), "../../etc/a.png", null },
        { "a right-to-left override in the name", AdvertisingMediaErrors.UnsafeFileName, Png(), "ad\u202Egnp.exe", null }
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task Upload_Should_StoreNothing_When_TheFileOrItsNameIsRefused(string reason, string error, byte[] bytes, string name, long? length)
    {
        var manager = await ManagerAsync($"it.media.refused.{Math.Abs(reason.GetHashCode(StringComparison.Ordinal)) % 100000}");
        var before = await _host.ReadAsync<long>("SELECT count(*) FROM advertising_media");

        var result = await _host.AsCallerAsync(manager, s => Media(s).UploadAsync("DMO", Upload(bytes, name, length: length), Ct));

        result.ErrorMessages.Should().Equal([error], reason);
        (await _host.ReadAsync<long>("SELECT count(*) FROM advertising_media")).Should().Be(before);
    }

    [Fact]
    public async Task Approve_Should_NeedAPersonOtherThanTheUploaderAndTheLastChanger_When_AnItemIsApprovedAndChanged()
    {
        var uploader = await ManagerAsync("it.media.two.uploader");
        var approver = await ManagerAsync("it.media.two.approver");
        var third = await ManagerAsync("it.media.two.third");
        var item = await UploadedAsync(uploader);

        (await ApproveAsShownAsync(uploader, "DMO", item.Id)).ErrorMessages.Should().Equal(AdvertisingMediaErrors.SamePerson);
        (await _host.AsCallerAsync(approver, s => Media(s).GetAsync("DMO", item.Id, Ct))).Data.CanApprove.Should().BeTrue();
        var approved = await ApproveAsShownAsync(approver, "DMO", item.Id);
        approved.Data.Should().Match<AdvertisingMediaViewModel>(m => m.Status == "Approved" && m.ApprovedBy != null && m.ApprovedOn != null && m.Playable);
        (await _host.ReadAsync<string>("SELECT approved_by_id::text FROM advertising_media WHERE id = @id", item.Id)).Should().Be(approver.ToString());
        (await ApproveAsShownAsync(third, "DMO", item.Id)).ErrorMessages.Should().Equal(AdvertisingMediaErrors.NotDraft);

        // The approver changes it: back to draft, and neither the approver (now its last changer) nor the uploader approves it.
        var changed = await _host.AsCallerAsync(approver, s => Media(s).ChangeAsync("DMO", item.Id, Change(Last.AddDays(30)), Ct));
        changed.Data.Should().Match<AdvertisingMediaViewModel>(m => m.Status == "Draft" && m.ApprovedBy == null && !m.Playable && m.EndsOn == Last.AddDays(30));
        (await ApproveAsShownAsync(approver, "DMO", item.Id)).ErrorMessages.Should().Equal(AdvertisingMediaErrors.SamePerson);
        (await ApproveAsShownAsync(uploader, "DMO", item.Id)).ErrorMessages.Should().Equal(AdvertisingMediaErrors.SamePerson);
        (await ApproveAsShownAsync(third, "DMO", item.Id)).HasErrors.Should().BeFalse("a third person approves the change");

        var actions = await _host.ReadAsync<string>(
            "SELECT string_agg(action || ':' || actor_id::text, ',' ORDER BY occurred_on, id) FROM audit_entry WHERE target_id = @id", item.Id);
        actions.Should().Be($"AdvertisingMedia.Uploaded:{uploader},AdvertisingMedia.Approved:{approver},AdvertisingMedia.Changed:{approver},AdvertisingMedia.Approved:{third}");
    }

    [Fact]
    public async Task Approve_Should_BeRefused_When_TheItemChangedSinceItsApproverWasShownIt()
    {
        var uploader = await ManagerAsync("it.media.review.uploader");
        var approver = await ManagerAsync("it.media.review.approver");
        var item = await UploadedAsync(uploader);
        var shown = (await _host.AsCallerAsync(approver, s => Media(s).GetAsync("DMO", item.Id, Ct))).Data.ReviewToken;

        // The uploader widens the dates after the approver looked: the approval of what was shown is refused.
        (await _host.AsCallerAsync(uploader, s => Media(s).ChangeAsync("DMO", item.Id, Change(First.AddDays(700)), Ct))).HasErrors.Should().BeFalse();
        var stale = await _host.AsCallerAsync(approver, s => Media(s).ApproveAsync("DMO", item.Id, new ApproveAdvertisingMediaRequest(shown), Ct));

        stale.ErrorMessages.Should().Equal(AdvertisingMediaErrors.ChangedSinceReview);
        (await _host.ReadAsync<string>("SELECT status FROM advertising_media WHERE id = @id", item.Id)).Should().Be("Draft");
        (await _host.AsCallerAsync(approver, s => Media(s).ApproveAsync("DMO", item.Id, new ApproveAdvertisingMediaRequest(null), Ct)))
            .ErrorMessages.Should().Equal([AdvertisingMediaErrors.ChangedSinceReview], "no token is no review");
        (await ApproveAsShownAsync(approver, "DMO", item.Id)).HasErrors.Should().BeFalse("approved once looked at again");
    }

    [Fact]
    public async Task Upload_Should_KeepALibraryToItsLimit_When_TwoUploadsRaceForItsLastPlace()
    {
        var first = await ManagerAsync("it.media.cap.first", "MED3");
        var second = await ManagerAsync("it.media.cap.second", "MED3");
        (await _host.ReadAsync<int>(
            """
            INSERT INTO advertising_media (id, site_code, storage_key, content_type, size_bytes, sha256, width_pixels, height_pixels, advertiser, campaign,
                                           starts_on, ends_on, status, uploaded_by_id, changed_by_id, created_on)
            SELECT gen_random_uuid(), 'MED3', md5(g::text || 'filler') || '.png', 'image/png', 1, repeat('a', 64), 1, 1, 'Filler', 'Filler',
                   '2026-09-30', '2026-10-31', 'Draft', u, u, now() - interval '1 day'
            FROM (SELECT g, gen_random_uuid() AS u FROM generate_series(1, 499) g) filler
            RETURNING 1
            """)).Should().Be(1);

        var results = await Task.WhenAll(
            _host.AsCallerAsync(first, s => Media(s).UploadAsync("MED3", Upload(Png()), Ct)),
            _host.AsCallerAsync(second, s => Media(s).UploadAsync("MED3", Upload(Png()), Ct)));

        results.Count(r => !r.HasErrors).Should().Be(1, "one upload takes the last place");
        results.Single(r => r.HasErrors).ErrorMessages.Should().Equal(AdvertisingMediaErrors.LibraryFull);
        (await _host.ReadAsync<long>("SELECT count(*) FROM advertising_media WHERE site_code = 'MED3'")).Should().Be(AdvertisingMedia.MaxItemsPerSite);
        (await _host.AsCallerAsync(first, s => Media(s).UploadAsync("MED3", Upload(Png()), Ct))).ErrorMessages.Should().Equal(AdvertisingMediaErrors.LibraryFull);
    }

    [Fact]
    public async Task Upload_Should_CountEveryAttemptAgainstThePersonsWindow_When_FilesAreRefusedToo()
    {
        var manager = await ManagerAsync("it.media.window");
        var refused = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        for (var attempt = 0; attempt < 30; attempt++)
            (await _host.AsCallerAsync(manager, s => Media(s).UploadAsync("DMO", Upload(refused), Ct))).ErrorMessages.Should().Equal(AdvertisingMediaErrors.UnsupportedFile);

        (await _host.AsCallerAsync(manager, s => Media(s).UploadAsync("DMO", Upload(Png()), Ct))).ErrorMessages
            .Should().Equal([AdvertisingMediaErrors.TooManyUploads], "the 31st attempt in 10 minutes, whatever the first 30 were");
        var other = await ManagerAsync("it.media.window.other");
        (await _host.AsCallerAsync(other, s => Media(s).UploadAsync("DMO", Upload(Png()), Ct))).HasErrors.Should().BeFalse("another person's own window");

        _host.Clock.Advance(TimeSpan.FromMinutes(10));
        (await _host.AsCallerAsync(manager, s => Media(s).UploadAsync("DMO", Upload(Png()), Ct))).HasErrors.Should().BeFalse("a new window");
    }

    [Fact]
    public async Task Lookup_Should_AnswerNotFound_When_TheItemIsAnotherSitesOrTheCallerCannotSeeTheSite()
    {
        var both = await ManagerAsync("it.media.idor.both", "DMO", "MED2");
        var other = await ManagerAsync("it.media.idor.other", "MED2");
        var item = await UploadedAsync(both);

        // Under another site's route, by a caller who sees both sites: not found, whatever the action.
        (await _host.AsCallerAsync(both, s => Media(s).GetAsync("MED2", item.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(both, s => Media(s).OpenAsync("MED2", item.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(both, s => Media(s).ChangeAsync("MED2", item.Id, Change(), Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(both, s => Media(s).RetireAsync("MED2", item.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(other, s => Media(s).ApproveAsync("MED2", item.Id, new ApproveAdvertisingMediaRequest(item.ReviewToken), Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        // A caller without the site: the library and the item do not exist for it.
        (await _host.AsCallerAsync(other, s => Media(s).ListAsync("DMO", Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(other, s => Media(s).GetAsync("DMO", item.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(other, s => Media(s).UploadAsync("DMO", Upload(Png()), Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(both, s => Media(s).ListAsync("MED2", Ct))).Data.Should().NotContain(m => m.Id == item.Id);
        (await _host.AsCallerAsync(both, s => Media(s).ListAsync("DMO", Ct))).Data.Should().Contain(m => m.Id == item.Id);
        (await _host.ReadAsync<string>("SELECT status FROM advertising_media WHERE id = @id", item.Id)).Should().Be("Draft", "nothing was changed");
    }

    [Fact]
    public async Task Retire_Should_StopTheItemForGood_When_Retired()
    {
        var uploader = await ManagerAsync("it.media.retire.uploader");
        var approver = await ManagerAsync("it.media.retire.approver");
        var item = await ApprovedAsync(uploader, approver);

        var retired = await _host.AsCallerAsync(uploader, s => Media(s).RetireAsync("DMO", item.Id, Ct));
        retired.Data.Should().Match<AdvertisingMediaViewModel>(m => m.Status == "Retired" && !m.Playable && m.RetiredOn != null && m.ApprovedBy != null, "the approval stays in its history");
        (await _host.AsCallerAsync(uploader, s => Media(s).RetireAsync("DMO", item.Id, Ct))).ErrorMessages.Should().Equal(AdvertisingMediaErrors.Retired);
        (await _host.AsCallerAsync(uploader, s => Media(s).ChangeAsync("DMO", item.Id, Change(), Ct))).ErrorMessages.Should().Equal(AdvertisingMediaErrors.Retired);
        (await ApproveAsShownAsync(approver, "DMO", item.Id)).ErrorMessages.Should().Equal(AdvertisingMediaErrors.Retired);
    }

    [Fact]
    public async Task OpenForDisplay_Should_ServeOnlyWhatMayPlayOnTheDisplayNow_When_ADisplayFetches()
    {
        var admin = await AdminAsync();
        var uploader = await ManagerAsync("it.media.display.uploader", "DMO", "MED2");
        var approver = await ManagerAsync("it.media.display.approver", "DMO", "MED2");
        var playing = await ApprovedAsync(uploader, approver);
        var draft = await UploadedAsync(uploader);
        var future = await ApprovedAsync(uploader, approver, first: Last.AddDays(1), last: Last.AddDays(10));
        var theirs = await ApprovedAsync(uploader, approver, "MED2");
        var advertising = await DisplayAsync(admin, "MED-AD", "A-CIT", "Advertising");
        var instructions = await DisplayAsync(admin, "MED-IN", "A-CIT", "Instructions");

        async Task<bool> ServedAsync(Guid id, (Guid Id, string Prefix) display)
        {
            var result = await _host.AsCallerAsync(null, s => Media(s).OpenForDisplayAsync(display.Id, display.Prefix, id, Ct));
            if (result.HasErrors)
            {
                result.ErrorMessages.Should().Equal(TopologyErrors.NotFound);
                return false;
            }

            await result.Data.Content.DisposeAsync();
            return true;
        }

        (await ServedAsync(playing.Id, advertising)).Should().BeTrue();
        (await ServedAsync(draft.Id, advertising)).Should().BeFalse("a draft");
        (await ServedAsync(future.Id, advertising)).Should().BeFalse("before its first day");
        (await ServedAsync(theirs.Id, advertising)).Should().BeFalse("another site's approved item");
        (await ServedAsync(playing.Id, instructions)).Should().BeFalse("a display set to instructions");
        (await ServedAsync(playing.Id, (advertising.Id, "ardp_wrongpre"))).Should().BeFalse("another credential's prefix");

        // The site's policy, read on every fetch: an excluded zone, then advertising off.
        var reviewed = (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAdvertisingPolicies>().GetAsync("DMO", Ct))).Data.PublishedProfileId;
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAdvertisingPolicies>().ChangeAsync("DMO",
            new AdvertisingPolicyRequest(true, ["A-CIT"], "Airport operator letter AO-IT-142a, citizens hall excluded", reviewed), Ct))).HasErrors.Should().BeFalse();
        (await ServedAsync(playing.Id, advertising)).Should().BeFalse("the display's zone is excluded");
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAdvertisingPolicies>().ChangeAsync("DMO",
            new AdvertisingPolicyRequest(false, [], "Border authority asked for no advertising, ref BA-142"), Ct))).HasErrors.Should().BeFalse();
        (await ServedAsync(playing.Id, advertising)).Should().BeFalse("advertising is off");
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAdvertisingPolicies>().ChangeAsync("DMO",
            new AdvertisingPolicyRequest(true, [], "Airport operator letter AO-IT-142a, commercial team", reviewed), Ct))).HasErrors.Should().BeFalse();
        (await ServedAsync(playing.Id, advertising)).Should().BeTrue("allowed again");
    }

    public static TheoryData<string, string> RuntimeRefusals => new()
    {
        { "approved by its uploader", "UPDATE advertising_media SET status = 'Approved', approved_by_id = uploaded_by_id, approved_by = 'x', approved_on = now() WHERE id = @id" },
        { "approved by its last changer", "UPDATE advertising_media SET status = 'Approved', approved_by_id = changed_by_id, approved_by = 'x', approved_on = now() WHERE id = @id" },
        { "approved with no approver", "UPDATE advertising_media SET status = 'Approved' WHERE id = @id" },
        { "approved and changed in one write", "UPDATE advertising_media SET status = 'Approved', approved_by_id = gen_random_uuid(), approved_by = 'x', approved_on = now(), advertiser = 'Other' WHERE id = @id" },
        { "its file replaced", "UPDATE advertising_media SET storage_key = '00000000000000000000000000000000.png' WHERE id = @id" },
        { "its hash replaced", "UPDATE advertising_media SET sha256 = repeat('0', 64) WHERE id = @id" },
        { "its uploader replaced", "UPDATE advertising_media SET uploaded_by_id = gen_random_uuid() WHERE id = @id" },
        { "moved to another site", "UPDATE advertising_media SET site_code = 'MED2' WHERE id = @id" },
        { "an SVG type", "UPDATE advertising_media SET content_type = 'image/svg+xml' WHERE id = @id" },
        { "its last day before its first", "UPDATE advertising_media SET ends_on = '2026-01-01' WHERE id = @id" },
        { "a date that does not exist", "UPDATE advertising_media SET ends_on = '2026-02-30' WHERE id = @id" },
        { "inserted as approved", InsertCopy("'Approved', uploaded_by_id, changed_by_id, gen_random_uuid(), 'x', now()") },
        { "inserted with another last changer", InsertCopy("'Draft', uploaded_by_id, gen_random_uuid(), NULL, NULL, NULL") },
        { "retired with its uploader as approver", "UPDATE advertising_media SET status = 'Retired', retired_on = now(), approved_by_id = uploaded_by_id, approved_by = 'x', approved_on = now() WHERE id = @id" },
        { "retired with another approval", "UPDATE advertising_media SET status = 'Retired', retired_on = now(), approved_by_id = gen_random_uuid(), approved_by = 'x', approved_on = now() WHERE id = @id" },
        { "retired with its details changed", "UPDATE advertising_media SET status = 'Retired', retired_on = now(), campaign = 'Other' WHERE id = @id" },
        { "its creator replaced", "UPDATE advertising_media SET created_by = 'someone else' WHERE id = @id" },
        { "its creation time moved", "UPDATE advertising_media SET created_on = created_on - interval '1 day' WHERE id = @id" }
    };

    /// <summary>A copy of the item under a new id and key, written by the runtime role with the given state and people.</summary>
    private static string InsertCopy(string stateAndPeople) =>
        "INSERT INTO advertising_media (id, site_code, storage_key, content_type, size_bytes, sha256, width_pixels, height_pixels, advertiser, campaign, " +
        "starts_on, ends_on, status, uploaded_by_id, changed_by_id, approved_by_id, approved_by, approved_on) " +
        "SELECT gen_random_uuid(), site_code, md5(random()::text) || '.png', content_type, size_bytes, sha256, width_pixels, height_pixels, advertiser, campaign, " +
        "starts_on, ends_on, " + stateAndPeople + " FROM advertising_media WHERE id = @id";

    [Theory]
    [MemberData(nameof(RuntimeRefusals))]
    public async Task Database_Should_RefuseTheRuntimeRole_When_AWriteBreaksTheTwoPersonRuleOrTheFile(string reason, string sql)
    {
        var uploader = await ManagerAsync($"it.media.rt.{Math.Abs(reason.GetHashCode(StringComparison.Ordinal)) % 100000}");
        var item = await UploadedAsync(uploader);

        await using var runtime = await RuntimeAsync();
#pragma warning disable CA2100 // literal statements from the theory data above
        await using var command = new NpgsqlCommand(sql, runtime.Connection, runtime.Transaction);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("id", item.Id);
        var writing = () => command.ExecuteNonQueryAsync(Ct);

        (await writing.Should().ThrowAsync<PostgresException>(reason)).Which.SqlState.Should().BeOneOf(["23514", "22008"], reason);
    }

    [Fact]
    public async Task Database_Should_KeepApprovedAndRetiredItemsAndRefuseDeletes_When_TheRuntimeRoleWrites()
    {
        var uploader = await ManagerAsync("it.media.rt.kept.uploader");
        var approver = await ManagerAsync("it.media.rt.kept.approver");
        var approved = await ApprovedAsync(uploader, approver);
        var retired = await ApprovedAsync(uploader, approver);
        (await _host.AsCallerAsync(uploader, s => Media(s).RetireAsync("DMO", retired.Id, Ct))).HasErrors.Should().BeFalse();

        foreach (var (reason, sql, id, state) in new (string, string, Guid, string)[]
                 {
                     ("an approved item's details changed in place", "UPDATE advertising_media SET campaign = 'Changed' WHERE id = @id", approved.Id, "23514"),
                     ("an approved item's dates moved in place", "UPDATE advertising_media SET ends_on = '2026-12-31' WHERE id = @id", approved.Id, "23514"),
                     ("a retired item restored", "UPDATE advertising_media SET status = 'Draft', approved_by_id = NULL, approved_by = NULL, approved_on = NULL, retired_on = NULL WHERE id = @id", retired.Id, "23514"),
                     ("a retired item changed", "UPDATE advertising_media SET advertiser = 'Changed' WHERE id = @id", retired.Id, "23514"),
                     ("a delete", "DELETE FROM advertising_media WHERE id = @id", approved.Id, "42501")
                 })
        {
            await using var runtime = await RuntimeAsync();
#pragma warning disable CA2100 // literal statements from the array above
            await using var command = new NpgsqlCommand(sql, runtime.Connection, runtime.Transaction);
#pragma warning restore CA2100
            command.Parameters.AddWithValue("id", id);
            var writing = () => command.ExecuteNonQueryAsync(Ct);
            (await writing.Should().ThrowAsync<PostgresException>(reason)).Which.SqlState.Should().Be(state, reason);
        }

        // What the service does is allowed: a change of an approved item that sends it back to draft.
        await using (var runtime = await RuntimeAsync())
        {
            await using var command = new NpgsqlCommand(
                "UPDATE advertising_media SET status = 'Draft', approved_by_id = NULL, approved_by = NULL, approved_on = NULL, campaign = 'Changed', changed_by_id = @changer WHERE id = @id",
                runtime.Connection, runtime.Transaction);
            command.Parameters.AddWithValue("id", approved.Id);
            command.Parameters.AddWithValue("changer", approver);
            (await command.ExecuteNonQueryAsync(Ct)).Should().Be(1);
        }
    }

    [Fact]
    public async Task Approve_Should_WaitForAChangeInProgressAndJudgeWhatItLeft_When_BothTouchOneItem()
    {
        var uploader = await ManagerAsync("it.media.race.uploader");
        var approver = await ManagerAsync("it.media.race.approver");
        var item = await UploadedAsync(uploader);

        // Another session holds the item's row and changes it as the approver; the approval waits, then sees the change.
        await using var holder = await RuntimeAsync();
        await using (var hold = new NpgsqlCommand("UPDATE advertising_media SET campaign = 'Changed meanwhile', changed_by_id = @changer WHERE id = @id",
                         holder.Connection, holder.Transaction))
        {
            hold.Parameters.AddWithValue("id", item.Id);
            hold.Parameters.AddWithValue("changer", approver);
            (await hold.ExecuteNonQueryAsync(Ct)).Should().Be(1);
        }

        var approving = _host.AsCallerAsync(approver, s => Media(s).ApproveAsync("DMO", item.Id, new ApproveAdvertisingMediaRequest(item.ReviewToken), Ct));
        await WaitForAsync("SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%FROM advertising_media WHERE id%FOR UPDATE%'");
        approving.IsCompleted.Should().BeFalse("the approval waits for the row");
        await holder.Transaction.CommitAsync(Ct);

        (await approving).ErrorMessages.Should().Equal([AdvertisingMediaErrors.ChangedSinceReview], "the approval judged the item as the change left it, not as it was shown");
        (await _host.ReadAsync<string>("SELECT status FROM advertising_media WHERE id = @id", item.Id)).Should().Be("Draft");
    }

    /// <summary>Approves the item as the caller is shown it now (its review token read first, as the screen does).</summary>
    private async Task<Fluentx.Result<AdvertisingMediaViewModel>> ApproveAsShownAsync(Guid caller, string site, Guid id)
    {
        var shown = await _host.AsCallerAsync(caller, s => Media(s).GetAsync(site, id, Ct));
        return await _host.AsCallerAsync(caller, s => Media(s).ApproveAsync(site, id, new ApproveAdvertisingMediaRequest(shown.Data?.ReviewToken), Ct));
    }

    private async Task<(Guid Id, string Prefix)> DisplayAsync(Guid admin, string code, string zone, string secondRegion)
    {
        var request = new DisplayRequest("DMO", code, "Arrivals hall", null, "Landscape", ["en"], 5, 1, 150,
            [new DisplayEntryRequest(zone, new Dictionary<string, string> { ["en"] = "Citizens" })], new Dictionary<string, string> { ["en"] = "Please follow the signs" },
            true, "Split", 60, secondRegion);
        var created = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDisplays>().CreateAsync(request, Ct));
        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        return (created.Data.Display.Id, created.Data.Credential[..DisplayCredentials.PrefixLength]);
    }

    private sealed class Runtime(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public NpgsqlConnection Connection { get; } = connection;
        public NpgsqlTransaction Transaction { get; } = transaction;

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    /// <summary>A connection with an open transaction as the runtime role (rolled back when disposed unless committed).</summary>
    private async Task<Runtime> RuntimeAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
        return new Runtime(connection, transaction);
    }

    /// <summary>Waits until the count is above zero (another session is waiting on a lock), for at most 20 seconds.</summary>
    private async Task WaitForAsync([System.Diagnostics.CodeAnalysis.ConstantExpected] string countSql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        for (var attempt = 0; attempt < 200; attempt++)
        {
#pragma warning disable CA2100 // test helper: every caller passes a literal (ConstantExpected, CA1857 is an error)
            await using var command = new NpgsqlCommand(countSql, connection);
#pragma warning restore CA2100
            if ((long)(await command.ExecuteScalarAsync(Ct))! > 0)
                return;
            await Task.Delay(100, Ct);
        }

        throw new TimeoutException("No session waited on the lock.");
    }
}
