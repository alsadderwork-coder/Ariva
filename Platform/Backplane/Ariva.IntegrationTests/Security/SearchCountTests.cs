using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Ariva.Core;
using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Alerting;
using Ariva.Core.Services.Topology;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.IntegrationTests.Security;

/// <summary>
/// ARV-096d (CWE-400, ASVS 5.0 V15.2.2) against PostgreSQL 17: the audit and alert searches, whose filters need no date range,
/// count at most <see cref="SearchCounts.Cap"/> matches and say so instead of counting a table that grows with time, page through
/// those first matches only (a page that starts past them is refused with a fixed text), and count exactly once a filter narrows
/// them below the cap. 10,005 rows each, planted in a database of this class's own (an open alert a zone each, as one rule keeps
/// one open alert per zone). The statements the searches send are read back from Npgsql's activities: past the cap none of them
/// counts the table, so the database reads at most the cap and one more row (a Take before Count would not do: NHibernate puts it
/// on the count's one row, which counts every match).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SearchCountTests(PostgresFixture fixture) : IAsyncDisposable
{
    private readonly AccountsHost _host = new(fixture, database: TestDatabase.SearchCounts);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Runs <paramref name="work"/> and returns what it gave and the statements on <paramref name="table"/> Npgsql sent meanwhile.</summary>
    private static async Task<(T Result, List<string> Statements)> CapturingAsync<T>(string table, Func<Task<T>> work)
    {
        var statements = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if ((activity.GetTagItem("db.query.text") ?? activity.GetTagItem("db.statement")) is string sql)
                    statements.Enqueue(sql);
            }
        };
        ActivitySource.AddActivityListener(listener);
        var result = await work();
        var mine = statements.Where(sql => Regex.IsMatch(sql, $@"\b{table}\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))).ToList();
        mine.Should().NotBeEmpty($"the search's statements on {table} are captured (Npgsql's activities)");
        return (result, mine);
    }

    /// <summary>True when a statement counts rows (count of a whole filtered set, bounded or not).</summary>
    private static bool Counts(string sql) => Regex.IsMatch(sql, @"\bcount\s*\(", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    [Fact]
    public async Task AuditSearch_Should_CountUpToTheCapAndSaySo_When_MoreEntriesMatch()
    {
        var admin = await _host.CreateUserAsync("it.count.audit", roles: [RoleCodes.SystemAdministrator], allSites: true);
        (await _host.ReadAsync<long>("""
            WITH planted AS (
                INSERT INTO audit_entry (id, occurred_on, action, target_type, target_name)
                SELECT gen_random_uuid(), timestamptz '2026-01-01T00:00:00Z' + g * interval '1 minute', 'IT.Counted', 'Site', 'DMO'
                FROM generate_series(1, 10005) g
                RETURNING 1)
            SELECT count(*) FROM planted
            """)).Should().Be(10_005);
        Task<Fluentx.Result<Ariva.Core.Domain.ViewModels.PageViewModel<Ariva.Core.Domain.ViewModels.AuditEntryViewModel>>> Search(AuditEntryCriteria criteria) =>
            _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAuditEntries>().SearchAsync(criteria, Ct));

        var (first, sent) = await CapturingAsync("audit_entry", () => Search(new AuditEntryCriteria { Action = "IT.Counted", PageSize = 50 }));
        var last = await Search(new AuditEntryCriteria { Action = "IT.Counted", PageSize = 50, PageIndex = 200 });
        var beyond = await Search(new AuditEntryCriteria { Action = "IT.Counted", PageSize = 50, PageIndex = 201 });
        var narrowed = await Search(new AuditEntryCriteria
        {
            Action = "IT.Counted",
            FromDate = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc),
            ToDate = new DateTime(2026, 1, 1, 1, 40, 0, DateTimeKind.Utc)
        });

        first.HasErrors.Should().BeFalse(string.Join(" ", first.ErrorMessages ?? []));
        first.Data.TotalCount.Should().Be(SearchCounts.Cap);
        first.Data.TotalCountCapped.Should().BeTrue("more entries match than a search counts");
        first.Data.Data.Should().HaveCount(50);
        sent.Should().NotContain(sql => Counts(sql), "past the cap the matches are not counted: one row past it is read instead");
        sent.Should().Contain(sql => Regex.IsMatch(sql, @"\boffset\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), "the probe reads the row past the cap");
        last.Data.Data.Should().HaveCount(50, "the last page within the cap is still served");
        beyond.ErrorMessages.Should().Equal(SearchCounts.BeyondCap);
        narrowed.Data.TotalCount.Should().Be(100, "a date range narrows it below the cap, counted exactly");
        narrowed.Data.TotalCountCapped.Should().BeFalse();
    }

    [Fact]
    public async Task AlertSearch_Should_CountUpToTheCapAndSaySo_When_MoreAlertsMatch()
    {
        var admin = await _host.CreateUserAsync("it.count.alerts", roles: [RoleCodes.SystemAdministrator], allSites: true);
        (await _host.ReadAsync<long>("""
            WITH site AS (
                INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), 'CNT', 'Counted') RETURNING code),
            rule AS (
                INSERT INTO alert_rule (id, site_code, code, name, scope_zones, metric, comparator, threshold, sustain_minutes, clear_after_minutes,
                                        severity, notify_by_email, enabled, created_on, created_by)
                SELECT gen_random_uuid(), code, 'R-001', 'Long queue', 'Q1', 'QueueLength', 'GreaterThan', 10, 1, 1, 'Warning', false, true, now(), 'it'
                FROM site RETURNING id, site_code, code, name),
            planted AS (
                INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, metric, severity, raised_utc, raised_value, state)
                SELECT gen_random_uuid(), r.site_code, r.id, r.code, r.name, 'Q' || g, 'QueueLength', 'Warning',
                       timestamptz '2026-01-01T00:00:00Z' + g * interval '1 minute', 12, 'Raised'
                FROM rule r CROSS JOIN generate_series(1, 10005) g
                RETURNING 1)
            SELECT count(*) FROM planted
            """)).Should().Be(10_005);
        Task<Fluentx.Result<Ariva.Core.Domain.ViewModels.PageViewModel<Ariva.Core.Domain.ViewModels.AlertViewModel>>> Search(AlertCriteria criteria) =>
            _host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcAlerts>().SearchAsync(criteria, Ct));

        var (first, sent) = await CapturingAsync("alert", () => Search(new AlertCriteria { SiteCode = "CNT", PageSize = 500 }));
        var beyond = await Search(new AlertCriteria { SiteCode = "CNT", PageSize = 500, PageIndex = 21 });
        var narrowed = await Search(new AlertCriteria
        {
            SiteCode = "CNT",
            FromUtc = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc),
            ToUtc = new DateTime(2026, 1, 1, 0, 30, 0, DateTimeKind.Utc)
        });
        var reversed = await Search(new AlertCriteria
        {
            SiteCode = "CNT",
            FromUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            ToUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        first.HasErrors.Should().BeFalse(string.Join(" ", first.ErrorMessages ?? []));
        first.Data.TotalCount.Should().Be(SearchCounts.Cap);
        first.Data.TotalCountCapped.Should().BeTrue();
        first.Data.Data.Should().HaveCount(500);
        sent.Should().NotContain(sql => Counts(sql), "past the cap the matches are not counted");
        beyond.ErrorMessages.Should().Equal(SearchCounts.BeyondCap);
        narrowed.Data.TotalCount.Should().Be(30);
        narrowed.Data.TotalCountCapped.Should().BeFalse();
        reversed.ErrorMessages.Should().ContainSingle().Which.Should().Be(TopologyErrors.InvalidCriteria, "a range that ends before it starts is refused, as the audit search does");
    }
}
