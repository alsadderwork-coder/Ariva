using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Domain.Constants;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Security;
using Ariva.Infra.Security;
using Ariva.UnitTests.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.UnitTests.Security;

/// <summary>
/// ARV-011: role ranks for grants, the append-only audit entry, temporary passwords that the policy accepts, and an
/// audit API that can only read.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class AdministrationTests
{
    #region Role ranks

    [Theory]
    [InlineData(new[] { RoleCodes.SystemAdministrator }, RoleCodes.SystemAdministrator, true)]
    [InlineData(new[] { RoleCodes.SystemAdministrator }, RoleCodes.BorderShiftSupervisor, true)]
    [InlineData(new[] { RoleCodes.TerminalDutyManager }, RoleCodes.HandlerStationManager, true)]
    [InlineData(new[] { RoleCodes.TerminalDutyManager }, RoleCodes.SystemAdministrator, false)]
    [InlineData(new[] { RoleCodes.BorderShiftSupervisor, RoleCodes.TerminalDutyManager }, RoleCodes.SystemAdministrator, false)]
    [InlineData(new string[0], RoleCodes.BorderShiftSupervisor, false)]
    [InlineData(new[] { RoleCodes.SystemAdministrator }, "Superuser", false)]
    public void CanAssign_Should_AllowOnlyRolesAtOrBelowTheGranter_When_Checked(string[] granter, string role, bool allowed)
    {
        RoleHierarchy.CanAssign(granter, role).Should().Be(allowed);
    }

    [Fact]
    public void Rank_Should_CoverEveryRoleCode_When_Read()
    {
        RoleCodes.All.Should().OnlyContain(code => RoleHierarchy.Rank(code) > 0);
        RoleHierarchy.Rank(RoleCodes.SystemAdministrator).Should().BeGreaterThan(RoleCodes.All.Where(c => c != RoleCodes.SystemAdministrator).Max(RoleHierarchy.Rank));
        RoleHierarchy.Rank(null).Should().Be(0);
    }

    #endregion

    #region Entities

    [Fact]
    public void AuditEntry_Should_ClipLongTextAndRequireUtc_When_Created()
    {
        var now = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        var entry = new AuditEntry(now, Guid.NewGuid(), "admin", AuditActions.UserCreated, AuditActions.UserTarget, Guid.NewGuid(), "x",
            null, new string('a', 5000), "10.0.0.1", "trace");

        entry.AfterSummary.Should().HaveLength(AuditEntry.SummaryLength);
        entry.BeforeSummary.Should().BeNull();
        var local = () => new AuditEntry(DateTime.Now, null, null, AuditActions.UserCreated, AuditActions.UserTarget, null, null, null, null, null, null);
        local.Should().Throw<ArgumentException>();
        var noAction = () => new AuditEntry(now, null, null, " ", AuditActions.UserTarget, null, null, null, null, null, null);
        noAction.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UserRoles_Should_RecordWhoGrantedAndRevokeOnce_When_Changed()
    {
        var user = new User("ops.one", "Ops One", null);
        var granter = Guid.NewGuid();
        var at = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);

        var grant = user.Grant(RoleCodes.TerminalDutyManager, granter, at);
        var again = user.Grant(RoleCodes.TerminalDutyManager, granter, at);

        grant.GrantedById.Should().Be(granter);
        grant.GrantedOn.Should().Be(at);
        again.Should().BeNull("a role is held once");
        user.Holds(RoleCodes.TerminalDutyManager).Should().BeTrue();
        user.Revoke(RoleCodes.TerminalDutyManager).Should().BeSameAs(grant);
        user.Revoke(RoleCodes.TerminalDutyManager).Should().BeNull();
        user.Holds(RoleCodes.TerminalDutyManager).Should().BeFalse();
    }

    [Fact]
    public void UpdateProfile_Should_StoreBlankAsNull_When_Cleared()
    {
        var user = new User("ops.two", "Ops Two", "ops@example.org");

        user.UpdateProfile("  ", " new@example.org ");

        user.DisplayName.Should().BeNull();
        user.Email.Should().Be("new@example.org");
    }

    #endregion

    #region Temporary passwords

    [Fact]
    public void TemporaryPasswords_Should_PassThePolicyAndNeverRepeat_When_Generated()
    {
        var policy = new PasswordPolicy(["ariva", "AMM"]);
        var passwords = Enumerable.Range(0, 200).Select(_ => TemporaryPasswords.New()).ToList();

        passwords.Should().OnlyHaveUniqueItems();
        passwords.Should().OnlyContain(p => p.Length == 23 && p.Count(c => c == '-') == 3);
        passwords.Should().OnlyContain(p => policy.Validate(p, "ops.three").Count == 0);
        passwords.Should().OnlyContain(p => !p.Any(c => "0O1lI".Contains(c)), "no look-alike characters");
    }

    #endregion

    #region Audit API

    [Fact]
    public async Task AuditEndpoints_Should_OnlyRead_When_MainIsBooted()
    {
        await using var app = ArivaHosts.Create(ArivaHosts.Main);
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        string[] writes = [nameof(Global.Defaults.Permissions.CreateAuditEntry), nameof(Global.Defaults.Permissions.EditAuditEntry), nameof(Global.Defaults.Permissions.DeleteAuditEntry)];

        var audit = endpoints.Where(e => e.RoutePattern.RawText!.Contains("audit-entries", StringComparison.Ordinal)).ToList();
        audit.Should().NotBeEmpty();
        audit.SelectMany(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"]).Should().OnlyContain(method => method == HttpMethods.Get);
        endpoints.SelectMany(e => e.Metadata.GetOrderedMetadata<PermissionAttribute>()).SelectMany(p => p.PermissionNames)
            .Should().NotContain(writes, "no endpoint writes audit entries");
        RolePermissions.ByRole.Values.SelectMany(p => p).Where(p => p.Entity == "AuditEntry")
            .Should().OnlyContain(p => p.Action == PermissionAction.View || p.Action == PermissionAction.Search);
    }

    #endregion
}
