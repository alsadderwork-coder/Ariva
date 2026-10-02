using Ariva.Infra.Alerting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.Di.Extensions;

/// <summary>The live alert evaluation of Ariva.Api.Stream (ARV-038).</summary>
public static class AlertingExtensions
{
    /// <summary>
    /// Registers the evaluation, what it reads, and, when <c>Alerts:Evaluation:Enabled</c>, its worker (one tick a
    /// minute). Call after <c>RegisterArivaServices</c> (persistence and the unit of work).
    /// </summary>
    public static IServiceCollection AddArivaAlertEvaluation(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = configuration.GetSection(AlertEvaluationSettings.SectionName).Get<AlertEvaluationSettings>() ?? new AlertEvaluationSettings();
        var problems = settings.Problems().ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
        services.AddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        // What each rule's evaluation reads and runs with (also registered for Api.Main's backtest).
        services.TryAddSingleton<Ariva.Core.Alerting.IArrivalWaveSource, Ariva.Core.Alerting.NoArrivalWave>();
        services.TryAddScoped<AlertInputs>();
        services.TryAddScoped<AlertRuleTick>();
        // Raised and escalated alerts write their emails (ARV-040); Integration sends them.
        services.AddArivaEmailOutbox(configuration);
        // Escalations are audited (ARV-039) and changes announced to the live hub (Redis, or nothing without it).
        services.TryAddScoped<Ariva.Infra.Services.Administration.AuditTrail>();
        services.TryAddSingleton<Ariva.Infra.Live.IAlertNotices, Ariva.Infra.Live.NoAlertNotices>();
        services.TryAddSingleton<AlertEvaluation>();
        if (settings.Enabled)
            services.AddHostedService<AlertEvaluationWorker>();
        return services;
    }
}
