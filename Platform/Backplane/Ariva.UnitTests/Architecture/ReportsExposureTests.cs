using System.Reflection;
using Ariva.Api.Common.Extensions;
using Ariva.Api.Main.Controllers.OpsArea.Reports;
using Ariva.Core.Services.Reports;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.RateLimiting;

namespace Ariva.UnitTests.Architecture;

/// <summary>
/// ARV-096 (CWE-400, CWE-770, CWE-863): the on-demand daily report and its CSV files, as their controller declares them. Both
/// actions share the <c>reports</c> concurrency policy (a few at once per host, a few waiting) and carry the <c>reports</c> request
/// timeout, the controller is site-scoped, and it reaches only the report service, whose read takes the request's token.
/// </summary>
public sealed class ReportsExposureTests
{
    [Fact]
    public void Controller_Should_LimitConcurrencyAndTime_When_ItsEndpointsAreReflected()
    {
        var controller = typeof(ReportsController);

        controller.GetCustomAttribute<EnableRateLimitingAttribute>().PolicyName.Should().Be(RateLimitingExtensions.ReportsPolicy);
        controller.GetCustomAttribute<Ariva.Api.Common.Security.SiteScopedAttribute>().Should().NotBeNull();
        controller.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).Should().Equal(typeof(ISvcReports));
        foreach (var name in new[] { nameof(ReportsController.Daily), nameof(ReportsController.DailyCsv) })
        {
            var action = controller.GetMethod(name);
            action.GetCustomAttribute<RequestTimeoutAttribute>().PolicyName.Should().Be(RequestTimeoutsExtensions.ReportsPolicy, name);
            action.GetCustomAttribute<DisableRateLimitingAttribute>().Should().BeNull("{0} keeps the controller's policy", name);
            action.GetParameters().Last().ParameterType.Should().Be(typeof(CancellationToken), "{0} passes the request's token, which the timeout cancels", name);
        }

        new Ariva.Api.Common.Settings.RateLimitingSettings().Reports.Should().Match<Ariva.Api.Common.Settings.ConcurrencySettings>(
            s => s.PermitLimit == 2 && s.QueueLimit == 4, "a few reports at once per host, a few more waiting for a turn");
        new Ariva.Api.Common.Settings.RequestTimeoutsSettings().ReportsSeconds.Should().Be(60);
    }

    [Fact]
    public void Service_Should_TakeTheCallersToken_When_ItsReadIsReflected()
    {
        // The request timeout only cancels HttpContext.RequestAborted: the service and its reader must carry the token to the
        // database (ReportReader passes it to every ExecuteSqlAsync, and NHibernate to Npgsql's command).
        var daily = typeof(ISvcReports).GetMethod(nameof(ISvcReports.DailyAsync));
        daily.GetParameters().Last().ParameterType.Should().Be(typeof(CancellationToken));

        var reader = typeof(Ariva.Infra.Services.Reports.ReportReader);
        foreach (var method in reader.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(m => m.Name.EndsWith("Async", StringComparison.Ordinal)))
            method.GetParameters().Last().ParameterType.Should().Be(typeof(CancellationToken), "{0} reads the database", method.Name);
    }
}
