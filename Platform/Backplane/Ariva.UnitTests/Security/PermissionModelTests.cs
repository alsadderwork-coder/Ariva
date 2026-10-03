using System.Reflection;
using System.Security.Claims;
using Ariva.Api.Common.Security;
using Ariva.Core;
using Ariva.Core.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ariva.UnitTests.Security;

/// <summary>ARV-009 (CWE-862, CWE-863): the permission catalogue, the role seed and the policy machinery.</summary>
public sealed class PermissionModelTests
{
    private static readonly PermissionAction[] CrudActions =
        [PermissionAction.View, PermissionAction.Create, PermissionAction.Edit, PermissionAction.Search, PermissionAction.Delete];

    #region Catalogue

    [Fact]
    public void Permissions_Should_HaveUniqueCodesAndConventionalNames_When_Declared()
    {
        var all = Global.Defaults.Permissions.All;

        all.Should().NotBeEmpty();
        all.Values.Select(p => p.Code).Should().OnlyHaveUniqueItems();
        all.Should().AllSatisfy(pair => pair.Key.Should().Be(pair.Value.Name, "the property name is action then entity, as in AMAN"));
    }

    [Fact]
    public void Permissions_Should_CoverViewCreateEditSearchDelete_When_EntityIsCatalogued()
    {
        var byEntity = Global.Defaults.Permissions.All.Values
            // View-only entities: nothing is created, edited, searched or deleted through them (the system information
            // endpoint; the live queue stream of ARV-035; the arrival-wave projection of ARV-047). Alerts (ARV-039) are raised by the evaluation and never
            // deleted: people view, search and act on them (Edit) only.
            .Where(p => p.Entity is not ("SystemInfo" or "LiveQueue" or "Alert" or "ArrivalWave" or "ArrivalWaveLanes"))
            .GroupBy(p => p.Entity);

        byEntity.Should().AllSatisfy(entity =>
            entity.Select(p => p.Action).Should().Contain(CrudActions, $"{entity.Key} needs the five standard actions"));
    }

    [Fact]
    public void Find_Should_ReturnNull_When_NameIsUnknown()
    {
        Global.Defaults.Permissions.Find("ViewEverything").Should().BeNull();
        Global.Defaults.Permissions.Find(nameof(Global.Defaults.Permissions.ViewDesk)).Should().Be(Global.Defaults.Permissions.ViewDesk);
    }

    #endregion

    #region Role Seed

    [Fact]
    public void ByRole_Should_HaveAnEntryForEveryRoleCode_When_Seeded()
    {
        RolePermissions.ByRole.Keys.Should().BeEquivalentTo(RoleCodes.All);
        RolePermissions.ByRole.Values.Should().AllSatisfy(grants => grants.Should().NotBeEmpty());
        RolePermissions.ByRole.Values.SelectMany(g => g).Should().OnlyContain(p => Global.Defaults.Permissions.All.ContainsKey(p.Name));
    }

    [Fact]
    public void ByRole_Should_KeepAuditEntriesReadOnly_When_AnyRoleIsSeeded()
    {
        RolePermissions.ByRole.Values.SelectMany(g => g)
            .Where(p => p.Entity == "AuditEntry")
            .Should().OnlyContain(p => p.Action == PermissionAction.View || p.Action == PermissionAction.Search,
                "audit rows can never be created, edited or deleted through an API (ARV-011)");
    }

    [Theory]
    [InlineData(RoleCodes.BorderShiftSupervisor)]
    [InlineData(RoleCodes.TerminalDutyManager)]
    [InlineData(RoleCodes.HandlerStationManager)]
    public void ByRole_Should_GrantNoUserRoleOrIntegrationAdministration_When_RoleIsOperational(string role)
    {
        RolePermissions.ByRole[role].Should().NotContain(p => p.Entity == "User" || p.Entity == "Role" || p.Entity == "IntegrationClient",
            "only system administrators manage users, roles and integration clients (wiki 01)");
    }

    [Fact]
    public void ByRole_Should_KeepHandlerOutOfZoneChanges_When_Seeded()
    {
        RolePermissions.HandlerStationManager.Should().NotContain(p => p.Entity == "ZoneProfile" && p.Action != PermissionAction.View && p.Action != PermissionAction.Search);
        RolePermissions.HandlerStationManager.Should().NotContain(p => p.Entity == "Device");
    }

    [Fact]
    public void For_Should_UniteRolesAndIgnoreUnknownCodes_When_UserHasSeveralRoles()
    {
        var granted = RolePermissions.For([RoleCodes.HandlerStationManager, "NotARole", RoleCodes.SystemAdministrator]);

        granted.Should().Contain(Global.Defaults.Permissions.CreateUser).And.Contain(Global.Defaults.Permissions.ViewDesk);
        RolePermissions.For(["NotARole"]).Should().BeEmpty();
    }

    #endregion

    #region Policies

    [Theory]
    [InlineData(RoleCodes.TerminalDutyManager, nameof(Global.Defaults.Permissions.PublishZoneProfile), true)]
    [InlineData(RoleCodes.HandlerStationManager, nameof(Global.Defaults.Permissions.PublishZoneProfile), false)]
    [InlineData(RoleCodes.SystemAdministrator, nameof(Global.Defaults.Permissions.ViewSystemInfo), true)]
    [InlineData(RoleCodes.BorderShiftSupervisor, nameof(Global.Defaults.Permissions.ViewSystemInfo), false)]
    public async Task AuthorizeAsync_Should_FollowRoleSeed_When_PolicyIsAPermission(string role, string permission, bool expected)
    {
        var user = User(role);

        var result = await Authorize(user, PermissionPolicyProvider.PolicyFor([permission]));

        result.Should().Be(expected);
    }

    [Fact]
    public async Task AuthorizeAsync_Should_SucceedOnAnyPermission_When_SeveralAreNamed()
    {
        var policy = PermissionPolicyProvider.PolicyFor([nameof(Global.Defaults.Permissions.CreateUser), nameof(Global.Defaults.Permissions.ViewDesk)]);

        (await Authorize(User(RoleCodes.HandlerStationManager), policy)).Should().BeTrue("OR semantics, as in AMAN");
    }

    [Fact]
    public async Task AuthorizeAsync_Should_Deny_When_UserIsAnonymousOrPermissionIsUnknown()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        (await Authorize(anonymous, PermissionPolicyProvider.PolicyFor([nameof(Global.Defaults.Permissions.ViewDesk)]))).Should().BeFalse();
        (await Authorize(User(RoleCodes.SystemAdministrator), PermissionPolicyProvider.PolicyFor(["ViewEverything"]))).Should().BeFalse("unknown names fail closed");
    }

    [Fact]
    public void PermissionAttribute_Should_NameOnlyCataloguedPermissions_When_UsedOnAnyController()
    {
        var hostAssemblies = new[]
        {
            typeof(Ariva.Api.Main._IAssemblyMark).Assembly,
            typeof(Ariva.Api.Ingest._IAssemblyMark).Assembly,
            typeof(Ariva.Api.Stream._IAssemblyMark).Assembly,
            typeof(Ariva.Api.Cronz._IAssemblyMark).Assembly,
            typeof(Ariva.Api.Integration._IAssemblyMark).Assembly
        };

        var attributes = hostAssemblies
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => new MemberInfo[] { type }.Concat(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)))
            .SelectMany(member => member.GetCustomAttributes<PermissionAttribute>().Select(attribute => (member, attribute)))
            .ToList();

        attributes.Should().NotBeEmpty("SystemInfo is the first [Permission] endpoint");
        attributes.SelectMany(a => a.attribute.PermissionNames.Select(name => (a.member, name)))
            .Where(x => Global.Defaults.Permissions.Find(x.name) is null)
            .Select(x => $"{x.member.DeclaringType?.Name ?? x.member.Name}.{x.member.Name}: {x.name}")
            .Should().BeEmpty();
    }

    #endregion

    #region Helpers

    private static ClaimsPrincipal User(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "tester"), new Claim(ClaimTypes.Role, role)], "Test"));

    private static async Task<bool> Authorize(ClaimsPrincipal user, string policyName)
    {
        var provider = new PermissionPolicyProvider(Options.Create(new AuthorizationOptions()), NullLogger<PermissionPolicyProvider>.Instance);
        var policy = await provider.GetPolicyAsync(policyName);
        var context = new AuthorizationHandlerContext(policy.Requirements, user, resource: null);

        foreach (var handler in new IAuthorizationHandler[]
                 {
                     new PermissionAuthorizationHandler(new RoleClaimPermissionResolver()),
                     new Microsoft.AspNetCore.Authorization.Infrastructure.PassThroughAuthorizationHandler()
                 })
        {
            await handler.HandleAsync(context);
        }

        return context.HasSucceeded;
    }

    #endregion
}
