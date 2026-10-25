using System.Diagnostics;
using System.Globalization;
using System.Text;
using Ariva.Core;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Imports;
using Ariva.Core.Services.Topology;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.IntegrationTests.CsvImports;

/// <summary>
/// ARV-123a against PostgreSQL: a desk code mapping CSV previewed (a dry run of the mapping service) and committed with
/// its token creates every mapping; the service's rules answer per row (a code taken, a code for a desk of the wrong kind,
/// a desk that does not exist); a row refused at commit time keeps nothing of the rows before it; a device token never
/// opens a mapping commit; the commit at the row bound is measured in one transaction.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DeskCodeMappingImportTests(PostgresFixture fixture) : IAsyncDisposable
{
    private const string Header = "externalSystem,externalCode,checkpoint,desk";

    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Administration);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ISvcDeskCodeMappingImports Imports(IServiceProvider s) => s.GetRequiredService<ISvcDeskCodeMappingImports>();

    private static T Ok<T>(Fluentx.Result<T> result)
    {
        result.HasErrors.Should().BeFalse(string.Join(", ", result.ErrorMessages ?? []));
        return result.Data;
    }

    private static Func<Stream> File(string text) => () => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static string Csv(params string[] rows) => Header + "\n" + string.Join("\n", rows) + "\n";

    private async Task<Guid> AdministratorAsync(string name)
    {
        var admin = await _host.CreateUserAsync($"it.mimp.{name}", roles: [RoleCodes.SystemAdministrator]);
        await _host.ReadAsync<int>("UPDATE \"user\" SET all_sites = true WHERE id = @id RETURNING 1", admin);
        return admin;
    }

    /// <summary>A site with an immigration checkpoint (desks D01, D02 and e-gate EG01) and a check-in counter C01; answers the immigration checkpoint.</summary>
    private async Task<Guid> SiteAsync(Guid admin, string site)
    {
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcSites>().CreateAsync(new CreateSiteRequest(site, site), Ct)));
        var topology = (IServiceProvider s) => s.GetRequiredService<ISvcTopology>();
        var airport = Ok(await _host.AsCallerAsync(admin, s => topology(s).CreateAirportAsync(new CreateAirportRequest(site[1..], null, $"Airport {site}", "Asia/Dubai"), Ct))).Id;
        var terminal = Ok(await _host.AsCallerAsync(admin, s => topology(s).CreateTerminalAsync(new CreateTerminalRequest(airport, "T1", "T1", site), Ct))).Id;
        var level = Ok(await _host.AsCallerAsync(admin, s => topology(s).CreateLevelAsync(new CreateLevelRequest(terminal, "L0", "Arrivals", 0, 100, 50), Ct))).Id;
        var immigration = Ok(await _host.AsCallerAsync(admin, s => topology(s).CreateCheckpointAsync(new CreateCheckpointRequest(level, "IMM", "Immigration", "Immigration"), Ct))).Id;
        var checkIn = Ok(await _host.AsCallerAsync(admin, s => topology(s).CreateCheckpointAsync(new CreateCheckpointRequest(level, "CHK", "Check-in", "CheckIn"), Ct))).Id;
        foreach (var (code, kind, lane) in new[] { ("D01", "Desk", "CIT"), ("D02", "Desk", "CIT"), ("EG01", "EGate", "EG") })
            Ok(await _host.AsCallerAsync(admin, s => topology(s).CreateDeskAsync(new CreateDeskRequest(immigration, code, null, kind, [lane]), Ct)));
        Ok(await _host.AsCallerAsync(admin, s => topology(s).CreateDeskAsync(new CreateDeskRequest(checkIn, "C01", null, "Counter"), Ct)));
        return immigration;
    }

    private Task<long> MappingsAsync(string site) => _host.ReadAsync<long>("SELECT count(*) FROM desk_code_mapping WHERE site_code = @secret", secret: site);

    [Fact]
    public async Task PreviewThenCommit_Should_CreateEveryMapping_When_TheTokenIsThePreviews()
    {
        var admin = await AdministratorAsync("ok");
        await SiteAsync(admin, "XCMA");
        var csv = Csv("Aman,imm-01,IMM,D01", "Aman,EG-01,IMM,EG01", "Aodb,C/01,CHK,C01");

        var preview = Ok(await _host.AsCallerAsync(admin, s => Imports(s).PreviewAsync("XCMA", File(csv), Ct)));

        preview.Errors.Should().BeEmpty();
        preview.Mappings.Select(m => (m.Row, m.System, m.ExternalCode)).Should().Equal((2, "Aman", "IMM-01"), (3, "Aman", "EG-01"), (4, "Aodb", "C/01"));
        (await MappingsAsync("XCMA")).Should().Be(0, "a preview is a dry run");

        var committed = Ok(await _host.AsCallerAsync(admin, s => Imports(s).CommitAsync("XCMA", File(csv), preview.PreviewToken, Ct)));

        committed.Mappings.Should().Be(3);
        (await _host.ReadAsync<string>("SELECT d.code FROM desk_code_mapping m JOIN desk d ON d.id = m.desk_id WHERE m.site_code = 'XCMA' AND m.external_code = 'IMM-01'"))
            .Should().Be("D01");
        var audit = await _host.ReadAsync<string>("SELECT after_summary FROM audit_entry WHERE action = 'DeskCodeMappingImport.Committed' AND target_name = 'XCMA'");
        new[] { preview.FileSha256, "\"committed\"", "\"created\":3" }.Where(w => !audit.Contains(w, StringComparison.Ordinal)).Should().BeEmpty();
        new[] { "IMM-01", "C/01", "D01" }.Where(w => audit.Contains(w, StringComparison.Ordinal)).Should().BeEmpty("no cell");
    }

    [Fact]
    public async Task Preview_Should_AnswerTheMappingServicesRulesPerRow_When_RowsBreakThem()
    {
        var admin = await AdministratorAsync("rf");
        await SiteAsync(admin, "XCMB");
        var desk = await _host.ReadAsync<Guid>("SELECT id FROM desk WHERE site_code = 'XCMB' AND code = 'D02'");
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDeskCodeMappings>().CreateAsync(new CreateDeskCodeMappingRequest("Aman", "TAKEN", desk), Ct)));
        var csv = Csv("Aman,IMM-01,IMM,D01", "Aman,TAKEN,IMM,EG01", "Aman,C-01,CHK,C01", "Aodb,X-01,IMM,D99", "Aman,\"@SUM(A1)\",IMM,D01");

        var preview = Ok(await _host.AsCallerAsync(admin, s => Imports(s).PreviewAsync("XCMB", File(csv), Ct)));

        (preview.PreviewToken is null).Should().BeTrue("a file with a refused row gets no token (asserted as a boolean, so a failure never prints one)");
        preview.Mappings.Select(m => m.Row).Should().Equal(2);
        preview.Errors.Select(e => (e.Row, e.Column, e.Reason)).Should().BeEquivalentTo(new[]
        {
            (3, (int?)2, CsvImportErrors.MappingTaken),
            (4, 4, CsvImportErrors.MappingDoesNotFitDesk),
            (5, 4, CsvImportErrors.DeskNotFound),
            (6, 2, CsvImportErrors.FormulaPrefix)
        });
        (await MappingsAsync("XCMB")).Should().Be(1, "only the mapping that was there");
    }

    [Fact]
    public async Task Commit_Should_KeepNothing_When_ALaterRowIsRefusedAndRefuseADeviceToken()
    {
        var admin = await AdministratorAsync("rb");
        await SiteAsync(admin, "XCMC");
        var csv = Csv("Aman,IMM-01,IMM,D01", "Aman,IMM-02,IMM,D02");
        var token = Ok(await _host.AsCallerAsync(admin, s => Imports(s).PreviewAsync("XCMC", File(csv), Ct))).PreviewToken;
        var desk = await _host.ReadAsync<Guid>("SELECT id FROM desk WHERE site_code = 'XCMC' AND code = 'EG01'");
        Ok(await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDeskCodeMappings>().CreateAsync(new CreateDeskCodeMappingRequest("Aman", "IMM-02", desk), Ct)));

        (await _host.AsCallerAsync(admin, s => Imports(s).CommitAsync("XCMC", File(csv), token, Ct))).ErrorMessages
            .Should().Equal(CsvImportErrors.AtRow(3, CsvImportErrors.MappingTaken));
        (await _host.ReadAsync<string>("SELECT string_agg(external_code, ',') FROM desk_code_mapping WHERE site_code = 'XCMC'")).Should().Be("IMM-02", "row 2 was rolled back");

        var deviceCsv = "code,family,model,transport,dialect,clockSource,terminal,level,x,y,mountingHeightMetres,orientationDegrees,queueZone\nS-01,StereoVision,PC2SE,HttpsPush,Xovis,Ntp,T1,L0,20,16,5,0,Snake A\n";
        var deviceToken = (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDeviceImports>().PreviewAsync("XCMC", File(deviceCsv), Ct))).Data?.PreviewToken;
        (deviceToken is null).Should().BeTrue("the site has no queue zone, so the device row is refused");
        var other = Csv("Aman,IMM-05,IMM,D01");
        var otherToken = Ok(await _host.AsCallerAsync(admin, s => Imports(s).PreviewAsync("XCMC", File(other), Ct))).PreviewToken;
        (await _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcDeviceImports>().CommitAsync("XCMC", File(other), otherToken, Ct))).ErrorMessages
            .Should().Equal([CsvImportErrors.NotThePreview], "a mapping token never opens a device commit");
        var someoneElse = await AdministratorAsync("rb2");
        (await _host.AsCallerAsync(someoneElse, s => Imports(s).CommitAsync("XCMC", File(other), otherToken, Ct))).ErrorMessages
            .Should().Equal([CsvImportErrors.NotThePreview], "another user");
        (await MappingsAsync("XCMC")).Should().Be(1);
    }

    [Fact]
    public async Task PreviewAndCommit_Should_AnswerSiteBusyAfterTheWait_When_AnotherTransactionHoldsTheSiteLonger()
    {
        // ARV-155: the mapping import waits at most CsvImportLimits.SiteLockWait for the site's lock, preview and commit alike.
        var admin = await AdministratorAsync("lk");
        await SiteAsync(admin, "XCML");
        var csv = Csv("Aman,IMM-01,IMM,D01");
        var token = Ok(await _host.AsCallerAsync(admin, s => Imports(s).PreviewAsync("XCML", File(csv), Ct))).PreviewToken;
        (token is null).Should().BeFalse("the file passes");

        TimeSpan waited;
        Fluentx.Result<DeskCodeMappingImportPreviewViewModel> preview;
        Fluentx.Result<DeskCodeMappingImportCommitViewModel> commit;
        await using (await SiteHolder.HoldAsync(fixture.ConnectionString(await _host.DatabaseAsync()), "XCML"))
        {
            var clock = Stopwatch.StartNew();
            preview = await _host.AsCallerAsync(admin, s => Imports(s).PreviewAsync("XCML", File(csv), Ct));
            waited = clock.Elapsed;
            commit = await _host.AsCallerAsync(admin, s => Imports(s).CommitAsync("XCML", File(csv), token, Ct));
        }

        preview.ErrorMessages.Should().Equal(CsvImportErrors.SiteBusy);
        commit.ErrorMessages.Should().Equal(CsvImportErrors.SiteBusy);
        waited.Should().BeGreaterThanOrEqualTo(CsvImportLimits.SiteLockWait - TimeSpan.FromMilliseconds(100), "it waits for the lock first")
            .And.BeLessThan(CsvImportLimits.SiteLockWait + TimeSpan.FromSeconds(10), "and no longer than its wait and the dry run");
        (await MappingsAsync("XCML")).Should().Be(0);
        (await _host.ReadAsync<long>("SELECT count(*) FROM audit_entry WHERE action = 'DeskCodeMappingImport.Refused' AND target_name = 'XCML'"))
            .Should().Be(1, "the busy commit is audited as refused");
        Ok(await _host.AsCallerAsync(admin, s => Imports(s).CommitAsync("XCML", File(csv), token, Ct))).Mappings.Should().Be(1, "once the lock is free the same token commits");
    }

    [Fact]
    public async Task Commit_Should_CreateTheRowBoundInOneTransaction_When_Measured()
    {
        var admin = await AdministratorAsync("bd");
        var checkpoint = await SiteAsync(admin, "XCMD");
        // The desks of the bound, written directly (the topology endpoints would take minutes to create them one by one).
        (await _host.ReadAsync<long>("""
            WITH created AS (
                INSERT INTO desk (id, checkpoint_id, site_code, code, name, kind, in_service)
                SELECT gen_random_uuid(), @id, 'XCMD', 'B' || g, 'B' || g, 'Desk', true FROM generate_series(1, 10000) g RETURNING 1)
            SELECT count(*) FROM created
            """, checkpoint)).Should().Be(10_000);
        var csv = Csv([.. Enumerable.Range(1, CsvImportLimits.MaxMappingRows).Select(n => string.Create(CultureInfo.InvariantCulture, $"Aman,A-{n},IMM,B{n}"))]);

        var clock = Stopwatch.StartNew();
        var token = Ok(await _host.AsCallerAsync(admin, s => Imports(s).PreviewAsync("XCMD", File(csv), Ct))).PreviewToken;
        var previewTime = clock.Elapsed;
        clock.Restart();
        var committed = Ok(await _host.AsCallerAsync(admin, s => Imports(s).CommitAsync("XCMD", File(csv), token, Ct)));
        var commitTime = clock.Elapsed;

        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{CsvImportLimits.MaxMappingRows} mappings: preview {previewTime.TotalSeconds:0.0} s, commit {commitTime.TotalSeconds:0.0} s"));
        committed.Mappings.Should().Be(CsvImportLimits.MaxMappingRows);
        commitTime.Should().BeLessThan(TimeSpan.FromSeconds(60), "well within a request's time");
    }
}
