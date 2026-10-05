using System.Text.Json;
using Ariva.Core;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Infra.Caching;
using Ariva.Infra.Messaging;
using StackExchange.Redis;

namespace Ariva.Infra.Live;

/// <summary>
/// An alert as screens are told of it when it is raised or changes (ARV-039): what it is about, its state and who it is
/// for. No identities: the people who acknowledged, escalated or resolved it are in the API's answer, not here.
/// </summary>
public sealed record AlertNotice(
    Guid AlertId,
    string SiteCode,
    string RuleCode,
    string RuleName,
    string ZoneName,
    string DeviceCode,
    string Metric,
    string Severity,
    string State,
    DateTime RaisedUtc,
    double RaisedValue,
    string OwnerRole,
    string EscalateToRole,
    bool Escalated,
    DateTime? ResolvedUtc,
    DateTime PublishedUtc)
{
    public static AlertNotice From(Alert alert, DateTime publishedUtc)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return new AlertNotice(alert.Id!.Value, alert.SiteCode, alert.RuleCode, alert.RuleName, alert.ZoneName, alert.DeviceCode, alert.Metric.ToString(),
            alert.Severity.ToString(), alert.State.ToString(), alert.RaisedUtc, alert.RaisedValue, alert.OwnerRole, alert.EscalateToRole, alert.EscalatedUtc is not null,
            alert.ResolvedUtc, publishedUtc);
    }

    /// <summary>
    /// The roles whose screens are told: its owner role, and its escalation role once escalated, and always the
    /// administrators; every operational role of the site when it has no owner role (as <see cref="Alert.IsFor"/>).
    /// </summary>
    public IReadOnlyList<string> Audience() =>
        OwnerRole is null
            ? RoleCodes.All
            : [.. new[] { OwnerRole, Escalated ? EscalateToRole : null, RoleCodes.SystemAdministrator }.Where(r => r is not null).Distinct(StringComparer.Ordinal)];

    /// <summary>Whether a notice read back from Redis is plausible enough to send to a screen (CWE-501).</summary>
    public static bool Plausible(AlertNotice n) =>
        n is not null && n.AlertId != Guid.Empty && Site.IsValidCode(n.SiteCode) && n.RuleCode is { Length: <= 8 } && n.RuleCode.StartsWith("R-", StringComparison.Ordinal) &&
        Text(n.RuleName, AlertRule.MaxNameLength) && Text(n.ZoneName, 200) && (n.DeviceCode is null || Ariva.Core.Sensing.DeviceCodes.IsValid(n.DeviceCode)) &&
        Named<AlertMetric>(n.Metric) && Named<AlertSeverity>(n.Severity) && Named<AlertState>(n.State) &&
        double.IsFinite(n.RaisedValue) && Math.Abs(n.RaisedValue) <= Alert.MaxRecordedValue && Role(n.OwnerRole) && Role(n.EscalateToRole) &&
        n.RaisedUtc.Year is >= 2000 and < 9000 && n.PublishedUtc.Year is >= 2000 and < 9000 && (n.ResolvedUtc is null || n.ResolvedUtc.Value.Year is >= 2000 and < 9000);

    private static bool Text(string value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && DisplayText.IsClean(value);

    private static bool Role(string role) => role is null || Enumerable.Contains(RoleCodes.All, role, StringComparer.Ordinal);

    private static bool Named<T>(string value) where T : struct, Enum => value is not null && Enumerable.Contains(Enum.GetNames<T>(), value, StringComparer.Ordinal);
}

/// <summary>Where alert notices are announced.</summary>
public interface IAlertNotices
{
    Task PublishAsync(IReadOnlyCollection<AlertNotice> notices, CancellationToken ct);

    /// <summary>Calls <paramref name="handler"/> for every notice announced from now on.</summary>
    Task SubscribeAsync(Func<AlertNotice, Task> handler, CancellationToken ct);
}

/// <summary>No Redis configured: nothing is announced (development without the live hub).</summary>
public sealed class NoAlertNotices : IAlertNotices
{
    public Task PublishAsync(IReadOnlyCollection<AlertNotice> notices, CancellationToken ct) => Task.CompletedTask;

    public Task SubscribeAsync(Func<AlertNotice, Task> handler, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Alert notices on the Redis channel <c>{instance}live:alerts</c>, JSON of at most 4 KB; anything read back that is
/// larger, unreadable or implausible is dropped (CWE-501: Redis is shared infrastructure).
/// </summary>
public sealed class RedisAlertNotices(RedisConnection redis) : IAlertNotices
{
    public const int MaxBytes = 4 * 1024;

    private RedisChannel Channel => RedisChannel.Literal($"{redis.Settings.InstanceName}live:alerts");

    public async Task PublishAsync(IReadOnlyCollection<AlertNotice> notices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notices);
        if (notices.Count == 0)
            return;
        var subscriber = (await redis.GetAsync()).GetSubscriber();
        foreach (var notice in notices.Where(AlertNotice.Plausible))
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(notice, EventCatalog.Json);
            if (json.Length <= MaxBytes)
                await subscriber.PublishAsync(Channel, json).WaitAsync(RedisLiveSnapshots.OperationTimeout, ct);
        }
    }

    public async Task SubscribeAsync(Func<AlertNotice, Task> handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var queue = await (await redis.GetAsync()).GetSubscriber().SubscribeAsync(Channel);
        queue.OnMessage(async message =>
        {
            if (Read(message.Message) is { } notice)
                await handler(notice);
        });
        ct.Register(() => queue.Unsubscribe());
    }

    internal static AlertNotice Read(RedisValue value)
    {
        if (value.IsNullOrEmpty)
            return null;
        byte[] bytes = value;
        if (bytes is null || bytes.Length > MaxBytes)
            return null;
        try
        {
            var notice = JsonSerializer.Deserialize<AlertNotice>(bytes, EventCatalog.Json);
            return AlertNotice.Plausible(notice) ? notice : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
