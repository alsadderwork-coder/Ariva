using Ariva.Core.Security;
using Ariva.Infra.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NHibernate.Linq;

namespace Ariva.Infra.Security;

/// <summary>
/// vm-local only: creates the accounts listed in Auth:DevelopmentUsers at startup (developers and the E2E suite).
/// An existing account gets its password, roles and lock reset to the configured state, so every E2E run starts the
/// same. Registered only when the environment is vm-local; production accounts come from the installer (ARV-010c) and
/// user administration (ARV-011).
/// </summary>
internal sealed class DevelopmentUserSeed(IServiceScopeFactory scopes, AuthSettings settings, IDataProtectionProvider dataProtection, TimeProvider timeProvider, ILogger<DevelopmentUserSeed> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICurrentUser>().SetSystemUser(Guid.Empty, "development-seed");
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var storage = unitOfWork.StorageProvider;
        storage.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);

        // Sites created in this run: the queries below do not see inserts the session has not flushed yet.
        var createdSites = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in settings.DevelopmentUsers.Where(u => !string.IsNullOrWhiteSpace(u.UserName) && !string.IsNullOrEmpty(u.Password)))
        {
            var userName = UserNames.Normalize(entry.UserName);
            var user = await storage.Query<User>().FirstOrDefaultAsync(u => u.UserName == userName, cancellationToken);
            var created = user is null;
            user ??= new User(userName, entry.UserName, null);

            user.SetPassword(PasswordHasher.Hash(entry.Password), entry.Temporary);
            user.Unlock();
            user.Enable();
            user.ResetTotp();
            if (!string.IsNullOrWhiteSpace(entry.TotpSecret))
            {
                // Enrolled with a known secret, so the E2E suite can compute codes; any step from now on is new.
                user.BeginTotpEnrolment(dataProtection.CreateProtector(Totp.DataProtectionPurpose).Protect(entry.TotpSecret.Trim().ToUpperInvariant()));
                user.ConfirmTotp(0);
            }
            await storage.SaveOrUpdateAsync(user, cancellationToken);

            foreach (var role in entry.Roles.Where(r => !user.Roles.Any(existing => existing.RoleCode == r)))
            {
                user.Grant(role);
                await storage.SaveAsync(user.Roles.Last(), cancellationToken);
            }

            var siteCodes = entry.Sites.Where(code => code != "*").Distinct(StringComparer.Ordinal).ToList();
            foreach (var code in siteCodes)
            {
                if (!createdSites.Contains(code) && !await storage.Query<Site>().AnyAsync(s => s.Code == code, cancellationToken))
                {
                    await storage.SaveAsync(new Site(code, code), cancellationToken);
                    createdSites.Add(code);
                }
            }

            var (removed, added) = user.SetSites(entry.Sites.Contains("*"), siteCodes, null, timeProvider.GetUtcNow().UtcDateTime);
            foreach (var binding in removed)
                await storage.DeleteAsync(binding, cancellationToken);
            foreach (var binding in added)
                await storage.SaveAsync(binding, cancellationToken);
            await storage.UpdateAsync(user, cancellationToken);

            logger.LogInformation("Development account {UserName} {Action} with roles {Roles}", userName, created ? "created" : "reset", entry.Roles);
        }

        unitOfWork.PromiseToCommit();
        await unitOfWork.EndAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
