using System.Text;
using Ariva.Core;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Displays;
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
/// ARV-143a against PostgreSQL with script 0060: a playlist keeps items of its own site (an advertisement of the library
/// that is not retired, an active slide), a video's slot being its stored length; a refused playlist or schedule stores
/// nothing; a schedule plays an active playlist of its site on queue zones of the published profile or live displays;
/// another site's playlist or schedule answers like one that does not exist (IDOR); the timeline resolves a display's week
/// in the site's time zone; the cap of 100 playlists holds when two creations race for the last place; and the database
/// refuses the runtime role writes that break the same rules (items of another site, a schedule of another site's
/// playlist, a delete). Its own database, so the advertising policy of DMO here reaches no other test.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AdvertisingPlansServiceTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim SeedGate = new(1, 1);
    private static Guid? _admin;

    /// <summary>The host's clock starts on Thursday 2026-10-01 08:00 UTC: noon at DMO (Asia/Dubai, UTC+4).</summary>
    private static readonly DateOnly First = new(2026, 9, 30);

    private static readonly DateOnly Last = new(2026, 10, 31);

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.AdvertisingPlans);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ISvcAdvertisingPlaylists Playlists(IServiceProvider s) => s.GetRequiredService<ISvcAdvertisingPlaylists>();

    private static ISvcAdvertisingSchedules Schedules(IServiceProvider s) => s.GetRequiredService<ISvcAdvertisingSchedules>();

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(16, 8, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(SKColors.Teal);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] Video() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Media", "vp9-opus.webm"));

    private async Task<Guid> AdminAsync()
    {
        await SeedGate.WaitAsync(Ct);
        try
        {
            if (_admin is { } existing)
                return existing;
            var admin = await _host.CreateUserAsync("it.plans.admin", roles: [RoleCodes.SystemAdministrator]);
            await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
            await _host.AsCallerAsync(null, s =>
                new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
            await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("PLN2", "Advertising plans test site"), Ct));
            await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("PLN3", "Advertising plans cap site"), Ct));
            await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("PLN4", "Advertising schedules cap site"), Ct));
            var reviewed = (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAdvertisingPolicies>().GetAsync("DMO", Ct))).Data.PublishedProfileId;
            var allowed = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAdvertisingPolicies>().ChangeAsync("DMO",
                new AdvertisingPolicyRequest(true, [], "Airport operator letter AO-IT-143a, commercial team", reviewed), Ct));
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

    private async Task<AdvertisingMediaViewModel> MediaAsync(Guid caller, string site = "DMO", byte[] bytes = null, string name = "winter.png")
    {
        var bytesToSend = bytes ?? Png();
        var result = await _host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcAdvertisingMedia>().UploadAsync(site,
            new AdvertisingMediaUpload(new MemoryStream(bytesToSend), bytesToSend.Length, name, "Gulf Air", "Winter sun", First, Last), Ct));
        result.HasErrors.Should().BeFalse(string.Join(", ", result.ErrorMessages ?? []));
        return result.Data;
    }

    private async Task<Guid> SlideAsync(Guid caller, string site = "DMO", string title = "Have your passport ready")
    {
        var result = await _host.AsCallerAsync(caller, s => s.GetRequiredService<ISvcInstructionSlides>().CreateAsync(new InstructionSlideRequest(site, "Site", [],
            "Passport", 8, false, new Dictionary<string, InstructionTextRequest> { ["en"] = new(title, null) }), Ct));
        result.HasErrors.Should().BeFalse(string.Join(", ", result.ErrorMessages ?? []));
        return result.Data.Id;
    }

    private static AdvertisingPlaylistRequest Playlist(string name, params AdvertisingPlaylistItemRequest[] items) => new(name, items);

    private static AdvertisingPlaylistItemRequest Item(string kind, Guid id, int? seconds = 10, int? weight = null) => new(kind, id, seconds, weight);

    private async Task<AdvertisingPlaylistViewModel> CreatedPlaylistAsync(Guid caller, string site, AdvertisingPlaylistRequest request)
    {
        var result = await _host.AsCallerAsync(caller, s => Playlists(s).CreateAsync(site, request, Ct));
        result.HasErrors.Should().BeFalse(string.Join(", ", result.ErrorMessages ?? []));
        return result.Data;
    }

    private static AdvertisingScheduleRequest Schedule(Guid playlist, string scope = "Site", string[] targets = null, string[] days = null, string opens = "00:00",
        string closes = "24:00") =>
        new(playlist, scope, targets ?? [], days ?? ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"], opens, closes);

    private async Task<string> DisplayAsync(Guid admin, string code, string zone)
    {
        var request = new DisplayRequest("DMO", code, "Arrivals hall", null, "Landscape", ["en"], 5, 1, 150,
            [new DisplayEntryRequest(zone, new Dictionary<string, string> { ["en"] = "Citizens" })], new Dictionary<string, string> { ["en"] = "Please follow the signs" },
            true, "Split", 60, "Advertising");
        var created = await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDisplays>().CreateAsync(request, Ct));
        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        return code;
    }

    [Fact]
    public async Task Playlist_Should_KeepItemsOfItsSiteAsAnAuditedRow_When_ItNamesAnImageAVideoAndASlide()
    {
        var manager = await ManagerAsync("it.plans.create");
        var image = await MediaAsync(manager);
        var video = await MediaAsync(manager, bytes: Video(), name: "spot.webm");
        var slide = await SlideAsync(await AdminAsync());

        var created = await CreatedPlaylistAsync(manager, "DMO", Playlist("  Morning bank  ", Item("Media", image.Id, 12, 3), Item("Media", video.Id, 30), Item("Slide", slide, 8)));

        created.Should().Match<AdvertisingPlaylistViewModel>(p => p.Name == "Morning bank" && p.Status == "Active" && p.SiteCode == "DMO");
        created.Items.Select(i => (i.Kind, i.Id, i.Seconds, i.Weight)).Should().Equal(("Media", image.Id, 12, 3), ("Media", video.Id, 3, 1), ("Slide", slide, 8, 1));
        created.Items[0].Should().Match<AdvertisingPlaylistItemViewModel>(i => i.Label == "Gulf Air: Winter sun" && !i.Playable && i.MediaStatus == "Draft",
            "a draft is listed and plays only once approved");
        created.Items[1].ContentType.Should().Be("video/webm", "a video's slot is its own length, rounded up, whatever was asked");
        created.Items[2].Should().Match<AdvertisingPlaylistItemViewModel>(i => i.Label == "Have your passport ready" && i.Playable);
        created.TotalSeconds.Should().Be(23);
        await using (var shorter = new AccountsHost(fixture, new() { ["Advertising:Videos:MaxSeconds"] = "2" }, TestDatabase.AdvertisingPlans))
        {
            await shorter.CreateUserAsync("it.plans.shorter"); // starts the second host on the same database
            var capped = await shorter.AsCallerAsync(manager, s => Playlists(s).CreateAsync("DMO", Playlist("Short spots", Item("Media", video.Id, 30)), Ct));
            capped.Data.Items.Should().ContainSingle().Which.Seconds.Should().Be(2, "an installation that lowered its video length plays a longer video for that long");
        }

        (await _host.ReadAsync<string>("SELECT items FROM advertising_playlist WHERE id = @id", created.Id)).Should().Contain("\"kind\":\"Media\"");
        (await _host.ReadAsync<string>("SELECT actor_id::text || ' ' || after_summary FROM audit_entry WHERE action = 'AdvertisingPlaylist.Created' AND target_id = @id",
            created.Id)).Should().StartWith($"{manager} ").And.Contain($"M:{image.Id:N}:12:3").And.Contain($"S:{slide:N}:8:1");
    }

    [Fact]
    public async Task Playlist_Should_StoreNothing_When_AnItemIsNotTheSitesOwnOrBreaksARule()
    {
        var manager = await ManagerAsync("it.plans.refused", "DMO", "PLN2");
        var admin = await AdminAsync();
        var ours = await MediaAsync(manager);
        var theirs = await MediaAsync(manager, "PLN2");
        var retired = await MediaAsync(manager);
        (await _host.AsCallerAsync(manager, s => s.GetRequiredService<ISvcAdvertisingMedia>().RetireAsync("DMO", retired.Id, Ct))).HasErrors.Should().BeFalse();
        var oldSlide = await SlideAsync(admin, title: "Old slide");
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcInstructionSlides>().RetireAsync(oldSlide, Ct))).HasErrors.Should().BeFalse();
        var before = await _host.ReadAsync<long>("SELECT count(*) FROM advertising_playlist");

        foreach (var (reason, request, error) in new (string, AdvertisingPlaylistRequest, string)[]
                 {
                     ("another site's advertisement", Playlist("Bank", Item("Media", theirs.Id)), AdvertisingPlanErrors.UnknownItems),
                     ("a retired advertisement", Playlist("Bank", Item("Media", retired.Id)), AdvertisingPlanErrors.UnknownItems),
                     ("a retired slide", Playlist("Bank", Item("Slide", oldSlide)), AdvertisingPlanErrors.UnknownItems),
                     ("an id that is no item", Playlist("Bank", Item("Media", Guid.CreateVersion7())), AdvertisingPlanErrors.UnknownItems),
                     ("an advertisement named as a slide", Playlist("Bank", Item("Slide", ours.Id)), AdvertisingPlanErrors.UnknownItems),
                     ("an item twice", Playlist("Bank", Item("Media", ours.Id), Item("Media", ours.Id)), AdvertisingPlanErrors.UnknownItems),
                     ("an image for 3 seconds", Playlist("Bank", Item("Media", ours.Id, 3)), AdvertisingPlanErrors.ImageSeconds),
                     ("an image without seconds", Playlist("Bank", Item("Media", ours.Id, null)), AdvertisingPlanErrors.ImageSeconds),
                     ("a kind in lower case", Playlist("Bank", Item("media", ours.Id)), AdvertisingPlanErrors.UnknownKind),
                     ("a kind that is a number", Playlist("Bank", Item("0", ours.Id)), AdvertisingPlanErrors.UnknownKind),
                     ("no items", Playlist("Bank"), "A playlist has 1 to 40 items.")
                 })
        {
            var result = await _host.AsCallerAsync(manager, s => Playlists(s).CreateAsync("DMO", request, Ct));
            result.ErrorMessages.Should().Contain(error, reason);
        }

        (await _host.ReadAsync<long>("SELECT count(*) FROM advertising_playlist")).Should().Be(before, "nothing refused is stored");
    }

    [Fact]
    public async Task Schedule_Should_PlayAnActivePlaylistOfItsSiteOnItsZonesAndDisplays_When_Saved()
    {
        var manager = await ManagerAsync("it.plans.schedule", "DMO", "PLN2");
        var admin = await AdminAsync();
        var media = await MediaAsync(manager);
        var playlist = await CreatedPlaylistAsync(manager, "DMO", Playlist("Day", Item("Media", media.Id)));
        var theirs = await CreatedPlaylistAsync(manager, "PLN2", Playlist("Theirs", Item("Media", (await MediaAsync(manager, "PLN2")).Id)));
        var retired = await CreatedPlaylistAsync(manager, "DMO", Playlist("Old", Item("Media", media.Id)));
        (await _host.AsCallerAsync(manager, s => Playlists(s).RetireAsync("DMO", retired.Id, Ct))).HasErrors.Should().BeFalse();
        await DisplayAsync(admin, "PLN-SCH", "A-CIT");
        var before = await _host.ReadAsync<long>("SELECT count(*) FROM advertising_schedule");

        foreach (var (reason, request, error) in new (string, AdvertisingScheduleRequest, string)[]
                 {
                     ("another site's playlist", Schedule(theirs.Id), AdvertisingPlanErrors.UnknownPlaylist),
                     ("a retired playlist", Schedule(retired.Id), AdvertisingPlanErrors.UnknownPlaylist),
                     ("a zone that is not a queue zone of the published profile", Schedule(playlist.Id, "Zone", ["Nowhere"]), InstructionSlideErrors.UnknownZones),
                     ("a display that does not exist", Schedule(playlist.Id, "Display", ["NO-SUCH"]), InstructionSlideErrors.UnknownDisplays),
                     ("a scope in lower case", Schedule(playlist.Id, "site"), AdvertisingPlanErrors.UnknownScope),
                     ("no day", Schedule(playlist.Id, days: []), "A schedule names 1 to 7 different days: Monday, Tuesday, Wednesday, Thursday, Friday, Saturday or Sunday."),
                     ("a window of no length", Schedule(playlist.Id, opens: "10:00", closes: "10:00"), Core.Availability.WeeklyHours.InvalidInterval)
                 })
        {
            var result = await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", request, Ct));
            result.ErrorMessages.Should().Contain(error, reason);
        }

        (await _host.ReadAsync<long>("SELECT count(*) FROM advertising_schedule")).Should().Be(before, "nothing refused is stored");

        var created = await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(playlist.Id, "Zone", ["A-CIT"], ["Friday", "Monday"], "22:00", "02:00"), Ct));
        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        created.Data.Should().Match<AdvertisingScheduleViewModel>(s => s.PlaylistName == "Day" && s.PlaylistActive && s.Scope == "Zone" && s.Opens == "22:00" &&
                                                                        s.Closes == "02:00" && s.Status == "Active");
        created.Data.Days.Should().Equal("Monday", "Friday");
        var display = await _host.AsCallerAsync(manager, s => Schedules(s).UpdateAsync("DMO", created.Data.Id, Schedule(playlist.Id, "Display", ["PLN-SCH"]), Ct));
        display.Data.Targets.Should().Equal("PLN-SCH");
        (await _host.AsCallerAsync(manager, s => Schedules(s).RetireAsync("DMO", created.Data.Id, Ct))).Data.Status.Should().Be("Retired");
        (await _host.AsCallerAsync(manager, s => Schedules(s).RestoreAsync("DMO", created.Data.Id, Ct))).Data.Status.Should().Be("Active");
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE target_id = @id AND action LIKE 'AdvertisingSchedule.%'", created.Data.Id))
            .Should().Be(4, "created, updated, retired and restored");
    }

    [Fact]
    public async Task Lookup_Should_AnswerNotFound_When_ThePlanIsAnotherSitesOrTheCallerCannotSeeTheSite()
    {
        var manager = await ManagerAsync("it.plans.idor", "DMO", "PLN2");
        var outsider = await ManagerAsync("it.plans.outsider", "PLN2");
        var media = await MediaAsync(manager);
        var ours = await CreatedPlaylistAsync(manager, "DMO", Playlist("Ours", Item("Media", media.Id)));
        var theirs = await CreatedPlaylistAsync(manager, "PLN2", Playlist("Theirs", Item("Media", (await MediaAsync(manager, "PLN2")).Id)));
        var schedule = (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(ours.Id), Ct))).Data;

        (await _host.AsCallerAsync(manager, s => Playlists(s).GetAsync("DMO", theirs.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(manager, s => Playlists(s).UpdateAsync("DMO", theirs.Id, Playlist("Taken", Item("Media", media.Id)), Ct))).ErrorMessages
            .Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(manager, s => Playlists(s).RetireAsync("DMO", theirs.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(manager, s => Schedules(s).GetAsync("PLN2", schedule.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(outsider, s => Playlists(s).ListAsync("DMO", Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(outsider, s => Schedules(s).RetireAsync("DMO", schedule.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(outsider, s => Schedules(s).TimelineAsync("DMO", "PLN-SCH", Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.ReadAsync<string>("SELECT name FROM advertising_playlist WHERE id = @id", theirs.Id)).Should().Be("Theirs");
        (await _host.ReadAsync<string>("SELECT status FROM advertising_schedule WHERE id = @id", schedule.Id)).Should().Be("Active");
    }

    [Fact]
    public async Task Timeline_Should_ResolveTheDisplaysWeekInTheSitesTimeZone_When_SchedulesOverlap()
    {
        var manager = await ManagerAsync("it.plans.timeline");
        var admin = await AdminAsync();
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync())))
        {
            // The class shares one database: the other tests' DMO schedules would apply to this display too.
            await connection.OpenAsync(Ct);
            await using var others = new NpgsqlCommand("UPDATE advertising_schedule SET status = 'Retired' WHERE site_code = 'DMO'", connection);
            await others.ExecuteNonQueryAsync(Ct);
        }

        var media = await MediaAsync(manager);
        var allDay = await CreatedPlaylistAsync(manager, "DMO", Playlist("All day", Item("Media", media.Id)));
        var peak = await CreatedPlaylistAsync(manager, "DMO", Playlist("Peak bank", Item("Media", media.Id)));
        await DisplayAsync(admin, "PLN-TML", "A-CIT");
        await DisplayAsync(admin, "PLN-OTH", "A-VIS");
        (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(allDay.Id), Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(peak.Id, "Zone", ["A-CIT"], ["Thursday"], "12:00", "14:00"), Ct)))
            .HasErrors.Should().BeFalse();

        var timeline = await _host.AsCallerAsync(manager, s => Schedules(s).TimelineAsync("DMO", "PLN-TML", Ct));

        timeline.HasErrors.Should().BeFalse(string.Join(", ", timeline.ErrorMessages ?? []));
        timeline.Data.TimeZone.Should().Be("Asia/Dubai");
        timeline.Data.SecondRegion.Should().Be("Advertising");
        timeline.Data.FromUtc.Should().Be(new DateTime(2026, 9, 30, 20, 0, 0, DateTimeKind.Utc), "midnight of Thursday 1 October at DMO");
        timeline.Data.ToUtc.Should().Be(timeline.Data.FromUtc.AddDays(7));
        var segments = timeline.Data.Segments.Select(s => (s.FromLocal, s.ToLocal, s.PlaylistName)).ToList();
        segments.Should().Equal(
            ("2026-10-01T00:00", "2026-10-01T12:00", "All day"),
            ("2026-10-01T12:00", "2026-10-01T14:00", "Peak bank"),
            ("2026-10-01T14:00", "2026-10-08T00:00", "All day"));
        timeline.Data.Segments[1].FromUtc.Should().Be(new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));
        (await _host.AsCallerAsync(manager, s => Schedules(s).TimelineAsync("DMO", "PLN-OTH", Ct))).Data.Segments.Should().ContainSingle()
            .Which.PlaylistName.Should().Be("All day", "the peak bank is for the citizens' hall only");

        (await _host.AsCallerAsync(manager, s => Playlists(s).RetireAsync("DMO", allDay.Id, Ct))).HasErrors.Should().BeFalse();
        var afterRetire = await _host.AsCallerAsync(manager, s => Schedules(s).TimelineAsync("DMO", "PLN-TML", Ct));
        afterRetire.Data.Segments.Select(s => s.PlaylistName).Should().Equal([null, "Peak bank", null], "a retired playlist's schedule stops applying: instructions");
        (await _host.AsCallerAsync(manager, s => Schedules(s).TimelineAsync("DMO", "NO-SUCH", Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(manager, s => Schedules(s).TimelineAsync("DMO", null, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
    }

    [Fact]
    public async Task Create_Should_KeepThePlaylistCap_When_TwoCreationsRaceForTheLastPlace()
    {
        var manager = await ManagerAsync("it.plans.cap", "PLN3");
        var media = await MediaAsync(manager, "PLN3");
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync())))
        {
            await connection.OpenAsync(Ct);
            await using var fill = new NpgsqlCommand(
                "INSERT INTO advertising_playlist (id, site_code, name, items, status) SELECT gen_random_uuid(), 'PLN3', 'Filler ' || n, " +
                "'[{\"kind\":\"Media\",\"id\":\"' || @media::text || '\",\"seconds\":10,\"weight\":1}]', 'Active' FROM generate_series(1, @count) n", connection);
            fill.Parameters.AddWithValue("media", media.Id);
            fill.Parameters.AddWithValue("count", AdvertisingPlaylist.MaxPlaylistsPerSite - 1);
            (await fill.ExecuteNonQueryAsync(Ct)).Should().Be(AdvertisingPlaylist.MaxPlaylistsPerSite - 1);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(n =>
            _host.AsCallerAsync(manager, s => Playlists(s).CreateAsync("PLN3", Playlist($"Last {n}", Item("Media", media.Id)), Ct))));

        results.Count(r => !r.HasErrors).Should().Be(1, "one takes the last place");
        results.Single(r => r.HasErrors).ErrorMessages.Should().Equal(AdvertisingPlanErrors.TooManyPlaylists);
        (await _host.ReadAsync<long>("SELECT count(*) FROM advertising_playlist WHERE site_code = 'PLN3'")).Should().Be(AdvertisingPlaylist.MaxPlaylistsPerSite);
    }

    [Fact]
    public async Task Create_Should_KeepTheScheduleCap_When_TwoCreationsRaceForTheLastPlace()
    {
        var manager = await ManagerAsync("it.plans.schedcap", "PLN4");
        var playlist = await CreatedPlaylistAsync(manager, "PLN4", Playlist("Capped", Item("Media", (await MediaAsync(manager, "PLN4")).Id)));
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync())))
        {
            await connection.OpenAsync(Ct);
            // Retired ones count: the cap is on rows, so retiring and creating again cannot pass it.
            await using var fill = new NpgsqlCommand(
                "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                "SELECT gen_random_uuid(), 'PLN4', @playlist, 'Site', '[]', 'Monday', 0, 60, CASE WHEN n % 2 = 0 THEN 'Retired' ELSE 'Active' END " +
                "FROM generate_series(1, @count) n", connection);
            fill.Parameters.AddWithValue("playlist", playlist.Id);
            fill.Parameters.AddWithValue("count", AdvertisingSchedule.MaxSchedulesPerSite - 1);
            (await fill.ExecuteNonQueryAsync(Ct)).Should().Be(AdvertisingSchedule.MaxSchedulesPerSite - 1);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("PLN4", Schedule(playlist.Id), Ct))));

        results.Count(r => !r.HasErrors).Should().Be(1, "one takes the last place");
        results.Single(r => r.HasErrors).ErrorMessages.Should().Equal(AdvertisingPlanErrors.TooManySchedules);
        AdvertisingPlanErrors.Conflicts.Should().Contain(AdvertisingPlanErrors.TooManySchedules, "the API answers it with 409");
        (await _host.ReadAsync<long>("SELECT count(*) FROM advertising_schedule WHERE site_code = 'PLN4'")).Should().Be(AdvertisingSchedule.MaxSchedulesPerSite);
        (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("PLN4", Schedule(playlist.Id), Ct))).ErrorMessages
            .Should().Equal(AdvertisingPlanErrors.TooManySchedules);
    }

    public static TheoryData<string, string> RuntimeRefusals => new()
    {
        { "another site's advertisement", "UPDATE advertising_playlist SET items = '[{\"kind\":\"Media\",\"id\":\"' || (SELECT id FROM advertising_media WHERE site_code = 'PLN2' LIMIT 1) || '\",\"seconds\":10,\"weight\":1}]' WHERE id = @id" },
        { "an id that is no item", "UPDATE advertising_playlist SET items = '[{\"kind\":\"Media\",\"id\":\"' || gen_random_uuid() || '\",\"seconds\":10,\"weight\":1}]' WHERE id = @id" },
        { "a kind that is not taken", "UPDATE advertising_playlist SET items = replace(items, '\"Media\"', '\"Video\"') WHERE id = @id" },
        { "no time on screen", "UPDATE advertising_playlist SET items = replace(items, '\"seconds\":10', '\"seconds\":0') WHERE id = @id" },
        { "a weight of 11", "UPDATE advertising_playlist SET items = replace(items, '\"weight\":1', '\"weight\":11') WHERE id = @id" },
        { "a fraction of a second", "UPDATE advertising_playlist SET items = replace(items, '\"seconds\":10', '\"seconds\":10.5') WHERE id = @id" },
        { "no items", "UPDATE advertising_playlist SET items = '[]' WHERE id = @id" },
        { "items that are not a list", "UPDATE advertising_playlist SET items = '{\"kind\":\"Media\"}' WHERE id = @id" },
        { "moved to another site", "UPDATE advertising_playlist SET site_code = 'PLN2' WHERE id = @id" },
        { "a status that is not taken", "UPDATE advertising_playlist SET status = 'Deleted' WHERE id = @id" },
        { "a schedule of another site's playlist", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                                  "SELECT gen_random_uuid(), 'PLN2', id, 'Site', '[]', 'Monday', 0, 1440, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "a schedule for the site naming a zone", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                                    "SELECT gen_random_uuid(), site_code, id, 'Site', '[\"A-CIT\"]', 'Monday', 0, 1440, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "a day that is not a day", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                     "SELECT gen_random_uuid(), site_code, id, 'Site', '[]', 'Funday', 0, 1440, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "a window opening at 24:00", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                       "SELECT gen_random_uuid(), site_code, id, 'Site', '[]', 'Monday', 1440, 60, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "zone targets that are not a list", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                              "SELECT gen_random_uuid(), site_code, id, 'Zone', '{\"zone\":\"A-CIT\"}', 'Monday', 0, 60, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "a zone target that is not text", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                            "SELECT gen_random_uuid(), site_code, id, 'Zone', '[1]', 'Monday', 0, 60, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "an empty zone target", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                  "SELECT gen_random_uuid(), site_code, id, 'Zone', '[\"\"]', 'Monday', 0, 60, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "a zone named twice", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                "SELECT gen_random_uuid(), site_code, id, 'Zone', '[\"A-CIT\",\"A-CIT\"]', 'Monday', 0, 60, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "21 displays", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                         "SELECT gen_random_uuid(), site_code, id, 'Display', (SELECT json_agg('D-' || n)::text FROM generate_series(1, 21) n), 'Monday', 0, 60, 'Active' " +
                         "FROM advertising_playlist WHERE id = @id" },
        { "a zone schedule naming an empty list written with a space", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                                                       "SELECT gen_random_uuid(), site_code, id, 'Zone', '[ ]', 'Monday', 0, 60, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "targets that are not JSON", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                       "SELECT gen_random_uuid(), site_code, id, 'Zone', 'A-CIT', 'Monday', 0, 60, 'Active' FROM advertising_playlist WHERE id = @id" },
        { "a schedule moved to another site", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                              "SELECT gen_random_uuid(), site_code, id, 'Site', '[]', 'Monday', 0, 60, 'Active' FROM advertising_playlist WHERE id = @id; " +
                                              "UPDATE advertising_schedule SET site_code = 'PLN2' WHERE playlist_id = @id" },
        { "a schedule given another id", "INSERT INTO advertising_schedule (id, site_code, playlist_id, scope, targets, days, opens_minute, length_minutes, status) " +
                                         "SELECT gen_random_uuid(), site_code, id, 'Site', '[]', 'Monday', 0, 60, 'Active' FROM advertising_playlist WHERE id = @id; " +
                                         "UPDATE advertising_schedule SET id = gen_random_uuid() WHERE playlist_id = @id" }
    };

    [Theory]
    [MemberData(nameof(RuntimeRefusals))]
    public async Task Database_Should_RefuseTheRuntimeRole_When_AWriteBreaksTheRules(string reason, string sql)
    {
        var manager = await ManagerAsync($"it.plans.rt.{Math.Abs(reason.GetHashCode(StringComparison.Ordinal)) % 100000}", "DMO", "PLN2");
        await MediaAsync(manager, "PLN2");
        var playlist = await CreatedPlaylistAsync(manager, "DMO", Playlist("Runtime", Item("Media", (await MediaAsync(manager)).Id)));

        await using var runtime = await RuntimeAsync();
#pragma warning disable CA2100 // literal statements from the theory data above
        await using var command = new NpgsqlCommand(sql, runtime.Connection, runtime.Transaction);
#pragma warning restore CA2100
        command.Parameters.AddWithValue("id", playlist.Id);
        var writing = () => command.ExecuteNonQueryAsync(Ct);

        (await writing.Should().ThrowAsync<PostgresException>(reason)).Which.SqlState.Should().BeOneOf(["23514", "23503", "22P02"], reason);
    }

    [Fact]
    public async Task Database_Should_RefuseDeletesAndKeepRetiredItemsInAPlaylist_When_TheRuntimeRoleWrites()
    {
        var manager = await ManagerAsync("it.plans.rt.kept");
        var media = await MediaAsync(manager);
        var playlist = await CreatedPlaylistAsync(manager, "DMO", Playlist("Kept", Item("Media", media.Id)));
        var schedule = (await _host.AsCallerAsync(manager, s => Schedules(s).CreateAsync("DMO", Schedule(playlist.Id), Ct))).Data;

        foreach (var (reason, sql, id) in new (string, string, Guid)[]
                 {
                     ("a playlist deleted", "DELETE FROM advertising_playlist WHERE id = @id", playlist.Id),
                     ("a schedule deleted", "DELETE FROM advertising_schedule WHERE id = @id", schedule.Id),
                     ("the schedules truncated", "TRUNCATE advertising_schedule", schedule.Id),
                     ("the playlists truncated", "TRUNCATE advertising_playlist CASCADE", playlist.Id)
                 })
        {
            await using var runtime = await RuntimeAsync();
#pragma warning disable CA2100 // literal statements from the array above
            await using var command = new NpgsqlCommand(sql, runtime.Connection, runtime.Transaction);
#pragma warning restore CA2100
            command.Parameters.AddWithValue("id", id);
            var writing = () => command.ExecuteNonQueryAsync(Ct);
            (await writing.Should().ThrowAsync<PostgresException>(reason)).Which.SqlState.Should().Be("42501", reason);
        }

        // An item retired after the playlist was written leaves the playlist as it was: it can still be renamed and retired.
        (await _host.AsCallerAsync(manager, s => s.GetRequiredService<ISvcAdvertisingMedia>().RetireAsync("DMO", media.Id, Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(manager, s => Playlists(s).RetireAsync("DMO", playlist.Id, Ct))).Data.Status.Should().Be("Retired");
        (await _host.AsCallerAsync(manager, s => Playlists(s).GetAsync("DMO", playlist.Id, Ct))).Data.Items.Should().ContainSingle()
            .Which.Should().Match<AdvertisingPlaylistItemViewModel>(i => i.MediaStatus == "Retired" && !i.Playable);
    }

    [Fact]
    public async Task Database_Should_RefuseASlideOfAnotherSiteOrARetiredOne_When_TheRuntimeRoleWritesAPlaylist()
    {
        var admin = await AdminAsync();
        var manager = await ManagerAsync("it.plans.rt.slides");
        var playlist = await CreatedPlaylistAsync(manager, "DMO", Playlist("Slides", Item("Media", (await MediaAsync(manager)).Id)));
        var theirs = await SlideAsync(admin, "PLN2", "Another site's slide");
        var retired = await SlideAsync(admin, title: "A retired slide");
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcInstructionSlides>().RetireAsync(retired, Ct))).HasErrors.Should().BeFalse();

        foreach (var (reason, slide) in new[] { ("another site's slide", theirs), ("a retired slide", retired) })
        {
            await using var runtime = await RuntimeAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE advertising_playlist SET items = '[{\"kind\":\"Slide\",\"id\":\"' || @slide::text || '\",\"seconds\":10,\"weight\":1}]' WHERE id = @id",
                runtime.Connection, runtime.Transaction);
            command.Parameters.AddWithValue("slide", slide);
            command.Parameters.AddWithValue("id", playlist.Id);
            var writing = () => command.ExecuteNonQueryAsync(Ct);
            (await writing.Should().ThrowAsync<PostgresException>(reason)).Which.SqlState.Should().Be("23514", reason);
        }
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

    /// <summary>A connection with an open transaction as the runtime role (rolled back when disposed).</summary>
    private async Task<Runtime> RuntimeAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
        return new Runtime(connection, transaction);
    }
}
