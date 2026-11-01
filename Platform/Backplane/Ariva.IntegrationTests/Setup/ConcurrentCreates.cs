using Ariva.Core.Security;
using Ariva.Core.Services;
using Ariva.IntegrationTests.Security;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.IntegrationTests.Setup;

/// <summary>
/// Two creates of one record at the same moment, held deterministically against PostgreSQL (ARV-278, ARV-281). The first
/// runs in a scope whose unit of work stays open once the service has returned (its row sent, or the site's row locked); the
/// second runs to its end in a scope of its own and must wait on the first (a lock not granted in the test's database)
/// before the first commits. A service that sends its row only at the commit lets the second run through instead, and the
/// harness fails there.
/// </summary>
internal static class ConcurrentCreates
{
    #region Methods

    /// <summary>Runs <paramref name="create"/> twice at once as <paramref name="caller"/>; the first must succeed.</summary>
    /// <returns>Both answers, the second read once the first has committed.</returns>
    public static async Task<(Fluentx.Result<T> First, Fluentx.Result<T> Second)> RaceAsync<T>(AccountsHost host, Guid caller,
        Func<IServiceProvider, Task<Fluentx.Result<T>>> create)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(create);
        Fluentx.Result<T> first;
        Task<Fluentx.Result<T>> second;
        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var user = (TestCurrentUser)scope.ServiceProvider.GetRequiredService<ICurrentUser>();
            user.Id = caller;
            user.UserName = "it-admin";
            first = await create(scope.ServiceProvider);
            first.HasErrors.Should().BeFalse(string.Join(", ", first.ErrorMessages ?? []));
            second = host.AsCallerAsync(caller, create);
            (await BlocksAsync(host, second)).Should().BeTrue("the second create waits on the first one, which has sent its row or holds the site's row");
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().EndAsync(TestContext.Current.CancellationToken);
        }

        return (first, await second);
    }

    /// <summary>True once <paramref name="work"/> waits for a lock in the host's database; false when it ends first, or after 30 seconds.</summary>
    public static async Task<bool> BlocksAsync(AccountsHost host, Task work)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(work);
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until && !work.IsCompleted)
        {
            if (await host.ReadAsync<long>("SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid WHERE NOT l.granted AND a.datname = current_database()") > 0)
                return true;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        return false;
    }

    #endregion
}
