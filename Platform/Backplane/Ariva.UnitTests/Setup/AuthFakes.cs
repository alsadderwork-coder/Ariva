using Ariva.Core;
using Ariva.Core.Domain.Criteria;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Administration;
using Ariva.Core.Services.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.UnitTests.Setup;

/// <summary>
/// Session check for in-process hosts, which have no database: every session is active unless a test marks it
/// otherwise.
/// </summary>
public sealed class FakeSessionValidator : ISessionValidator
{
    private readonly Dictionary<Guid, SessionState> _states = [];

    public SessionState this[Guid sessionId]
    {
        set => _states[sessionId] = value;
    }

    public Task<SessionState> CheckAsync(Guid sessionId, CancellationToken ct = default) =>
        Task.FromResult(_states.TryGetValue(sessionId, out var state) ? state : SessionState.Active);
}

/// <summary>
/// Sign-in service for in-process hosts. By default every call fails the way the real service fails for the
/// permission matrix bodies (unknown user, wrong current password, unknown id); a test can make sign-in and refresh
/// succeed with a fixed refresh token.
/// </summary>
public sealed class FakeAuthenticator : ISvcAuthenticator
{
    public SignInResult NextSignIn { get; set; }

    /// <summary>The error a failed sign-in returns (InvalidCredentials unless a test sets MfaRequired).</summary>
    public string LoginError { get; set; } = ISvcAuthenticator.InvalidCredentials;

    public List<SignInContext> Contexts { get; } = [];

    public Task<Fluentx.Result<SignInResult>> LoginAsync(LoginRequest request, SignInContext context, CancellationToken ct = default) =>
        Answer(context, LoginError);

    public Task<Fluentx.Result<SignInResult>> RefreshAsync(SignInContext context, CancellationToken ct = default) =>
        Answer(context, ISvcAuthenticator.SessionExpired);

    public Task<Fluentx.Result<bool>> LogoutAsync(SignInContext context, CancellationToken ct = default)
    {
        Contexts.Add(context);
        return Task.FromResult(new Fluentx.Result<bool>(true));
    }

    public Task<Fluentx.Result<TokenViewModel>> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default) =>
        Task.FromResult(Fluentx.Result.Error<TokenViewModel>(ISvcAuthenticator.InvalidCredentials));

    public Task<Fluentx.Result<bool>> UnlockAsync(Guid userId, CancellationToken ct = default) => NotFound();

    public Task<Fluentx.Result<bool>> DisableAsync(Guid userId, CancellationToken ct = default) => NotFound();

    public Task<Fluentx.Result<bool>> EnableAsync(Guid userId, CancellationToken ct = default) => NotFound();

    /// <summary>Enrolment succeeds for any caller, as it does for a real account without TOTP.</summary>
    public Task<Fluentx.Result<TotpEnrolmentViewModel>> EnrolTotpAsync(CancellationToken ct = default) =>
        Task.FromResult(new Fluentx.Result<TotpEnrolmentViewModel>(new TotpEnrolmentViewModel("MZXW6YTBOI", "otpauth://totp/Ariva:test?secret=MZXW6YTBOI")));

    public Task<Fluentx.Result<TotpConfirmedViewModel>> ConfirmTotpAsync(TotpCodeRequest request, CancellationToken ct = default) =>
        Task.FromResult(Fluentx.Result.Error<TotpConfirmedViewModel>(ISvcAuthenticator.InvalidCode));

    public Task<Fluentx.Result<TokenViewModel>> StepUpAsync(StepUpRequest request, CancellationToken ct = default) =>
        Task.FromResult(Fluentx.Result.Error<TokenViewModel>(ISvcAuthenticator.InvalidCode));

    public Task<Fluentx.Result<RecoveryCodesViewModel>> RegenerateRecoveryCodesAsync(TotpCodeRequest request, CancellationToken ct = default) =>
        Task.FromResult(Fluentx.Result.Error<RecoveryCodesViewModel>(ISvcAuthenticator.InvalidCode));

    private Task<Fluentx.Result<SignInResult>> Answer(SignInContext context, string error)
    {
        Contexts.Add(context);
        return Task.FromResult(NextSignIn is null ? Fluentx.Result.Error<SignInResult>(error) : new Fluentx.Result<SignInResult>(NextSignIn));
    }

    private static Task<Fluentx.Result<bool>> NotFound() => Task.FromResult(Fluentx.Result.Error<bool>(ISvcAuthenticator.UserNotFound));
}

/// <summary>
/// User, role and audit administration for in-process hosts, which have no database: unknown ids are not found, searches
/// are empty and the role list is the real one, as the real services answer the permission matrix calls.
/// </summary>
public sealed class FakeAdministration : ISvcUsers, ISvcRoleAssignment, ISvcAuditEntries, ISvcSites
{
    public static void Register(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Scoped<ISvcUsers, FakeAdministration>());
        services.Replace(ServiceDescriptor.Scoped<ISvcRoleAssignment, FakeAdministration>());
        services.Replace(ServiceDescriptor.Scoped<ISvcAuditEntries, FakeAdministration>());
        services.Replace(ServiceDescriptor.Scoped<ISvcSites, FakeAdministration>());
    }

    public Task<Fluentx.Result<UserCreatedViewModel>> CreateAsync(CreateUserRequest request, CancellationToken ct = default) =>
        Task.FromResult(Fluentx.Result.Error<UserCreatedViewModel>(AdministrationErrors.UserNameTaken));

    public Task<Fluentx.Result<UserViewModel>> GetAsync(Guid id, CancellationToken ct = default) => NotFound<UserViewModel>();

    public Task<Fluentx.Result<PageViewModel<UserViewModel>>> SearchAsync(UserCriteria criteria, CancellationToken ct = default) =>
        Task.FromResult(new Fluentx.Result<PageViewModel<UserViewModel>>(new PageViewModel<UserViewModel>([], 0, 1, 50)));

    public Task<Fluentx.Result<UserViewModel>> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default) => NotFound<UserViewModel>();

    public Task<Fluentx.Result<TemporaryPasswordViewModel>> ResetPasswordAsync(Guid id, CancellationToken ct = default) => NotFound<TemporaryPasswordViewModel>();

    public Task<Fluentx.Result<bool>> ResetTotpAsync(Guid id, CancellationToken ct = default) => NotFound<bool>();

    public Task<Fluentx.Result<UserViewModel>> SetSitesAsync(Guid id, SiteAccessRequest request, CancellationToken ct = default) => NotFound<UserViewModel>();

    public Task<Fluentx.Result<IReadOnlyList<SiteViewModel>>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(new Fluentx.Result<IReadOnlyList<SiteViewModel>>(Array.Empty<SiteViewModel>()));

    Task<Fluentx.Result<SiteViewModel>> ISvcSites.GetAsync(string code, CancellationToken ct) => NotFound<SiteViewModel>();

    public Task<Fluentx.Result<SiteViewModel>> CreateAsync(CreateSiteRequest request, CancellationToken ct = default) =>
        Task.FromResult(Fluentx.Result.Error<SiteViewModel>(AdministrationErrors.SiteTaken));

    public Task<Fluentx.Result<SiteViewModel>> UpdateAsync(string code, UpdateSiteRequest request, CancellationToken ct = default) => NotFound<SiteViewModel>();

    public IReadOnlyList<RoleViewModel> Roles() =>
        RoleCodes.All.Select(code => new RoleViewModel(code, RoleHierarchy.Rank(code), [.. RolePermissions.ByRole[code].Select(p => p.ToString())])).ToList();

    public Task<Fluentx.Result<UserViewModel>> GrantAsync(Guid userId, string roleCode, CancellationToken ct = default) => NotFound<UserViewModel>();

    public Task<Fluentx.Result<UserViewModel>> RevokeAsync(Guid userId, string roleCode, CancellationToken ct = default) => NotFound<UserViewModel>();

    Task<Fluentx.Result<AuditEntryViewModel>> ISvcAuditEntries.GetAsync(Guid id, CancellationToken ct) => NotFound<AuditEntryViewModel>();

    public Task<Fluentx.Result<PageViewModel<AuditEntryViewModel>>> SearchAsync(AuditEntryCriteria criteria, CancellationToken ct = default) =>
        Task.FromResult(new Fluentx.Result<PageViewModel<AuditEntryViewModel>>(new PageViewModel<AuditEntryViewModel>([], 0, 1, 50)));

    private static Task<Fluentx.Result<T>> NotFound<T>() => Task.FromResult(Fluentx.Result.Error<T>(AdministrationErrors.NotFound));
}

/// <summary>Topology for in-process hosts (no database): searches are empty and every id is unknown, as for the matrix calls.</summary>
public sealed class FakeTopology : Ariva.Core.Services.Topology.ISvcTopology, Ariva.Core.Services.Topology.ISvcDeskCodeMappings, Ariva.Core.Services.Topology.ISvcFloorPlans, Ariva.Core.Services.Topology.ISvcZoneProfiles
{
    public static void Register(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Scoped<Ariva.Core.Services.Topology.ISvcTopology, FakeTopology>());
        services.Replace(ServiceDescriptor.Scoped<Ariva.Core.Services.Topology.ISvcDeskCodeMappings, FakeTopology>());
        services.Replace(ServiceDescriptor.Scoped<Ariva.Core.Services.Topology.ISvcFloorPlans, FakeTopology>());
        services.Replace(ServiceDescriptor.Scoped<Ariva.Core.Services.Topology.ISvcZoneProfiles, FakeTopology>());
    }

    // Zone profiles (ARV-017): no site and no profile exists, as for the matrix calls.
    public Task<Fluentx.Result<IReadOnlyList<ZoneProfileSummaryViewModel>>> HistoryAsync(string siteCode, CancellationToken ct = default) => Missing<IReadOnlyList<ZoneProfileSummaryViewModel>>();
    Task<Fluentx.Result<ZoneProfileViewModel>> Ariva.Core.Services.Topology.ISvcZoneProfiles.GetAsync(Guid id, CancellationToken ct) => Missing<ZoneProfileViewModel>();
    public Task<Fluentx.Result<ZoneProfileViewModel>> CreateDraftAsync(CreateZoneProfileDraftRequest request, CancellationToken ct = default) =>
        Task.FromResult(Fluentx.Result.Error<ZoneProfileViewModel>(Ariva.Core.Services.Topology.TopologyErrors.UnknownSite));
    public Task<Fluentx.Result<ZoneProfileViewModel>> RenameAsync(Guid id, RenameZoneProfileRequest request, CancellationToken ct = default) => Missing<ZoneProfileViewModel>();
    public Task<Fluentx.Result<ZoneViewModel>> AddZoneAsync(Guid id, AddZoneRequest request, CancellationToken ct = default) => Missing<ZoneViewModel>();
    public Task<Fluentx.Result<ZoneViewModel>> UpdateZoneAsync(Guid id, Guid zoneId, UpdateZoneRequest request, CancellationToken ct = default) => Missing<ZoneViewModel>();
    public Task<Fluentx.Result<bool>> RemoveZoneAsync(Guid id, Guid zoneId, CancellationToken ct = default) => Missing<bool>();
    public Task<Fluentx.Result<LineViewModel>> AddLineAsync(Guid id, AddLineRequest request, CancellationToken ct = default) => Missing<LineViewModel>();
    public Task<Fluentx.Result<bool>> RemoveLineAsync(Guid id, Guid lineId, CancellationToken ct = default) => Missing<bool>();
    public Task<Fluentx.Result<ZoneProfileValidationViewModel>> ValidateAsync(Guid id, CancellationToken ct = default) => Missing<ZoneProfileValidationViewModel>();
    public Task<Fluentx.Result<ZoneProfileSummaryViewModel>> PublishAsync(Guid id, PublishZoneProfileRequest request, CancellationToken ct = default) => Missing<ZoneProfileSummaryViewModel>();
    public Task<Fluentx.Result<bool>> DiscardAsync(Guid id, CancellationToken ct = default) => Missing<bool>();

    Task<Fluentx.Result<FloorPlanViewModel>> Ariva.Core.Services.Topology.ISvcFloorPlans.GetAsync(Guid levelId, CancellationToken ct) => Missing<FloorPlanViewModel>();
    public Task<Fluentx.Result<FloorPlanContent>> OpenAsync(Guid levelId, CancellationToken ct = default) => Missing<FloorPlanContent>();
    public Task<Fluentx.Result<FloorPlanViewModel>> UploadAsync(Guid levelId, FloorPlanUpload upload, CancellationToken ct = default) => Missing<FloorPlanViewModel>();
    public Task<Fluentx.Result<FloorPlanViewModel>> CalibrateAsync(Guid levelId, CalibrateFloorPlanRequest request, CancellationToken ct = default) => Missing<FloorPlanViewModel>();
    Task<Fluentx.Result<bool>> Ariva.Core.Services.Topology.ISvcFloorPlans.DeleteAsync(Guid levelId, CancellationToken ct) => Missing<bool>();

    public Task<Fluentx.Result<PageViewModel<DeskCodeMappingViewModel>>> SearchAsync(DeskCodeMappingCriteria criteria, CancellationToken ct = default) => Empty<DeskCodeMappingViewModel>();
    public Task<Fluentx.Result<DeskCodeMappingViewModel>> GetAsync(Guid id, CancellationToken ct = default) => Missing<DeskCodeMappingViewModel>();
    public Task<Fluentx.Result<DeskCodeMappingViewModel>> CreateAsync(CreateDeskCodeMappingRequest request, CancellationToken ct = default) => Missing<DeskCodeMappingViewModel>();
    public Task<Fluentx.Result<DeskCodeMappingViewModel>> UpdateAsync(Guid id, UpdateDeskCodeMappingRequest request, CancellationToken ct = default) => Missing<DeskCodeMappingViewModel>();
    public Task<Fluentx.Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default) => Missing<bool>();
    public Task<Guid?> ResolveAsync(Ariva.Core.Domain.Enums.ExternalSystem system, string siteCode, string externalCode, CancellationToken ct = default) => Task.FromResult<Guid?>(null);

    private static Task<Fluentx.Result<PageViewModel<T>>> Empty<T>() =>
        Task.FromResult(new Fluentx.Result<PageViewModel<T>>(new PageViewModel<T>([], 0, 1, 50)));

    private static Task<Fluentx.Result<T>> Missing<T>() =>
        Task.FromResult(Fluentx.Result.Error<T>(Ariva.Core.Services.Topology.TopologyErrors.NotFound));

    public Task<Fluentx.Result<PageViewModel<AirportViewModel>>> SearchAirportsAsync(TopologyCriteria criteria, CancellationToken ct = default) => Empty<AirportViewModel>();
    public Task<Fluentx.Result<AirportViewModel>> GetAirportAsync(Guid id, CancellationToken ct = default) => Missing<AirportViewModel>();
    public Task<Fluentx.Result<AirportViewModel>> CreateAirportAsync(CreateAirportRequest request, CancellationToken ct = default) => Missing<AirportViewModel>();
    public Task<Fluentx.Result<AirportViewModel>> UpdateAirportAsync(Guid id, UpdateAirportRequest request, CancellationToken ct = default) => Missing<AirportViewModel>();
    public Task<Fluentx.Result<bool>> DeleteAirportAsync(Guid id, CancellationToken ct = default) => Missing<bool>();
    public Task<Fluentx.Result<PageViewModel<TerminalViewModel>>> SearchTerminalsAsync(TopologyCriteria criteria, CancellationToken ct = default) => Empty<TerminalViewModel>();
    public Task<Fluentx.Result<TerminalViewModel>> GetTerminalAsync(Guid id, CancellationToken ct = default) => Missing<TerminalViewModel>();
    public Task<Fluentx.Result<TerminalViewModel>> CreateTerminalAsync(CreateTerminalRequest request, CancellationToken ct = default) => Missing<TerminalViewModel>();
    public Task<Fluentx.Result<TerminalViewModel>> UpdateTerminalAsync(Guid id, UpdateTerminalRequest request, CancellationToken ct = default) => Missing<TerminalViewModel>();
    public Task<Fluentx.Result<bool>> DeleteTerminalAsync(Guid id, CancellationToken ct = default) => Missing<bool>();
    public Task<Fluentx.Result<PageViewModel<LevelViewModel>>> SearchLevelsAsync(TopologyCriteria criteria, CancellationToken ct = default) => Empty<LevelViewModel>();
    public Task<Fluentx.Result<LevelViewModel>> GetLevelAsync(Guid id, CancellationToken ct = default) => Missing<LevelViewModel>();
    public Task<Fluentx.Result<LevelViewModel>> CreateLevelAsync(CreateLevelRequest request, CancellationToken ct = default) => Missing<LevelViewModel>();
    public Task<Fluentx.Result<LevelViewModel>> UpdateLevelAsync(Guid id, UpdateLevelRequest request, CancellationToken ct = default) => Missing<LevelViewModel>();
    public Task<Fluentx.Result<bool>> DeleteLevelAsync(Guid id, CancellationToken ct = default) => Missing<bool>();
    public Task<Fluentx.Result<PageViewModel<CheckpointViewModel>>> SearchCheckpointsAsync(TopologyCriteria criteria, CancellationToken ct = default) => Empty<CheckpointViewModel>();
    public Task<Fluentx.Result<CheckpointViewModel>> GetCheckpointAsync(Guid id, CancellationToken ct = default) => Missing<CheckpointViewModel>();
    public Task<Fluentx.Result<CheckpointViewModel>> CreateCheckpointAsync(CreateCheckpointRequest request, CancellationToken ct = default) => Missing<CheckpointViewModel>();
    public Task<Fluentx.Result<CheckpointViewModel>> UpdateCheckpointAsync(Guid id, UpdateCheckpointRequest request, CancellationToken ct = default) => Missing<CheckpointViewModel>();
    public Task<Fluentx.Result<bool>> DeleteCheckpointAsync(Guid id, CancellationToken ct = default) => Missing<bool>();
    public Task<Fluentx.Result<PageViewModel<DeskViewModel>>> SearchDesksAsync(TopologyCriteria criteria, CancellationToken ct = default) => Empty<DeskViewModel>();
    public Task<Fluentx.Result<DeskViewModel>> GetDeskAsync(Guid id, CancellationToken ct = default) => Missing<DeskViewModel>();
    public Task<Fluentx.Result<DeskViewModel>> CreateDeskAsync(CreateDeskRequest request, CancellationToken ct = default) => Missing<DeskViewModel>();
    public Task<Fluentx.Result<DeskViewModel>> UpdateDeskAsync(Guid id, UpdateDeskRequest request, CancellationToken ct = default) => Missing<DeskViewModel>();
    public Task<Fluentx.Result<IReadOnlyList<DeskViewModel>>> CreateDeskRangeAsync(CreateDeskRangeRequest request, CancellationToken ct = default) => Missing<IReadOnlyList<DeskViewModel>>();
    public Task<Fluentx.Result<bool>> DeleteDeskAsync(Guid id, CancellationToken ct = default) => Missing<bool>();
}

/// <summary>The device registry for in-process hosts (no database): searches are empty and every id is unknown (ARV-021).</summary>
public sealed class FakeDevices : Ariva.Core.Services.Sensing.ISvcDevices
{
    public static void Register(IServiceCollection services) =>
        services.Replace(ServiceDescriptor.Scoped<Ariva.Core.Services.Sensing.ISvcDevices, FakeDevices>());

    public Task<Fluentx.Result<PageViewModel<DeviceViewModel>>> SearchAsync(DeviceCriteria criteria, CancellationToken ct = default) =>
        Task.FromResult(new Fluentx.Result<PageViewModel<DeviceViewModel>>(new PageViewModel<DeviceViewModel>([], 0, 1, 50)));

    public Task<Fluentx.Result<DeviceViewModel>> GetAsync(Guid id, CancellationToken ct = default) => Missing<DeviceViewModel>();
    public Task<Fluentx.Result<DeviceCredentialViewModel>> RegisterAsync(RegisterDeviceRequest request, CancellationToken ct = default) => Missing<DeviceCredentialViewModel>();
    public Task<Fluentx.Result<DeviceViewModel>> UpdateAsync(Guid id, UpdateDeviceRequest request, CancellationToken ct = default) => Missing<DeviceViewModel>();
    public Task<Fluentx.Result<DeviceViewModel>> MoveAsync(Guid id, DevicePlacement request, CancellationToken ct = default) => Missing<DeviceViewModel>();
    public Task<Fluentx.Result<DeviceCredentialViewModel>> RotateCredentialAsync(Guid id, CancellationToken ct = default) => Missing<DeviceCredentialViewModel>();
    public Task<Fluentx.Result<IReadOnlyList<CalibrationViewModel>>> CalibrationsAsync(Guid id, CancellationToken ct = default) => Missing<IReadOnlyList<CalibrationViewModel>>();
    public Task<Fluentx.Result<CalibrationViewModel>> RecordCalibrationAsync(Guid id, RecordCalibrationRequest request, CancellationToken ct = default) => Missing<CalibrationViewModel>();
    public Task<Fluentx.Result<DeviceViewModel>> RetireAsync(Guid id, CancellationToken ct = default) => Missing<DeviceViewModel>();
    public Task<Fluentx.Result<bool>> RemoveAsync(Guid id, CancellationToken ct = default) => Missing<bool>();

    /// <summary>Like the real service: an unknown family is refused before anything else.</summary>
    public Fluentx.Result<FootprintViewModel> AssumedFootprint(string family, double mountingHeightMetres) =>
        Enum.TryParse<Ariva.Core.Domain.Enums.DeviceFamily>(family, out var parsed) && family is { Length: > 0 } && char.IsLetter(family[0])
            ? new Fluentx.Result<FootprintViewModel>(Footprint(Ariva.Core.Domain.Components.CoverageFootprint.Assumed(parsed, Math.Max(2, mountingHeightMetres))))
            : Fluentx.Result.Error<FootprintViewModel>(Ariva.Core.Services.Sensing.DeviceErrors.UnknownFamily);

    private static FootprintViewModel Footprint(Ariva.Core.Domain.Components.CoverageFootprint f) =>
        new(f.LengthMetres, f.WidthMetres, f.RadiusMetres, f.Source.ToString(), f.Text, f.Note);

    private static Task<Fluentx.Result<T>> Missing<T>() =>
        Task.FromResult(Fluentx.Result.Error<T>(Ariva.Core.Services.Topology.TopologyErrors.NotFound));
}
