using System.Globalization;
using System.Text.Json;
using Ariva.Core.Domain.Components;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Settings;
using Npgsql;

namespace Ariva.Infra.Timescale;

/// <summary>One stored text the rule for its column refuses: where it is, and what breaks the rule, escaped.</summary>
/// <param name="Place">The table and column, for example <c>zone.name</c>, <c>display.entries</c> or <c>zone.polygon</c>.</param>
/// <param name="SiteCode">The row's site; none for an airport, a user, an integration client or an outbound endpoint.</param>
/// <param name="Id">The row's id.</param>
/// <param name="State">
/// <c>live</c>, <c>deleted</c> (soft deleted) or <c>disabled</c> (a user); for zone profiles, zones, lines and outlines the
/// profile's status (<c>draft</c>, <c>published</c>, <c>retired</c>); for alert notes the alert's state.
/// </param>
/// <param name="Escaped">
/// What breaks the rule, safe to print on any terminal: the whole value as <see cref="DisplayText.Escape"/> writes it, or
/// for a list or a display's labels the parts that break the rule (separated by <c> | </c>), or for an outline the zone's
/// name; for a personal text only the offending code points and their positions, never the text.
/// </param>
public sealed record StoredNameFinding(string Place, string SiteCode, Guid Id, string State, string Escaped);

/// <summary>What the check read and what it found.</summary>
public sealed record StoredNameCheckResult(int Checked, IReadOnlyList<StoredNameFinding> Findings);

/// <summary>
/// The pre-pilot check (ARV-159, widened by ARV-162): lists the stored texts that the rule for their column now refuses, so
/// they can be cleaned before the pilot, and the zone outlines that touch or cross themselves (ARV-161). Covered:
/// <list type="bullet">
/// <item>site and topology names (sites, airports, terminals, levels, checkpoints, desks, zone profiles, zones, lines) and
/// device queue zone names, by the name rule (<see cref="TopologyCodes.IsName"/>);</item>
/// <item>every other stored text <see cref="DisplayText"/> guards, by its own rule: one line (device models, alert rule
/// names and escalation contacts, display names and locations, integration client, outbound endpoint and report schedule
/// names, calendar reasons, validation campaign names, manual count and desk observation reasons), line breaks allowed
/// (alert notes, calibration notes), a list of one-line texts (an alert rule's scope zones), JSON (a display's zone names
/// and labels, its stale messages), or a person's name (user display names, which may hold the zero-width joiner and
/// non-joiner);</item>
/// <item>every zone's outline, by <see cref="Geometry.IsSimplePolygon"/>: such a zone blocks publication;</item>
/// <item>floor plan file names, by the upload's rule (<see cref="FloorPlan.IsSafeFileName"/>; review R1).</item>
/// </list>
/// A personal text (a user's display name, an escalation contact, an alert or calibration note, a manual count or desk
/// observation reason) is never printed: its finding shows only the offending code points and their positions (CWE-532).
/// Not read: the immutable evidence (the sensing and health archives, replay runs, audit entries, alerts' copies of rule
/// and zone names, validation campaigns' copies of zone, line and queue zone names, emails), whose rows nobody can rename,
/// and validation result revisions, which only the results service reads (ValidationResultsExposureTests). The email
/// sender name is configuration, checked when a host starts.
/// <para>
/// It only reads. It runs on the hosts' runtime login (Database:Username), which reads these tables already; the connection
/// asks PostgreSQL for read-only transactions (<c>default_transaction_read_only</c>) and the one statement runs, streamed,
/// in a <c>READ ONLY</c> transaction that is rolled back, so any write would fail (SQLSTATE 25006). The statement is a
/// constant; nothing is built from data (CWE-89).
/// </para>
/// </summary>
public sealed class StoredNameCheck(DatabaseSettings database)
{
    #region Constants

    internal const string NameRule = "name";
    internal const string TextRule = "text";
    internal const string NoteRule = "note";
    internal const string PersonRule = "person";
    internal const string ListRule = "list";
    internal const string EntriesRule = "entries";
    internal const string MessagesRule = "messages";
    internal const string OutlineRule = "outline";
    internal const string FileRule = "file";

    /// <summary>Written for a JSON column that does not read as the entity writes it.</summary>
    internal const string Unreadable = "(unreadable)";

    /// <summary>Every covered text, one row per stored value, with the rule that applies to it. Soft-deleted rows are read too, and marked.</summary>
    internal const string Sql = """
        SELECT 'site.name' AS place, code AS site_code, id, name AS value, 'live' AS state, 'name' AS rule, false AS personal, NULL::text AS label FROM site
        UNION ALL SELECT 'airport.name', NULL::varchar, id, name, CASE WHEN deleted_on IS NULL THEN 'live' ELSE 'deleted' END, 'name', false, NULL FROM airport
        UNION ALL SELECT 'terminal.name', site_code, id, name, CASE WHEN deleted_on IS NULL THEN 'live' ELSE 'deleted' END, 'name', false, NULL FROM terminal
        UNION ALL SELECT 'level.name', site_code, id, name, CASE WHEN deleted_on IS NULL THEN 'live' ELSE 'deleted' END, 'name', false, NULL FROM level
        UNION ALL SELECT 'checkpoint.name', site_code, id, name, CASE WHEN deleted_on IS NULL THEN 'live' ELSE 'deleted' END, 'name', false, NULL FROM checkpoint
        UNION ALL SELECT 'desk.name', site_code, id, name, CASE WHEN deleted_on IS NULL THEN 'live' ELSE 'deleted' END, 'name', false, NULL FROM desk
        UNION ALL SELECT 'floor_plan.original_file_name', site_code, id, original_file_name, CASE WHEN deleted_on IS NULL THEN 'live' ELSE 'deleted' END, 'file', false, NULL
                    FROM floor_plan WHERE original_file_name IS NOT NULL
        UNION ALL SELECT 'zone_profile.name', site_code, id, name, lower(status), 'name', false, NULL FROM zone_profile
        UNION ALL SELECT 'zone.name', p.site_code, z.id, z.name, lower(p.status), 'name', false, NULL FROM zone z JOIN zone_profile p ON p.id = z.profile_id
        UNION ALL SELECT 'zone.polygon', p.site_code, z.id, z.polygon, lower(p.status), 'outline', false, z.name FROM zone z JOIN zone_profile p ON p.id = z.profile_id
        UNION ALL SELECT 'line.name', p.site_code, l.id, l.name, lower(p.status), 'name', false, NULL FROM line l JOIN zone_profile p ON p.id = l.profile_id
        UNION ALL SELECT 'device.queue_zone_name', site_code, id, queue_zone_name, CASE WHEN deleted_on IS NULL THEN 'live' ELSE 'deleted' END, 'name', false, NULL FROM device
        UNION ALL SELECT 'device.model', site_code, id, model, CASE WHEN deleted_on IS NULL THEN 'live' ELSE 'deleted' END, 'text', false, NULL FROM device
        UNION ALL SELECT 'device_calibration.notes', site_code, id, notes, 'live', 'note', true, NULL FROM device_calibration WHERE notes IS NOT NULL
        UNION ALL SELECT 'alert_rule.name', site_code, id, name, CASE WHEN deleted_on IS NULL THEN 'live' ELSE 'deleted' END, 'text', false, NULL FROM alert_rule
        UNION ALL SELECT 'alert_rule.scope_zones', site_code, id, scope_zones, CASE WHEN deleted_on IS NULL THEN 'live' ELSE 'deleted' END, 'list', false, NULL FROM alert_rule
        UNION ALL SELECT 'alert_rule.escalation_contact', site_code, id, escalation_contact, CASE WHEN deleted_on IS NULL THEN 'live' ELSE 'deleted' END, 'text', true, NULL
                    FROM alert_rule WHERE escalation_contact IS NOT NULL
        UNION ALL SELECT 'alert.acknowledged_note', site_code, id, acknowledged_note, lower(state), 'note', true, NULL FROM alert WHERE acknowledged_note IS NOT NULL
        UNION ALL SELECT 'alert.escalated_note', site_code, id, escalated_note, lower(state), 'note', true, NULL FROM alert WHERE escalated_note IS NOT NULL
        UNION ALL SELECT 'alert.resolution_note', site_code, id, resolution_note, lower(state), 'note', true, NULL FROM alert WHERE resolution_note IS NOT NULL
        UNION ALL SELECT 'display.name', site_code, id, name, CASE WHEN deleted_on IS NULL THEN 'live' ELSE 'deleted' END, 'text', false, NULL FROM display
        UNION ALL SELECT 'display.location', site_code, id, location, CASE WHEN deleted_on IS NULL THEN 'live' ELSE 'deleted' END, 'text', false, NULL
                    FROM display WHERE location IS NOT NULL
        UNION ALL SELECT 'display.entries', site_code, id, entries, CASE WHEN deleted_on IS NULL THEN 'live' ELSE 'deleted' END, 'entries', false, NULL FROM display
        UNION ALL SELECT 'display.fallback_messages', site_code, id, fallback_messages, CASE WHEN deleted_on IS NULL THEN 'live' ELSE 'deleted' END, 'messages', false, NULL
                    FROM display
        UNION ALL SELECT 'integration_client.name', NULL, id, name, 'live', 'text', false, NULL FROM integration_client
        UNION ALL SELECT 'outbound_endpoint.name', NULL, id, name, 'live', 'text', false, NULL FROM outbound_endpoint
        UNION ALL SELECT 'report_schedule.name', site_code, id, name, CASE WHEN deleted_on IS NULL THEN 'live' ELSE 'deleted' END, 'text', false, NULL FROM report_schedule
        UNION ALL SELECT 'user.display_name', NULL, id, display_name, CASE WHEN is_disabled THEN 'disabled' ELSE 'live' END, 'person', true, NULL
                    FROM "user" WHERE display_name IS NOT NULL
        UNION ALL SELECT 'operating_day_exception.reason', site_code, id, reason, CASE WHEN deleted_on IS NULL THEN 'live' ELSE 'deleted' END, 'text', false, NULL
                    FROM operating_day_exception
        UNION ALL SELECT 'maintenance_window.reason', site_code, id, reason, CASE WHEN deleted_on IS NULL THEN 'live' ELSE 'deleted' END, 'text', false, NULL
                    FROM maintenance_window
        UNION ALL SELECT 'validation_campaign.name', site_code, id, name, 'live', 'text', false, NULL FROM validation_campaign
        UNION ALL SELECT 'manual_count.reason', site_code, id, reason, 'live', 'text', true, NULL FROM manual_count WHERE reason IS NOT NULL
        UNION ALL SELECT 'desk_observation.reason', site_code, id, reason, 'live', 'text', true, NULL FROM desk_observation WHERE reason IS NOT NULL
        ORDER BY 1, 2, 3
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    #endregion

    #region Methods

    /// <summary>Reads every covered value once and returns those that break their rule, in place, site and id order.</summary>
    public async Task<StoredNameCheckResult> RunAsync(CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(ReadOnlyConnectionString(database));
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(ct);

        var findings = new List<StoredNameFinding>();
        var read = 0;
        await using (var command = new NpgsqlCommand(Sql, connection, transaction) { CommandTimeout = DatabaseSettings.LongestRuntimeCommandSeconds })
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                read++;
                var rule = reader.GetString(5);
                var value = reader.GetString(3);
                var breaches = Breaches(rule, value, reader.IsDBNull(7) ? null : reader.GetString(7));
                if (breaches.Count > 0)
                {
                    // ARV-162 (review, CWE-532): a personal text (a person's name, a contact, a note, a reason) is never printed;
                    // only the code points that break its rule and where they are.
                    var shown = reader.GetBoolean(6) ? Positions(rule, value) : string.Join(" | ", breaches);
                    findings.Add(new StoredNameFinding(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetGuid(2), reader.GetString(4), shown));
                }
            }
        }

        await transaction.RollbackAsync(ct);
        return new StoredNameCheckResult(read, findings);
    }

    /// <summary>
    /// What in a stored value breaks the rule for its column, each part escaped; empty when it passes. Every rule is the one
    /// the entity or service applies when the value is written, called, never copied.
    /// </summary>
    internal static IReadOnlyList<string> Breaches(string rule, string value, string label = null) => rule switch
    {
        NameRule => TopologyCodes.IsName(value) ? [] : [DisplayText.Escape(value)],
        TextRule => DisplayText.IsClean(value) ? [] : [DisplayText.Escape(value)],
        NoteRule => DisplayText.IsClean(value, allowLineBreaks: true) ? [] : [DisplayText.Escape(value)],
        PersonRule => SvcUsers.IsUsableDisplayName(value) ? [] : [DisplayText.Escape(value)],
        ListRule => [.. (value ?? string.Empty).Split('\n').Where(item => !DisplayText.IsClean(item)).Select(DisplayText.Escape)],
        EntriesRule => EntryBreaches(value),
        MessagesRule => MessageBreaches(value),
        OutlineRule => Geometry.ParseRing(value, Zone.MaxVertices) is { } ring && Geometry.IsSimplePolygon(ring) ? [] : [DisplayText.Escape(label)],
        FileRule => FloorPlan.IsSafeFileName(value) ? [] : [DisplayText.Escape(value)],
        _ => throw new ArgumentOutOfRangeException(nameof(rule))
    };

    /// <summary>
    /// What a finding shows of a personal text (ARV-162, CWE-532): only the code points that break its rule and their
    /// positions, counted in code points from 1 (a person's name may hold the zero-width joiner and non-joiner); when the
    /// rule breaks without such a code point (a value longer than its rule), only that.
    /// </summary>
    internal static string Positions(string rule, string value)
    {
        var offenders = DisplayText.Offenders(value, allowLineBreaks: rule == NoteRule)
            .Where(o => rule != PersonRule || o.CodePoint is not (0x200C or 0x200D))
            .Select(o => string.Create(CultureInfo.InvariantCulture, $"U+{o.CodePoint:X4} at {o.Position}"))
            .ToList();
        return offenders.Count == 0 ? "(longer than its rule allows; text not shown)" : string.Join(", ", offenders) + " (text not shown)";
    }

    /// <summary>A display's entries: each zone name and each label, judged as one line.</summary>
    private static IReadOnlyList<string> EntryBreaches(string json)
    {
        List<DisplayEntry> entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<DisplayEntry>>(json ?? string.Empty, Json);
        }
        catch (JsonException)
        {
            return [Unreadable];
        }

        if (entries is null)
            return [Unreadable];
        return [.. entries.SelectMany(e => new[] { e?.Zone }.Concat(e?.Labels?.Values ?? [])).Where(text => !DisplayText.IsClean(text)).Select(DisplayText.Escape)];
    }

    /// <summary>A display's stale messages, one per language, judged as one line each.</summary>
    private static IReadOnlyList<string> MessageBreaches(string json)
    {
        Dictionary<string, string> messages;
        try
        {
            messages = JsonSerializer.Deserialize<Dictionary<string, string>>(json ?? string.Empty, Json);
        }
        catch (JsonException)
        {
            return [Unreadable];
        }

        return messages is null ? [Unreadable] : [.. messages.Values.Where(text => !DisplayText.IsClean(text)).Select(DisplayText.Escape)];
    }

    /// <summary>
    /// The runtime login's connection with every transaction read-only by default (PostgreSQL's
    /// <c>default_transaction_read_only</c>), under its own application name.
    /// </summary>
    internal static string ReadOnlyConnectionString(DatabaseSettings database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new NpgsqlConnectionStringBuilder(database.BuildConnectionString())
        {
            ApplicationName = "ariva-stored-name-check",
            Options = "-c default_transaction_read_only=on",
            // A one-off command (ARV-232): its connection is closed for good, not kept in a pool.
            Pooling = false
        }.ConnectionString;
    }

    #endregion
}
