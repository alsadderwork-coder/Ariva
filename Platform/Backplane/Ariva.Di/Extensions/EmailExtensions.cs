using Ariva.Infra.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.Di.Extensions;

/// <summary>Email notifications (ARV-040).</summary>
public static class EmailExtensions
{
    /// <summary>Where <see cref="SmtpSettings.AllowInsecure"/> may be on.</summary>
    public static readonly IReadOnlySet<string> InsecureEnvironments = new HashSet<string>(["vm-local", "k8s-dev"], StringComparer.Ordinal);

    /// <summary>
    /// What writes alert emails (every host that changes alerts): the settings (<c>Email</c>, without the relay or its
    /// credential), the templates of Ariva.Resources and <see cref="AlertEmails"/>. Refuses settings that do not hold together.
    /// </summary>
    public static IServiceCollection AddArivaEmailOutbox(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = Settings(configuration);
        var problems = settings.Problems().ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(" ", problems));
        services.TryAddSingleton(settings);
        services.TryAddSingleton<EmailTemplates>();
        services.TryAddScoped<AlertEmails>();
        return services;
    }

    /// <summary>
    /// What sends them (Ariva.Api.Integration, the host with egress to the mail relay): the relay (<c>Email:Smtp</c>,
    /// read here only), MailKit, the sender and, when <c>Email:Enabled</c>, its worker. Clear text (smtp4dev) only in
    /// vm-local and k8s-dev.
    /// </summary>
    public static IServiceCollection AddArivaEmailSending(this IServiceCollection services, IConfiguration configuration)
    {
        if (AddRelay(services, configuration))
            services.AddHostedService<EmailSenderWorker>();
        services.TryAddScoped<EmailSender>();
        return services;
    }

    /// <summary>
    /// The scheduled reports (ARV-060, Ariva.Api.Cronz): the relay as for alert emails (the same settings and checks, no
    /// alert sending worker) and the delivery round TickerQ runs. With <c>Email:Enabled</c> off a round sends nothing.
    /// </summary>
    public static IServiceCollection AddArivaReportDeliveries(this IServiceCollection services, IConfiguration configuration)
    {
        AddRelay(services, configuration);
        services.TryAddScoped<Ariva.Infra.Services.Reports.ReportReader>();
        services.TryAddScoped<Ariva.Core.Services.Reports.ISvcReportDeliveries, Ariva.Infra.Services.Reports.SvcReportDeliveries>();
        services.TryAddSingleton<Ariva.Infra.Services.Reports.ReportDeliveryRound>();
        return services;
    }

    /// <summary>The settings, the relay (checked when sending is on) and MailKit; true when sending is on.</summary>
    private static bool AddRelay(IServiceCollection services, IConfiguration configuration)
    {
        services.AddArivaEmailOutbox(configuration);
        var smtp = configuration.GetSection(SmtpSettings.SectionName).Get<SmtpSettings>() ?? new SmtpSettings();
        var enabled = Settings(configuration).Enabled;
        if (enabled)
        {
            var problems = smtp.Problems().ToList();
            var environment = configuration["Application:Environment"];
            if (smtp.AllowInsecure && !InsecureEnvironments.Contains(environment ?? string.Empty))
                problems.Add($"Email:Smtp:AllowInsecure is only allowed in {string.Join(" and ", InsecureEnvironments.Order(StringComparer.Ordinal))}.");
            if (problems.Count > 0)
                throw new InvalidOperationException(string.Join(" ", problems));
        }

        services.TryAddSingleton(smtp);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEmailTransport, MailKitTransport>();
        return enabled;
    }

    private static EmailSettings Settings(IConfiguration configuration) =>
        configuration.GetSection(EmailSettings.SectionName).Get<EmailSettings>() ?? new EmailSettings();
}
