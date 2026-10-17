using Ariva.Core;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Services;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Displays;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Displays;

/// <summary>
/// ARV-141 against PostgreSQL with script 0056: instruction slides round-trip through the service within the caller's
/// sites, in their site's order, audited; their zones are published queue zones and their displays live displays of the
/// site; another site's manager reaches none of them; a display's board carries the slides that apply to it in its own
/// languages, the first pinned one apart; the demo seed adds the starter arrivals slides once; the runtime role can neither
/// store what the entity refuses nor delete a slide.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class InstructionSlideServiceTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim SeedGate = new(1, 1);
    private static bool _seeded;

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Displays);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ISvcInstructionSlides Slides(IServiceProvider s) => s.GetRequiredService<ISvcInstructionSlides>();

    private static ISvcDisplays Displays(IServiceProvider s) => s.GetRequiredService<ISvcDisplays>();

    private static ISvcDisplayBoard Board(IServiceProvider s) => s.GetRequiredService<ISvcDisplayBoard>();

    private static InstructionSlideRequest Slide(string scope = "Site", string[] targets = null, string icon = "Information", bool pinned = false,
        string site = "DMO", string title = "Follow the signs", Dictionary<string, InstructionTextRequest> texts = null) =>
        new(site, scope, targets ?? [], icon, 10, pinned,
            texts ?? new Dictionary<string, InstructionTextRequest> { ["en"] = new(title, null), ["ar"] = new("اتبع اللافتات", "إلى الكاونتر") });

    private static DisplayRequest DisplayRequest(string code, string zone = "A-CIT", string[] languages = null) =>
        new("DMO", code, "Arrivals hall", null, "Landscape", languages ?? ["ar", "en"], 5, 1, 150,
            [new DisplayEntryRequest(zone, (languages ?? ["ar", "en"]).ToDictionary(l => l, _ => "Citizens"))],
            (languages ?? ["ar", "en"]).ToDictionary(l => l, _ => "Please follow the signs"));

    private async Task<Guid> AdminAsync(string userName)
    {
        var admin = await _host.CreateUserAsync(userName, roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
        await SeedGate.WaitAsync(Ct);
        try
        {
            if (!_seeded)
            {
                await _host.AsCallerAsync(null, s =>
                    new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
                // ALX may exist already (the displays' tests share this database): a second creation is refused, which is fine.
                await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest("ALX", "Other airport"), Ct));
                _seeded = true;
            }
        }
        finally
        {
            SeedGate.Release();
        }

        return admin;
    }

    private async Task<Guid> ManagerAsync(Guid admin, string userName, string site = "DMO")
    {
        var user = await _host.CreateUserAsync(userName, roles: [RoleCodes.TerminalDutyManager]);
        await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcUsers>().SetSitesAsync(user, new SiteAccessRequest(false, [site]), Ct));
        return user;
    }

    [Fact]
    public async Task Seed_Should_AddTheStarterArrivalsSlidesOnce_When_TheArrivalZonesArePublished()
    {
        var admin = await AdminAsync("it.slide.seed.admin");
        var seeded = (await _host.AsCallerAsync(admin, s => Slides(s).SearchAsync("DMO", Ct))).Data.Where(s => s.Position <= 5).ToList();
        seeded.Select(s => s.Icon).Should().Equal("Passport", "EGate", "HatAndGlasses", "Family", "NoPhone");
        seeded.Should().OnlyContain(s => s.Scope == "Zone" && s.Status == "Active" && s.Texts.Keys.Order().SequenceEqual(new[] { "ar", "en", "pt", "sw" }));
        seeded[1].Targets.Should().Equal("A-CIT", "A-RES", "A-EG");
        (await _host.ReadAsync<int>("SELECT CAST(count(*) AS integer) FROM audit_entry WHERE action = 'InstructionSlide.Created' AND target_name LIKE 'DMO:%'"))
            .Should().BeGreaterThanOrEqualTo(5, "each seeded slide is audited like one the API creates");

        var before = await _host.ReadAsync<int>("SELECT CAST(count(*) AS integer) FROM instruction_slide WHERE site_code = 'DMO'");
        await _host.AsCallerAsync(null, s =>
            new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
        (await _host.ReadAsync<int>("SELECT CAST(count(*) AS integer) FROM instruction_slide WHERE site_code = 'DMO'")).Should().Be(before, "the seed adds them once");
    }

    [Fact]
    public async Task Slide_Should_RoundTripInOrderAndBeAudited_When_CreatedChangedRetiredAndRestored()
    {
        var admin = await AdminAsync("it.slide.admin");
        var manager = await ManagerAsync(admin, "it.slide.manager");

        var first = await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide(title: "First"), Ct));
        first.HasErrors.Should().BeFalse(string.Join(", ", first.ErrorMessages ?? []));
        var second = (await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide("Zone", ["A-VIS", "A-RES"], "Baggage", title: "Second"), Ct))).Data;
        second.Position.Should().Be(first.Data.Position + 1, "a new slide goes to the end of its site's order");
        second.Should().Match<InstructionSlideViewModel>(s => s.SiteCode == "DMO" && s.Scope == "Zone" && s.Targets.SequenceEqual(new[] { "A-VIS", "A-RES" }) &&
                                                              s.Icon == "Baggage" && s.Texts["ar"].Body == "إلى الكاونتر" && s.Status == "Active");

        var markup = "<script>alert(1)</script>";
        var changed = await _host.AsCallerAsync(manager, s => Slides(s).UpdateAsync(second.Id, Slide("Zone", ["A-VIS"], "Family", pinned: true, title: markup), Ct));
        changed.Data.Should().Match<InstructionSlideViewModel>(s => s.Pinned && s.Icon == "Family" && s.Texts["en"].Title == markup && s.Position == second.Position);
        (await _host.ReadAsync<string>("SELECT texts FROM instruction_slide WHERE id = @id", second.Id)).Should().Contain(markup, "kept as text");

        (await _host.AsCallerAsync(manager, s => Slides(s).RetireAsync(second.Id, Ct))).Data.Status.Should().Be("Retired");
        (await _host.AsCallerAsync(manager, s => Slides(s).RestoreAsync(second.Id, Ct))).Data.Status.Should().Be("Active");
        (await _host.AsCallerAsync(manager, s => Slides(s).UpdateAsync(second.Id, Slide(site: "ALX"), Ct))).ErrorMessages.Should().Equal(InstructionSlideErrors.StaysInSite);

        var actions = await _host.ReadAsync<string>("SELECT string_agg(action, ',' ORDER BY action) FROM audit_entry WHERE target_id = @id", second.Id);
        actions.Should().Be("InstructionSlide.Created,InstructionSlide.Restored,InstructionSlide.Retired,InstructionSlide.Updated", "every change is audited once");
        (await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE target_id = @id AND action = 'InstructionSlide.Updated'", second.Id))
            .Should().Contain("\"pinned\":true").And.Contain("Family");
    }

    [Fact]
    public async Task Slide_Should_NameOnlyPublishedQueueZonesAndLiveDisplaysOfItsSite_When_Created()
    {
        var admin = await AdminAsync("it.slide.targets.admin");
        var manager = await ManagerAsync(admin, "it.slide.targets.manager");
        await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(DisplayRequest("SLD-T"), Ct));

        (await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide("Zone", ["NOT-A-ZONE"]), Ct))).ErrorMessages.Should().Equal(InstructionSlideErrors.UnknownZones);
        (await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide("Zone", ["A-OV"]), Ct))).ErrorMessages.Should().Equal(new[] { InstructionSlideErrors.UnknownZones },
            "an overflow band is no queue");
        (await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide("Display", ["NOPE"]), Ct))).ErrorMessages.Should().Equal(InstructionSlideErrors.UnknownDisplays);
        (await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide("Display", ["SLD-T"]), Ct))).HasErrors.Should().BeFalse();
        // Another site's queue zone or display, as names a slide of ALX might carry: refused like names that exist nowhere.
        var elsewhere = await ManagerAsync(admin, "it.slide.targets.elsewhere", "ALX");
        (await _host.AsCallerAsync(elsewhere, s => Slides(s).CreateAsync(Slide("Zone", ["A-CIT"], site: "ALX"), Ct))).ErrorMessages
            .Should().Equal(new[] { InstructionSlideErrors.UnknownZones }, "A-CIT is a queue zone of DMO, not of ALX");
        (await _host.AsCallerAsync(elsewhere, s => Slides(s).CreateAsync(Slide("Display", ["SLD-T"], site: "ALX"), Ct))).ErrorMessages
            .Should().Equal(new[] { InstructionSlideErrors.UnknownDisplays }, "SLD-T is a display of DMO, not of ALX");
        (await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide(site: "ALX"), Ct))).ErrorMessages.Should().Equal(new[] { TopologyErrors.UnknownSite },
            "a site outside the caller's answers like an unknown one");
        (await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide("site"), Ct))).HasErrors.Should().BeTrue("the scope's name is exact");
        (await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide(icon: "1"), Ct))).HasErrors.Should().BeTrue("never a number for an icon");
        (await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide(title: "Bell\u0007"), Ct))).HasErrors.Should().BeTrue("no control characters");
        (await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide(texts: new Dictionary<string, InstructionTextRequest> { ["fr"] = new("Passeport", null) }), Ct)))
            .HasErrors.Should().BeTrue("only the board's languages");
    }

    [Fact]
    public async Task Slide_Should_AnswerNotFound_When_AnotherSitesManagerReachesIt()
    {
        var admin = await AdminAsync("it.slide.scope.admin");
        var manager = await ManagerAsync(admin, "it.slide.scope.manager");
        var elsewhere = await ManagerAsync(admin, "it.slide.scope.elsewhere", "ALX");
        var dmo = (await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide(title: "Scoped"), Ct))).Data;

        (await _host.AsCallerAsync(elsewhere, s => Slides(s).GetAsync(dmo.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(elsewhere, s => Slides(s).SearchAsync("DMO", Ct))).Data.Should().BeEmpty();
        (await _host.AsCallerAsync(elsewhere, s => Slides(s).UpdateAsync(dmo.Id, Slide(pinned: true, title: "Taken over"), Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(elsewhere, s => Slides(s).RetireAsync(dmo.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(elsewhere, s => Slides(s).RestoreAsync(dmo.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(elsewhere, s => Slides(s).ReorderAsync("DMO", new InstructionSlideOrderRequest([dmo.Id]), Ct))).ErrorMessages
            .Should().Equal(TopologyErrors.UnknownSite);
        (await _host.ReadAsync<string>("SELECT status || ' ' || pinned || ' ' || texts FROM instruction_slide WHERE id = @id", dmo.Id))
            .Should().StartWith("Active false").And.Contain("Scoped").And.NotContain("Taken over", "nothing of the refused changes is kept");
    }

    [Fact]
    public async Task Reorder_Should_ListEverySlideOfTheSiteOnce_When_ItMovesThem()
    {
        var admin = await AdminAsync("it.slide.order.admin");
        var manager = await ManagerAsync(admin, "it.slide.order.manager");
        await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide(title: "Order A"), Ct));
        var all = (await _host.AsCallerAsync(manager, s => Slides(s).SearchAsync("DMO", Ct))).Data.Select(s => s.Id).ToList();
        all.Count.Should().BeGreaterThan(1);

        var reversed = Enumerable.Reverse(all).ToList();
        var ordered = await _host.AsCallerAsync(manager, s => Slides(s).ReorderAsync("DMO", new InstructionSlideOrderRequest(reversed), Ct));
        ordered.Data.Select(s => s.Id).Should().Equal(reversed);
        ordered.Data.Select(s => s.Position).Should().Equal(Enumerable.Range(1, reversed.Count));
        (await _host.AsCallerAsync(manager, s => Slides(s).SearchAsync("DMO", Ct))).Data.Select(s => s.Id).Should().Equal(reversed, "the new order is kept");

        (await _host.AsCallerAsync(manager, s => Slides(s).ReorderAsync("DMO", new InstructionSlideOrderRequest(reversed.Skip(1).ToList()), Ct))).ErrorMessages
            .Should().Equal(InstructionSlideErrors.OrderMismatch);
        (await _host.AsCallerAsync(manager, s => Slides(s).ReorderAsync("DMO", new InstructionSlideOrderRequest([.. reversed.Skip(1), reversed[1]]), Ct))).ErrorMessages
            .Should().Equal(InstructionSlideErrors.OrderMismatch);
        (await _host.AsCallerAsync(manager, s => Slides(s).ReorderAsync("DMO", new InstructionSlideOrderRequest([.. reversed.Skip(1), Guid.NewGuid()]), Ct))).ErrorMessages
            .Should().Equal(InstructionSlideErrors.OrderMismatch);
        // Each slide that moved is audited on its own, with its place before and after.
        var moved = reversed[0];
        (await _host.ReadAsync<string>(
                "SELECT before_summary || ' > ' || after_summary FROM audit_entry WHERE action = 'InstructionSlide.Moved' AND target_id = @id ORDER BY occurred_on DESC LIMIT 1",
                moved))
            .Should().Be($"position={all.Count} > position=1");

        // Back to the order the other tests expect (the seeded starter set first).
        await _host.AsCallerAsync(manager, s => Slides(s).ReorderAsync("DMO", new InstructionSlideOrderRequest(all), Ct));
    }

    [Fact]
    public async Task Board_Should_CarryTheSlidesThatApplyInTheDisplaysLanguages_When_ThePlayerAsks()
    {
        var admin = await AdminAsync("it.slide.board.admin");
        var manager = await ManagerAsync(admin, "it.slide.board.manager");
        var display = (await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(DisplayRequest("SLD-B", "A-CIT", ["en", "pt"]), Ct))).Data;
        await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(DisplayRequest("SLD-C"), Ct));
        var pinned = (await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide("Display", ["SLD-B"], "Accessibility", pinned: true, title: "Pinned here",
            texts: new Dictionary<string, InstructionTextRequest> { ["en"] = new("Pinned here", "Under the waits"), ["pt"] = new("Fixado aqui", null), ["ar"] = new("مثبت", null) }), Ct))).Data;
        var retired = (await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide(title: "Retired"), Ct))).Data;
        await _host.AsCallerAsync(manager, s => Slides(s).RetireAsync(retired.Id, Ct));
        await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide("Display", ["SLD-C"], title: "Another display"), Ct));
        var arabicOnly = (await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide(texts: new Dictionary<string, InstructionTextRequest> { ["ar"] = new("بالعربية فقط", null) }), Ct))).Data;

        // A slide of another site naming a zone of the same name, pinned: written past the service, as no API can.
        await _host.ReadAsync<int>("""
            INSERT INTO instruction_slide (id, site_code, position, scope, targets, icon, duration_seconds, pinned, status, texts)
            VALUES (gen_random_uuid(), 'ALX', 1, 'Zone', '["A-CIT"]', 'Baggage', 10, true, 'Active', '{"en":{"title":"From ALX","body":null}}')
            RETURNING 1
            """);

        var player = await _host.AsCallerAsync(null, s => Board(s).FindAsync("SLD-B", display.Credential, Ct));
        var board = (await _host.AsCallerAsync(null, s => Board(s).GetAsync(player.Id, player.CredentialPrefix, Ct))).Data;

        board.PinnedSlide.Should().Match<DisplayBoardSlideViewModel>(s => s.Position == pinned.Position && s.Icon == "Accessibility");
        board.PinnedSlide.Texts.Select(t => t.Language).Should().Equal(new[] { "en", "pt" }, "only the display's languages, in its order");
        board.PinnedSlide.Texts[0].Body.Should().Be("Under the waits");
        board.Slides.Select(s => s.Icon).Should().StartWith(["Passport", "EGate", "HatAndGlasses", "Family", "NoPhone"], "the starter set applies to A-CIT");
        board.Slides.Select(s => s.Position).Should().BeInAscendingOrder();
        board.Slides.Should().NotContain(s => s.Position == retired.Position || s.Position == arabicOnly.Position || s.Position == pinned.Position,
            "retired, without the display's languages, or pinned apart");
        board.Slides.SelectMany(s => s.Texts).Should().OnlyContain(t => t.Language == "en" || t.Language == "pt");
        board.Slides.SelectMany(s => s.Texts).Should().NotContain(t => t.Title == "Another display");
        board.Slides.Append(board.PinnedSlide).SelectMany(s => s.Texts).Should().NotContain(t => t.Title == "From ALX", "never another site's slide");
    }

    [Theory]
    [InlineData("an icon outside the built-in set", "RS-1")]
    [InlineData("a time on screen over a minute", "RS-2")]
    [InlineData("a site slide naming a zone", "RS-3")]
    [InlineData("an unknown status", "RS-4")]
    [InlineData("words that are not a JSON object", "RS-5")]
    public async Task RuntimeRole_Should_NotStoreASlideTheEntityRefuses_When_ItWritesOneDirectly(string change, string title)
    {
        var admin = await AdminAsync($"it.slide.sql.admin.{title}");
        var manager = await ManagerAsync(admin, $"it.slide.sql.manager.{title}");
        var slide = (await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide(title: title), Ct))).Data;
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
        await using var command = change switch
        {
            "an icon outside the built-in set" => new NpgsqlCommand("UPDATE instruction_slide SET icon = 'Script' WHERE id = @id", connection, transaction),
            "a time on screen over a minute" => new NpgsqlCommand("UPDATE instruction_slide SET duration_seconds = 61 WHERE id = @id", connection, transaction),
            "a site slide naming a zone" => new NpgsqlCommand("UPDATE instruction_slide SET targets = '[\"A-CIT\"]' WHERE id = @id", connection, transaction),
            "an unknown status" => new NpgsqlCommand("UPDATE instruction_slide SET status = 'Deleted' WHERE id = @id", connection, transaction),
            _ => new NpgsqlCommand("UPDATE instruction_slide SET texts = '<b>x</b>' WHERE id = @id", connection, transaction)
        };
        command.Parameters.AddWithValue("id", slide.Id);
        var update = () => command.ExecuteNonQueryAsync(Ct);
        (await update.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514", $"script 0056 checks {change}");
        await transaction.RollbackAsync(Ct);
    }

    [Fact]
    public async Task RuntimeRole_Should_NotDeleteASlide_When_ItTries()
    {
        var admin = await AdminAsync("it.slide.runtime.admin");
        var manager = await ManagerAsync(admin, "it.slide.runtime.manager");
        var slide = (await _host.AsCallerAsync(manager, s => Slides(s).CreateAsync(Slide(title: "Kept"), Ct))).Data;
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
        await using var command = new NpgsqlCommand("DELETE FROM instruction_slide WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", slide.Id);
        var delete = () => command.ExecuteNonQueryAsync(Ct);
        (await delete.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501", "slides are retired, never deleted");
        await transaction.RollbackAsync(Ct);
    }
}
