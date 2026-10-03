using Ariva.Core;
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
/// ARV-058 against PostgreSQL with script 0035: passenger displays round-trip through the service within the caller's
/// sites, show only queue zones of the site's published profile, keep their code unique and their site fixed, are
/// audited without the credential; a player is found only by the display's code and its current credential, and only
/// while the display is live and enabled; the runtime role cannot delete a display.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DisplayServiceTests(PostgresFixture fixture) : IAsyncDisposable
{
    private static readonly SemaphoreSlim SeedGate = new(1, 1);
    private static bool _seeded;

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Displays);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ISvcDisplays Displays(IServiceProvider s) => s.GetRequiredService<ISvcDisplays>();

    private static ISvcDisplayBoard Board(IServiceProvider s) => s.GetRequiredService<ISvcDisplayBoard>();

    private static DisplayRequest Request(string code, string site = "DMO", string zone = "A-CIT", string[] languages = null) =>
        new(site, code, "Arrivals hall", "Passport control entrance", "Landscape", languages ?? ["ar", "en"], 5, 1, 150,
            [new DisplayEntryRequest(zone, new Dictionary<string, string> { ["ar"] = "المواطنون", ["en"] = "Citizens" })],
            new Dictionary<string, string> { ["ar"] = "يرجى اتباع اللافتات", ["en"] = "Please follow the signs" });

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
    public async Task Display_Should_RoundTripAndAuditWithoutItsCredential_When_CreatedChangedAndDeleted()
    {
        var admin = await AdminAsync("it.display.admin");
        var manager = await ManagerAsync(admin, "it.display.manager");

        var created = await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("ARR-A"), Ct));
        created.HasErrors.Should().BeFalse(string.Join(", ", created.ErrorMessages ?? []));
        var credential = created.Data.Credential;
        var prefix = credential[..13];
        credential.Should().StartWith("ardp_");
        created.Data.Display.Should().Match<DisplayViewModel>(d => d.Code == "ARR-A" && d.SiteCode == "DMO" && d.CredentialPrefix == prefix &&
                                                                  d.Languages.SequenceEqual(new[] { "ar", "en" }) && d.Entries.Single().Labels["ar"] == "المواطنون");
        (await _host.ReadAsync<string>("SELECT credential_hash FROM display WHERE code = 'ARR-A' AND deleted_on IS NULL")).Should().HaveLength(64).And.NotContain(credential);
        (await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("ARR-A"), Ct))).ErrorMessages.Should().Equal(DisplayErrors.DuplicateCode);

        var id = created.Data.Display.Id;
        var changed = await _host.AsCallerAsync(manager, s => Displays(s).UpdateAsync(id, Request("ARR-A", zone: "A-VIS") with { BandMinutes = 10, HysteresisMinutes = 2 }, Ct));
        changed.Data.Should().Match<DisplayViewModel>(d => d.BandMinutes == 10 && d.Entries.Single().Zone == "A-VIS" && d.CredentialPrefix == prefix);
        (await _host.AsCallerAsync(manager, s => Displays(s).UpdateAsync(id, Request("ARR-B"), Ct))).ErrorMessages.Should().Equal(DisplayErrors.StaysInSite);
        (await _host.AsCallerAsync(manager, s => Displays(s).UpdateAsync(id, Request("ARR-A", site: "ALX"), Ct))).ErrorMessages.Should().Equal(DisplayErrors.StaysInSite);

        var audit = await _host.ReadAsync<string>("SELECT string_agg(coalesce(before_summary, '') || coalesce(after_summary, ''), ' ') FROM audit_entry WHERE target_id = @id", id);
        audit.Should().Contain("ARR-A").And.Contain(prefix).And.NotContain(credential);

        (await _host.AsCallerAsync(manager, s => Displays(s).DeleteAsync(id, Ct))).Data.Should().BeTrue();
        (await _host.AsCallerAsync(manager, s => Displays(s).GetAsync(id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("ARR-A"), Ct))).HasErrors.Should().BeFalse("a deleted display's code is free again");
    }

    [Fact]
    public async Task Display_Should_ShowOnlyQueueZonesOfItsSitesPublishedProfile_When_Created()
    {
        var admin = await AdminAsync("it.display.zones.admin");
        var manager = await ManagerAsync(admin, "it.display.zones.manager");

        (await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("ZN-1", zone: "NOT-A-ZONE"), Ct))).ErrorMessages.Should().Equal(DisplayErrors.UnknownZones);
        (await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("ZN-2", zone: "A-OV"), Ct))).ErrorMessages.Should().Equal(new[] { DisplayErrors.UnknownZones },
            "an overflow band is no queue");
        (await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("ZN-3", site: "ALX"), Ct))).ErrorMessages.Should().Equal(new[] { TopologyErrors.UnknownSite },
            "a site outside the caller's answers like an unknown one");
        (await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("zn-4"), Ct))).ErrorMessages.Should().Equal(DisplayErrors.InvalidCode);
        (await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("ZN-5", languages: ["ar", "en", "fr"]), Ct))).HasErrors.Should().BeTrue();
        (await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("ZN-6") with { Orientation = "landscape" }, Ct))).HasErrors.Should().BeTrue();

        var elsewhere = await ManagerAsync(admin, "it.display.zones.elsewhere", "ALX");
        var dmo = (await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("ZN-7"), Ct))).Data.Display;
        (await _host.AsCallerAsync(elsewhere, s => Displays(s).GetAsync(dmo.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(elsewhere, s => Displays(s).SearchAsync("DMO", Ct))).Data.Should().BeEmpty();
        (await _host.AsCallerAsync(elsewhere, s => Displays(s).NewCredentialAsync(dmo.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(elsewhere, s => Displays(s).DeleteAsync(dmo.Id, Ct))).ErrorMessages.Should().Equal(TopologyErrors.NotFound);
        (await _host.AsCallerAsync(manager, s => Displays(s).SearchAsync("DMO", Ct))).Data.Should().Contain(d => d.Code == "ZN-7");
    }

    [Fact]
    public async Task Player_Should_BeFoundByItsCodeAndCurrentCredentialOnly_When_ItAsksForItsBoard()
    {
        var admin = await AdminAsync("it.display.player.admin");
        var manager = await ManagerAsync(admin, "it.display.player.manager");
        var created = (await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("PL-1"), Ct))).Data;
        var other = (await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("PL-2", zone: "A-RES"), Ct))).Data;
        var credential = created.Credential;
        var prefix = credential[..13];

        var player = await _host.AsCallerAsync(null, s => Board(s).FindAsync("PL-1", credential, Ct));
        player.Should().Match<DisplayPlayer>(p => p.Id == created.Display.Id && p.Code == "PL-1" && p.SiteCode == "DMO" && p.CredentialPrefix == prefix);
        var board = await _host.AsCallerAsync(null, s => Board(s).GetAsync(player.Id, player.CredentialPrefix, Ct));
        board.Data.Should().Match<DisplayBoardViewModel>(b => b.Code == "PL-1" && b.BandMinutes == 5 && b.StaleSeconds == 150 && b.Languages.SequenceEqual(new[] { "ar", "en" }));
        board.Data.Entries.Should().ContainSingle().Which.Should().Match<DisplayBoardEntryViewModel>(e => e.Labels["en"] == "Citizens" && e.NowcastMinutes == null,
            "no live snapshot without Redis");

        (await _host.AsCallerAsync(null, s => Board(s).FindAsync("PL-2", credential, Ct))).Should().BeNull("another display's code with this credential");
        (await _host.AsCallerAsync(null, s => Board(s).FindAsync("PL-1", other.Credential, Ct))).Should().BeNull("another display's credential");
        (await _host.AsCallerAsync(null, s => Board(s).FindAsync("PL-1", credential[..47] + (credential[47] == 'A' ? "B" : "A"), Ct))).Should().BeNull();
        (await _host.AsCallerAsync(null, s => Board(s).FindAsync("PL-1", null, Ct))).Should().BeNull();
        (await _host.AsCallerAsync(null, s => Board(s).FindAsync("PL-1'--", credential, Ct))).Should().BeNull();

        // A new credential replaces the old one at once.
        var renewed = (await _host.AsCallerAsync(manager, s => Displays(s).NewCredentialAsync(created.Display.Id, Ct))).Data;
        renewed.Credential.Should().NotBe(credential);
        (await _host.AsCallerAsync(null, s => Board(s).GetAsync(player.Id, player.CredentialPrefix, Ct))).HasErrors.Should()
            .BeTrue("a request authenticated with the old credential gets no board once it is replaced");
        (await _host.AsCallerAsync(null, s => Board(s).FindAsync("PL-1", credential, Ct))).Should().BeNull("the old credential is gone");
        (await _host.AsCallerAsync(null, s => Board(s).FindAsync("PL-1", renewed.Credential, Ct))).Should().NotBeNull();

        // A disabled or deleted display has no player.
        (await _host.AsCallerAsync(manager, s => Displays(s).UpdateAsync(created.Display.Id, Request("PL-1") with { Enabled = false }, Ct))).HasErrors.Should().BeFalse();
        (await _host.AsCallerAsync(null, s => Board(s).FindAsync("PL-1", renewed.Credential, Ct))).Should().BeNull("disabled");
        (await _host.AsCallerAsync(null, s => Board(s).GetAsync(created.Display.Id, renewed.Display.CredentialPrefix, Ct))).HasErrors.Should().BeTrue();
        (await _host.AsCallerAsync(manager, s => Displays(s).DeleteAsync(other.Display.Id, Ct))).Data.Should().BeTrue();
        (await _host.AsCallerAsync(null, s => Board(s).FindAsync("PL-2", other.Credential, Ct))).Should().BeNull("deleted");
    }

    [Fact]
    public async Task RuntimeRole_Should_NotDeleteADisplay_When_ItTries()
    {
        var admin = await AdminAsync("it.display.runtime.admin");
        var manager = await ManagerAsync(admin, "it.display.runtime.manager");
        await _host.AsCallerAsync(manager, s => Displays(s).CreateAsync(Request("RT-1"), Ct));
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await _host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
        await using var command = new NpgsqlCommand("DELETE FROM display WHERE code = 'RT-1'", connection, transaction);
        var delete = () => command.ExecuteNonQueryAsync(Ct);
        (await delete.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501", "displays are soft deleted only");
        await transaction.RollbackAsync(Ct);
    }
}
