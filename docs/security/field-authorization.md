# Field-level authorization

Who may read and who may write each field of what Ariva's APIs answer and take, per entity and role, where the server enforces each rule, and the test that proves it. For a site's security officer, for the reviewer of a change that adds a field or a role, and for engineers who add an endpoint.

Status: this page describes the code as of 2026-10-19 (ARV-095). It changes no control. Where the code does not settle a rule it says To confirm; section 5 lists the gaps and the rules without a test.

## 1. Purpose and how to read it

This page serves two ASVS 5.0 rows of [asvs-l2](asvs-l2.md):
- V8.1.2, document field-level read and write rules: sections 2 and 3.
- V8.2.3, field-level access restricted to prevent BOPLA: sections 3 and 4 name the code that enforces each rule and its test.

How it relates to the other pages:
- Function-level rules (which role calls which endpoint) are in `security/permission-matrix.json`. Unit `PermissionMatrixTests.PermissionRows_Should_AgreeWithRoleSeed_When_MatrixIsRead` holds it to the role seed, and unit `PermissionMatrixTests.Send_Should_ReturnMatrixStatus_When_EachCallerCallsEachEndpoint` and e2e `api/permission-matrix.spec.ts` call every endpoint as every role. This page adds the rules inside one endpoint's answer or request.
- Data-specific rules (asvs-l2 V8.2.2): every site-bound read and write goes through `ISiteScope` (`Ariva.Infra/Security/SiteScope.cs`), and a record outside the caller's sites answers 404 like one that does not exist (unit and integration `SiteScopeTests`). They apply to every row below and are not repeated.
- The protection level of each value (L1 to L4) is in [data classification](data-classification.md); what never enters Ariva is in the [data boundary](../domain/data-boundary.md).

How to read a section of section 3:
- Its first line lists the view models of `Ariva.Core/Domain/ViewModels` it covers, in a fixed form a completeness test reads. Its second line lists the answer types it covers that live elsewhere, mostly beside their service interface in `Ariva.Core/Services`.
- "All fields" names the permissions that read and write the resource. The table lists only the fields whose access differs from that.
- "Written by" is the permission whose request model carries the field, "server" when only Ariva sets it, or "never" when nothing changes it after creation. A field that no request model carries cannot be written through the API: controllers bind request models, never entities (unit `EntityBindingTests.GetParameters_Should_ContainNoDomainEntity_When_ActionIsOnAnyArivaHost`), and request JSON is read strictly, so a member the model does not define answers 400 (`Ariva.Api.Common/Filters/StrictJson.cs`; unit `StrictInputTests.InputFormatters_Should_ReadStrictlyExceptForTheFrozenEndpoints_When_HostHasControllers`, e2e `api/strict-input.spec.ts`). Exception: the sign-in and the other authentication endpoints and the integration token exchange keep their earlier parsing until ARV-089a (login freeze; `Ariva.Api.Common/Filters/LenientInput.cs`): an unknown member is skipped, so it still writes nothing.
- Permissions are written `Entity.Action`, as `Ariva.Core/Global.cs` names them. A caller's permissions come from its stored role codes through the role seed on every request (`StoredPermissionResolver`, `Ariva.Infra/Security/StoredPermissionResolver.cs`), and every field rule below reads the same stored roles (`CallerRoles`, `Ariva.Infra/Services/Administration/CallerRoles.cs`, then `RolePermissions.For`), never the token, which carries no role.
- Unit tests are in `Platform/Backplane/Ariva.UnitTests`, integration tests in `Platform/Backplane/Ariva.IntegrationTests`, end-to-end specs in `Platform/Testing/Ariva.E2E/tests`.

## 2. Roles

From the seed, `Ariva.Core/Security/RolePermissions.cs`. The abbreviations are used in the tables of section 3.

| Role (code) | Short | Deployment | Holds |
|---|---|---|---|
| Border shift supervisor (`BorderShiftSupervisor`) | BSS | Border | Topology read; zone profiles and devices (create, change, publish; not delete); `LiveQueue.View`; `ArrivalWave.View` and `ArrivalWaveLanes.View`; `BorderDesks.View`; `Immigration.View`; alert rules (all five) and alerts; displays, instruction slides and `AdvertisingPolicy.View`; reports and schedules; `DataQuality.View`; `Validation.View` and `Validation.Manage` |
| Terminal duty manager (`TerminalDutyManager`) | TDM | Airport | As BSS without `ArrivalWaveLanes.View` and `BorderDesks.View`; with `DeskCodeMapping.View` and `.Search`, `AirportDesks.View` and advertising (`Advertising.View`, `.Manage`, `.Approve`) |
| Handler station manager (`HandlerStationManager`) | HSM | Airport | Topology read; `ZoneProfile.View` and `.Search`; `LiveQueue.View`; `AlertRule.View` and `.Search`; alerts |
| System administrator (`SystemAdministrator`) | SA | Either | Every permission except creating, changing or deleting audit entries and `Validation.Capture` (`NotForAdministrators`) |
| Validation observer (`ValidationObserver`) | VO | Either | `Validation.Capture` only |

Rules that hold for every role:
- Topology read is `Site`, `Airport`, `Terminal`, `Level`, `Checkpoint`, `Desk` and `FloorPlan`, View and Search (`RolePermissions.TopologyRead`). Creating, changing and deleting topology is SA only.
- A pending account (temporary password or TOTP not enrolled) and a disabled one hold no permission (`StoredPermissionResolver`).
- Grants: an administrator grants or revokes a role only when one of its roles ranks at least as high, never on its own account (`RoleHierarchy.CanAssign`, `Ariva.Core/Security/RoleHierarchy.cs`; integration `AdministrationTests.Grant_Should_RefuseSelfAndAboveOwnRank_When_RulesAreBroken`, e2e `api/administration.spec.ts`, "privilege escalation attempts return 403").
- Which roles are valid in which deployment is an administration rule ([wiki 11](../../wiki/11-Administration-Guide.md), section 2), not a code rule: a deployment holds whatever role codes its administrators grant.
- `[RequiresRecentMfa]` marks the critical actions (`security/critical-actions.json`): such a write also needs a second factor in the last 15 minutes. The tables say "with a second factor" for them.

## 3. Entities and resources

### 3.1 Accounts and sessions

View models: `TokenViewModel`, `CurrentUserViewModel`, `TotpEnrolmentViewModel`, `TotpConfirmedViewModel`, `RecoveryCodesViewModel`, `UserViewModel`, `UserCreatedViewModel`, `TemporaryPasswordViewModel`, `RoleViewModel`, `AuditEntryViewModel`
Other answer types: `SignInResult` (never sent whole: the controller writes its refresh token into the cookie only).

All fields of `UserViewModel`: read with `User.View` or `User.Search` (SA), written with `User.Create` or `User.Edit` (SA) through `Ariva.Api.Main/Controllers/AdminArea/Users/Controller.cs`. Which accounts a caller reads at all: `AdministrationGuards.Administrable` and `SvcUsers.VisibleAsync`; the break-glass account and an account that reaches beyond a site-limited administrator's sites answer 404. `RoleViewModel`: read with `Role.Search` (SA); no endpoint writes a role's permissions (they are the seed). `AuditEntryViewModel`: read with `AuditEntry.View` or `AuditEntry.Search` (SA); written by nobody through the API.

| Field or field group | Read by | Written by | Rule and where enforced | Test |
|---|---|---|---|---|
| `UserViewModel.UserName` | SA | `User.Create` with a second factor, once; never changed | `CreateUserRequest` carries it, `UpdateUserRequest` does not (`Ariva.Core/Domain/InputModels/UserRequests.cs`) | Integration `AdministrationTests.Create_Should_IssueAOneTimeTemporaryPasswordAndAudit_When_AdministratorCreatesAUser` |
| `UserViewModel.DisplayName`, `Email` | SA | `User.Edit`, no second factor | `SvcUsers.UpdateAsync` checks both before the entity changes | Integration `AdministrationTests.Names_Should_BeCleanTextAndQuotedInTheAudit_When_AnAdministratorSetsThem` |
| `UserViewModel.Roles` | SA | `User.Edit` with a second factor (`PUT` and `DELETE .../roles/{roleCode}`) | Rank rule and no self-grant (`RoleHierarchy`, `SvcRoleAssignment`); a revoke ends the account's sessions | Integration `AdministrationTests.Grant_Should_RefuseSelfAndAboveOwnRank_When_RulesAreBroken`, `AdministrationTests.Revoke_Should_EndTheUsersSessionsAndAudit_When_RoleIsRemoved` |
| `UserViewModel.AllSites`, `Sites` | SA | `User.Create` or `User.Edit` with a second factor (`PUT .../sites`), never on one's own account | `SvcUsers.SetSitesAsync`: a site-limited administrator gives only its own sites and never every site; a validation observer's only role never gets every site | Integration `SiteScopeTests.SetSites_Should_RefuseUnknownSitesSelfChangesAndWiderAccess_When_RulesAreBroken`, `SiteScopeTests.SiteAccess_Should_NeverBeEverySite_When_TheAccountsOnlyRoleIsValidationObserver` |
| `UserViewModel.IsDisabled`, `IsLocked` | SA | `User.Edit` (disable, unlock); a disable is refused on one's own account, an unlock is not (`SvcAuthenticator.UnlockAsync`, a gap for ARV-095a); enabling again needs a second factor | Separate actions through `ISvcAuthenticator`; no request model carries the flags; a disable ends every session of the account | Integration `SessionTests.Disable_Should_EndEverySessionOfTheUser_When_AdministratorDisables`, `AuthenticatorTests.Unlock_Should_ClearTheLock_When_AdministratorUnlocks`; e2e `api/sessions.spec.ts` |
| `UserViewModel.MustChangePassword`, `TotpEnrolled` | SA | Server; reset by `User.Edit` with a second factor (`reset-password`, `reset-totp`), never on one's own account | `SvcUsers.ResetPasswordAsync`, `SvcUsers.ResetTotpAsync` end the account's sessions | Integration `AdministrationTests.Resets_Should_EndSessionsClearTotpAndAudit_When_AdministratorResets`; e2e `api/administration.spec.ts` (own TOTP reset 403) |
| `UserViewModel.LastLoginOn`, `CreatedOn`, `Id` | SA | Server | Not in any request model | |
| `UserCreatedViewModel.TemporaryPassword`, `TemporaryPasswordViewModel` | The administrator who created or reset the account, in that answer only | Server | Shown once, `no-store`; L4 ([data classification](data-classification.md), section 5.6) | Unit `DataClassificationTests.ViewModels_Should_CarryAnIssuedCredentialOnlyInTheAnswersThatHandItToItsHolder_When_Read` |
| `CurrentUserViewModel`, all fields | The signed-in account, its own only (`GET api/auth/me`, any authenticated caller, pending included) | Server | No id parameter: the controller reads the caller; `Permissions` is empty while the account is pending | E2e `api/auth-me.spec.ts` ("a full account sees its roles, permissions and sites, never a secret", "a pending account is told what its first sign-in needs, with no permissions") |
| `TokenViewModel`, `TotpEnrolmentViewModel`, `TotpConfirmedViewModel`, `RecoveryCodesViewModel` | The account signing in, enrolling or regenerating, in that answer only | Server | L4, `no-store`; the refresh token travels only in the `__Secure-ariva_rt` cookie (`SignInResult` never serialised whole) | Unit `SignInHostTests.Login_Should_SetTheRefreshCookieAndKeepItOutOfTheBody_When_SignInSucceeds`, `DataClassificationTests` |
| `AuditEntryViewModel`, which entries | SA with every site: every entry; a site-limited SA: entries about accounts it may administer and about its own sites only | Nobody (no write path; the runtime role cannot change an entry) | `SvcAuditEntries.VisibleAsync` | Integration `SiteScopeTests.SiteLimitedAdministrator_Should_NotSeeOrTakeOverWiderAccounts_When_ActingOnUsers`, `AdministrationTests.AuditEntry_Should_BeAppendOnly_When_TheRuntimeRoleTriesToChangeIt` |
| `AuditEntryViewModel.IpAddress`, `ActorName`, `BeforeSummary`, `AfterSummary` | As the entry | Server | L3; summaries may quote a name or an email address | Unit `AuditSummaryTests` |

### 3.2 Sites and topology

View models: `SiteViewModel`, `AirportViewModel`, `TerminalViewModel`, `LevelViewModel`, `CheckpointViewModel`, `FloorPlanViewModel`, `SiteConfigurationBundleViewModel`, `SiteConfigurationCountsViewModel`, `SiteConfigurationDeviceCredentialViewModel`, `SiteConfigurationExportViewModel`, `SiteConfigurationImportViewModel`
Other answer types: `SiteCalendarViewModel`, `OperatingWeekViewModel`, `OperatingExceptionViewModel`, `MaintenanceWindowViewModel` (`Ariva.Core/Services/Quality/ISvcSiteCalendar.cs`); `FloorPlanContent` (the plan's bytes).

All fields: read with the entity's View or Search (BSS, TDM, HSM, SA), written with its Create, Edit or Delete (SA only). The site calendar: read with `Site.View`, written with `Site.Edit` (`SiteCalendarController`). The site configuration bundle: `SiteConfiguration.Transfer` with a second factor (SA) for both export and import.

| Field or field group | Read by | Written by | Rule and where enforced | Test |
|---|---|---|---|---|
| Codes and parents: `SiteViewModel.Code`, `AirportViewModel.IataCode`, `TerminalViewModel.Code`, `AirportId`, `SiteCode`, `LevelViewModel.Code`, `TerminalId`, `CheckpointViewModel.Code`, `LevelId`, `Kind` | Topology read | `.Create` (SA), once; never changed | The create requests carry them, the update requests do not (`Ariva.Core/Domain/InputModels/UserRequests.cs`, `TopologyRequests.cs`) | E2e `api/topology.spec.ts`, `api/sites.spec.ts` |
| `SiteViewModel.Illustrative` | Topology read; also the display board | Never through the API: only the demo seed sets it, when it inserts the site | No request model carries it; the runtime role may update only `name` and the `modified_*` columns of `site` (script 0046) | E2e `api/illustrative-site.spec.ts` ("the admin API cannot set the flag on a new site", "cannot clear the flag of AUH-TA") |
| `FloorPlanViewModel` scale and origin (`MetresPerPixel`, `OriginX`, `OriginY`) | Topology read | `FloorPlan.Create` (upload) or `FloorPlan.Edit` (`PUT .../calibration`), SA | `CalibrateFloorPlanRequest` | E2e `api/floor-plans.spec.ts` |
| `FloorPlanViewModel.ContentType`, `SizeBytes`, `Sha256`, `WidthPixels`, `HeightPixels`, `OriginalFileName` | Topology read | Server, from the uploaded bytes; the type is detected, never taken from the request | `FloorPlanFiles.Inspect` ([wiki 13a](../../wiki/13a-File-Handling.md), section 3.1) | E2e `api/floor-plans.spec.ts` |
| Calendar reasons (`OperatingExceptionViewModel.Reason`, `MaintenanceWindowViewModel.Reason`) | `Site.View` holders: BSS, TDM, HSM, SA | `Site.Edit` (SA) | L3 free text, shown to every reader of the site; an entry changes only until it takes effect (`SiteCalendarRules`) | Unit `SiteCalendarRulesTests` |
| The bundle's parts (`SiteConfigurationBundleViewModel.Parts`) | SA with a second factor, at its own sites | SA with a second factor (import into an empty site) | `SiteConfigurationTransfer.Export` writes no credential, hash, prefix, access setting, pairing secret, escalation contact, user, audit entry or time series; an import refuses a part with a member that is never exported; a site holding a border-only alert rule is refused to a caller without `ArrivalWaveLanes.View` (`SvcSiteConfiguration`, `BorderAlerts.Receives`) | Unit `SiteConfigurationTransferTests.Export_Should_CarryDevicesRulesAndDisplaysWithoutCredentialsContactsOrAccessSettings_When_TheSiteHasThem`, `SiteConfigurationPartsTests.Read_Should_RefuseThePart_When_ARowCarriesAMemberThatIsNeverExported`; integration `SiteConfigurationTests.Import_Should_ApplyTheRuleServicesRoleCheck_When_TheCallerIsNotASystemAdministrator` |
| `SiteConfigurationDeviceCredentialViewModel.Credential` | The importing SA, in the import's answer only | Server | L4, `no-store` | E2e `api/site-configuration.spec.ts` |

### 3.3 Zones and profiles

View models: `ZoneProfileSummaryViewModel`, `ZoneViewModel`, `LineViewModel`, `ZoneProfileViewModel`, `ZoneProfileValidationViewModel`
Other answer types: none.

All fields: read with `ZoneProfile.View` or `.Search` (BSS, TDM, HSM, SA), written with `ZoneProfile.Create` and `.Edit` (BSS, TDM, SA) through `ZoneProfilesController`; only a draft changes (`ZoneProfile` entity).

| Field or field group | Read by | Written by | Rule and where enforced | Test |
|---|---|---|---|---|
| `ZoneProfileSummaryViewModel.Status`, `Version`, `PublishedBy`, `PublishedOn` | As the profile | `ZoneProfile.Publish` with a second factor (BSS, TDM, SA) | `POST .../publish`; `PublishedBy` is the caller's user name (L3) | E2e `api/zone-profiles.spec.ts` |
| Deleting a draft | | `ZoneProfile.Delete` (SA only: the seed's `ZonesAndDevices` leaves it out) | | Permission matrix rows |
| `ZoneProfileSummaryViewModel.CreatedBy`, `CreatedOn`, `RetiredOn`, `BasedOnVersion`, `GeometryHash`, `ZoneCount`, `LineCount`; `ZoneViewModel.AreaSquareMetres`; `LineViewModel.LengthMetres`; `ZoneProfileValidationViewModel` | As the profile | Server | Computed from the geometry or set by the audit interceptor | |
| `ZoneViewModel.DeskId`, `LaneCategory`, `PhysicalCapacity` | As the profile, HSM included | `ZoneProfile.Edit` | Configuration (L2): a desk link alone is not border data | |

### 3.4 Devices

View models: `FootprintViewModel`, `DeviceViewModel`, `DeviceMappingViewModel`, `DeviceZoneViewModel`, `DeviceSelfViewModel`, `DeviceCredentialViewModel`, `CalibrationViewModel`, `DeviceHealthViewModel`, `ZoneHealthViewModel`, `DeviceHealthOverviewViewModel`, `CsvRowErrorViewModel`, `DeviceImportRowViewModel`, `DeviceImportPreviewViewModel`, `ImportedDeviceCredentialViewModel`, `DeviceImportCommitViewModel`
Other answer types: none (`DeviceCredentialRecord` holds the credential hash and is never sent).

All fields: read with `Device.View` or `.Search` (BSS, TDM, SA; HSM has none), written with `Device.Create` and `.Edit` (BSS, TDM, SA) through `DevicesController` and `DeviceImportsController`.

| Field or field group | Read by | Written by | Rule and where enforced | Test |
|---|---|---|---|---|
| `DeviceViewModel.Code`, `SiteCode`, `Family` | Device readers | `Device.Create` with a second factor (registration or CSV import), once; never changed | `RegisterDeviceRequest` carries them, `UpdateDeviceRequest` does not (`Ariva.Core/Domain/InputModels/DeviceRequests.cs`); a placement on another site's level answers 404, so a device stays in its site | Integration `DeviceRegistryTests.Registry_Should_ShowNothingAndRegisterNothing_When_TheCallerHasNoSiteAccess`; e2e `api/devices.spec.ts` |
| `DeviceViewModel.Model`, `Transport`, `Dialect`, `ClockSource`, `MappingName` | Device readers | `Device.Edit`, no second factor | `UpdateDeviceRequest` | E2e `api/devices.spec.ts` |
| Placement (`LevelId`, `X`, `Y`, `MountingHeightMetres`, `OrientationDegrees`, `QueueZoneName`, `Footprint`) | Device readers | `Device.Edit` (`PUT .../placement`) | `DevicePlacement`; a footprint left out takes the assumed one, labelled in `FootprintViewModel.Note` | E2e `api/devices.spec.ts` |
| Access settings (`AllowedSources`, `ClientCertificateSha256`) | Device readers (BSS, TDM, SA) | `Device.Edit` with a second factor (`PUT .../access`) | `SetDeviceAccessRequest`: both members required, so a change cannot drop the pin by leaving it out; never exported in a bundle | Permission matrix and `CriticalActionsTests`; e2e `api/devices.spec.ts` |
| `DeviceViewModel.CredentialPrefix`, `CredentialIssuedOn` | Device readers | Server, at registration or `POST .../credential` (`Device.Create` with a second factor) | The prefix (L2) finds the row and cannot authenticate | Unit `DeviceTests.Credential_Should_BeStoredAsPrefixAndHashOnly` |
| `DeviceCredentialViewModel.Credential`, `ImportedDeviceCredentialViewModel.Credential` | The caller who registered, rotated or imported, in that answer only | Server | L4, shown once, `no-store`; a read never shows it | E2e `api/devices.spec.ts`, `api/csv-imports.spec.ts`; unit `DataClassificationTests` |
| `DeviceViewModel.State`, `RetiredOn` | Device readers | Server; retire with `Device.Delete` and a second factor; delete with `Device.Delete` (SA only: the seed's `ZonesAndDevices` leaves it out) | Lifecycle methods of the `Device` entity | Unit `DeviceTests.Retire_Should_RevokeTheCredentialAndRefuseEveryLaterChange` |
| `CalibrationViewModel` | Device readers | `Device.Edit` (`POST .../calibrations`) | `RecordCalibrationRequest`; `PerformedBy` is the caller's user name and `Notes` free text (both L3); `LastCalibratedOn` and `LastCalibrationPassed` of the device follow from it | Integration `DeviceRegistryTests.Lifecycle_Should_RegisterCalibrateMoveRotateAndRetire_When_RunEndToEnd` |
| `DeviceHealthViewModel`, `ZoneHealthViewModel`, `DeviceHealthOverviewViewModel` | Device readers (`GET .../health`) | Server (heartbeats) | At most 2,000 of each, `Truncated` beyond | E2e `api/device-health.spec.ts` |
| `DeviceSelfViewModel`, `DeviceZoneViewModel` | The device itself (`[DeviceAuthenticated]`, Ingest); the zone only for the device's own queue zone (`ownZoneOnly`) | Server | `DeviceAuthentication` answers 403 for another zone | Unit `DeviceAuthenticationTests`; e2e `api/device-auth.spec.ts` |
| `DeviceImportPreviewViewModel.PreviewToken` | The caller who previewed | Server | Binds the file's SHA-256, the site and the user for 15 minutes (`CsvImportLimits.PreviewLifetime`) | Integration `DeviceImportTests`; e2e `api/csv-imports.spec.ts` |

### 3.5 Desks and border data

View models: `DeskViewModel`, `DeskCodeMappingViewModel`, `DeskCodeMappingImportRowViewModel`, `DeskCodeMappingImportPreviewViewModel`, `DeskCodeMappingImportCommitViewModel`
Other answer types: `DeskStateViewModel`, `DeskStatesViewModel` (`Ariva.Core/Services/Live/ISvcDeskStates.cs`); `ImmigrationViewModel`, `ImmigrationHallViewModel`, `ImmigrationLaneViewModel`, `ImmigrationDeskViewModel`, `ImmigrationGateViewModel`, `ImmigrationEGatesViewModel`, `ImmigrationRejectsViewModel`, `ImmigrationQueueViewModel` (`Ariva.Core/Services/Border/ISvcImmigrationView.cs`); `StaffingRecommendationViewModel`, `LaneStaffingViewModel`, `StaffingIntervalViewModel` (`Ariva.Core/Services/Border/ISvcStaffingRecommendations.cs`); the weekly summary (`WeeklySummary`, `Ariva.Core/Reports/WeeklySummary.cs`).

All fields of desks: read with topology read (BSS, TDM, HSM, SA), written with `Desk.Create`, `.Edit`, `.Delete` (SA). Desk code mappings: read with `DeskCodeMapping.View` or `.Search` (TDM, SA; BSS has none), written with `DeskCodeMapping.Create`, `.Edit`, `.Delete` (SA). The rows below are the border rules: a desk code beside a minute is border data (data boundary, principle 5; [data classification](data-classification.md), section 2).

| Field or field group | Read by | Written by | Rule and where enforced | Test |
|---|---|---|---|---|
| `DeskViewModel.Code`, `Kind`, `CheckpointId` | Topology read | `Desk.Create` (SA), once | `UpdateDeskRequest` carries only `Name`, `LaneCategories`, `InService` | E2e `api/topology.spec.ts` |
| Desk states of immigration and emigration desks and e-gates (`DeskStatesViewModel.Desks` rows, `BorderIncluded`) | `LiveQueue.View` holders with `BorderDesks.View` (BSS, SA) | Server (Ariva.Api.Stream's desk engine) | `SvcDeskStates.GetAsync`: the query names the checkpoint kinds the caller may see and leaves out a minute whose key ever belonged to a desk of another kind; a caller with neither desk permission (HSM) gets an empty list | Integration `ImmigrationIntakeTests.DeskStates_Should_ShowEachDesksLatestMinuteAndKeepBorderDesksForBorderRoles_When_Asked`; unit `DeskStatesTests.BorderDesks_Should_BeBorderData_When_RolesAreSeeded`; e2e `functional/live-operations.spec.ts` ("desk states and the arrival wave follow the role") |
| Desk states of check-in counters and security lanes (`AirportIncluded`) | `LiveQueue.View` holders with `AirportDesks.View` (TDM, SA) | Server | As above | Unit `DeskStatesTests.AirportDesks_Should_BeTheDutyManagersView_When_RolesAreSeeded`; the integration test above |
| Per-desk and per-gate rows of the immigration view (`ImmigrationHallViewModel.Desks`, `Gates`, `ImmigrationViewModel.DesksIncluded`) | `Immigration.View` holders with `BorderDesks.View` (BSS, SA) | Server (AMAN feed) | `SvcImmigrationView.GetAsync` and `Hall`: decided in the service, so a TDM's answer never holds a desk code | Unit `ImmigrationViewTests.Desks_Should_BeListedForBorderRolesOnly_When_TheHallIsBuilt`; integration `ImmigrationIntakeTests.ImmigrationView_Should_GiveLaneAggregatesToEveryoneAndDesksToBorderRolesOnly_When_AmanReported`; e2e `functional/immigration.spec.ts` ("a terminal duty manager sees lane and e-gate totals only") |
| A lane's service and cycle times (`ImmigrationLaneViewModel.MeanServiceSeconds`, `MaxP90ServiceSeconds`, `MeanCycleSeconds`) | BSS, SA always; TDM only when at least 3 desks with transactions made them | Server | `SvcImmigrationView.MinDesksForTimes`: fewer desks would be one or two desks' own figures | Unit `ImmigrationViewTests.Lanes_Should_LeaveOutTimesFromFewerThanThreeDesks_When_TheCallerHasNoDeskData` |
| The staffing recommendation, whole (`StaffingRecommendationViewModel`) | `ArrivalWaveLanes.View` holders (BSS, SA) | Server (Ariva.Api.Cronz) | Whole resource: the endpoint's permission and the service's own check from the stored roles (`SvcStaffingRecommendations`); lane aggregates only, no desk code | Integration `StaffingTests`; e2e `api/staffing-recommendation.spec.ts` ("ArrivalWaveLanes.View and the site decide who reads it"), `functional/immigration-staffing.spec.ts` |
| Weekly summary: per-desk rows of border desks | `Report.View` holders with `BorderDesks.View` | Server | No endpoint yet (ARV-146); `WeeklySummaryReader` reads only what the roles allow and `WeeklySummaries.Build` projects again | Unit `WeeklySummaryTests.DeskStates_Should_GiveEveryBorderDeskToABorderRole_When_ItHoldsBorderDesks`; integration `WeeklySummaryTests` |
| Weekly summary: a border lane's cells for others | `Report.View` holders without `BorderDesks.View`: each day and the week only where at least 3 desks were open, the days all or none | Server | `WeeklySummaries.MinDesksForLaneFigures`; closed desks never count, so no cell is one or two desks' own and none can be had by difference | Unit `WeeklySummaryTests.DeskStates_Should_GiveAnAirportRoleNoDeskRowAndNoCellOfFewerThanThreeOpenDesks_When_ItLacksBorderDesks`, `DeskStates_Should_WithholdALaneOfThreeConfiguredDesks_When_OnlyOneWasOpen`, `DeskStates_Should_ShowNoDayAndNothingThatGivesIt_When_ADayHadOneOpenDesk` |
| Weekly summary: check-in and security lanes | `Report.View` holders with `AirportDesks.View` | Server | As above | Unit `WeeklySummaryTests.DeskStates_Should_ShowAirportLanesOnlyToAirportRoles_When_ReadByOthers` |
| Weekly summary: data quality and availability | `Report.View` holders with `DataQuality.View` | Server | As above | Unit `WeeklySummaryTests.Quality_Should_BeLeftOut_When_TheReaderMayNotSeeDataQuality` |
| `DeskCodeMappingImportPreviewViewModel.PreviewToken` | The caller who previewed (SA) | Server | Binds the file's SHA-256, the site and the user for 15 minutes | E2e `api/csv-imports.spec.ts` |

### 3.6 Flights

View models: `SsimLineErrorViewModel`, `SsimLegViewModel`, `SsimPreviewViewModel`, `SsimImportViewModel`
Other answer types: `ArrivalWaveViewModel`, `ArrivalWaveFlightViewModel`, `ArrivalWaveMinuteViewModel`, `LaneCountsViewModel` (`Ariva.Core/Services/Flights/ISvcArrivalWave.cs`); the Integration API's batch answers are in section 3.11.

All fields: the SSIM preview and import with `FlightSchedule.Create` (SA) through `FlightSchedulesController`; the arrival wave with `ArrivalWave.View` (BSS, TDM, SA; HSM has none). No endpoint reads flight legs otherwise; flights are written by the Integration API (section 3.11) and the SSIM import.

| Field or field group | Read by | Written by | Rule and where enforced | Test |
|---|---|---|---|---|
| The lane split: `LaneCountsViewModel.Cit`, `Res`, `Vis`, `Crw`, `EGate` (per flight, per minute and in the alert window), `ArrivalWaveFlightViewModel.LaneSource`, `ArrivalWaveViewModel.EgateRejectRate`, `EgateRejectRateMeasured`, `RejectLane` | `ArrivalWave.View` holders with `ArrivalWaveLanes.View` (BSS, SA) | Server | `SvcArrivalWave.GetAsync` and `LaneCountsViewModel.Of(counts, lanes)`: null for others, who keep the totals (`Total`, `Passengers`); the e-gate rejects are not even read | E2e `api/arrival-wave.spec.ts` (a duty manager's lanes are null, the total kept); `functional/live-operations.spec.ts` |
| `SsimPreviewViewModel.PreviewToken` | The SA who previewed | Server | Binds the file's SHA-256, the site, the horizon, the window and the user for two hours | E2e `api/flight-schedules.spec.ts` |
| `SsimLineErrorViewModel.Reason` | The SA who previewed | Server | Ariva's fixed reason and the line's number, never the line's text | Unit `SsimTests` |

### 3.7 Alerts and rules

View models: `AlertRuleViewModel`, `AlertBacktestViewModel`, `AlertViewModel`
Other answer types: `AlertBacktestAlert`; `AlertNotice` on the live hub (section 3.13).

All fields of rules: read with `AlertRule.View` or `.Search` (BSS, TDM, HSM, SA), written with `AlertRule.Create`, `.Edit`, `.Delete` (BSS, TDM, SA; delete with a second factor) through `AlertRulesController`. Alerts: read with `Alert.View` or `.Search`, acted on with `Alert.Edit` (BSS, TDM, HSM, SA), through `AlertsController`; only the alerts the caller's roles are responsible for exist for the caller (row rule below).

| Field or field group | Read by | Written by | Rule and where enforced | Test |
|---|---|---|---|---|
| Rules and alerts on a border-only metric (`DesksBelowPlan`, `PredictedNowcast`), whole | `ArrivalWaveLanes.View` holders only (BSS, SA), whatever the owner and escalation roles | Rules: as other rules, owner and escalation role border roles only | `BorderAlerts` (`Ariva.Core/Security/BorderAlerts.cs`): `SvcAlertRules` and `SvcAlerts.Visible` leave them out of lists and answer 404 on get and every action; the same rule on the live hub, email, the daily report and CSV, deliveries and the weekly summary | Unit `BorderAlertsTests`, `BorderAlertExposureTests` (every reader listed); integration `StaffingAlertTests`, `PredictedAlertTests`, `AlertRuleServiceTests`; e2e `api/desks-below-recommendation.spec.ts`, `api/predicted-breach-border.spec.ts` |
| `AlertRuleViewModel.Code` | Rule readers | Server, never given again | `AlertRuleRequest` carries no code | E2e `api/alert-rules.spec.ts` ("the code is never given again") |
| `AlertRuleViewModel.SiteCode` | Rule readers | `AlertRule.Create`, once | `SvcAlertRules`: a change naming another site is refused ("A rule stays in its site.") | Integration `AlertRuleServiceTests`; e2e `api/alert-rules.spec.ts` ("a rule stays in its site") |
| `AlertRuleViewModel.EscalationContact` | Every rule reader of the site, HSM included | `AlertRule.Create`, `.Edit` | L3 free text, at most 100 characters; never exported in a bundle (the import lists the rules whose contact must be typed again) | Unit `SiteConfigurationTransferTests.Export_Should_CarryDevicesRulesAndDisplaysWithoutCredentialsContactsOrAccessSettings_When_TheSiteHasThem` |
| `AlertBacktestViewModel` | `AlertRule.Create` holders (BSS, TDM, SA) | Server | `POST .../backtest` | E2e `api/alert-rules.spec.ts` ("a backtest returns the count and first fire time, within bounds, for creators only") |
| Which alerts a caller reads or acts on (`AlertViewModel`, whole) | SA every alert of its sites; others an alert without owner, one their role owns, or one escalated to their role | | `SvcAlerts.Visible` and `Alert.IsFor`; any other alert answers 404 | E2e `api/alerts.spec.ts` ("each role sees only the alerts it is responsible for, in its own sites") |
| `AlertViewModel.AcknowledgedUtc`, `AcknowledgedBy`, `AcknowledgedNote`, `EscalatedUtc`, `EscalatedBy`, `EscalatedNote`, `ResolvedUtc`, `Resolution`, `ResolvedBy`, `ResolutionNote` | As the alert | `Alert.Edit` through acknowledge, escalate and resolve only (`AlertActionRequest.Note`); the user name is the caller's | `SvcAlerts.ActAsync`: the row is locked and moves forward only; names and notes L3 | E2e `api/alerts.spec.ts` ("the owner acknowledges, escalates and resolves with a note") |
| Every other alert field (rule, zone, device, metric, severity, state, raised value, times, owner and escalation roles, `EscalationContact`) | As the alert | Server (alert evaluation) | Copied from the rule at raise time | Unit `AlertLifecycleTests` |

### 3.8 Reports

View models: none
Other answer types: `DailyReport` and its rows (`Ariva.Core/Reports/DailyReport.cs`); `ReportScheduleViewModel`, `ReportRecipientViewModel` (`Ariva.Core/Services/Reports/ISvcReports.cs`); `WeeklySummary` (section 3.5).

All fields: the daily report and its CSV with `Report.View` (BSS, TDM, SA); schedules read with `ReportSchedule.View` or `.Search`, written with `ReportSchedule.Create`, `.Edit`, `.Delete` (BSS, TDM, SA).

| Field or field group | Read by | Written by | Rule and where enforced | Test |
|---|---|---|---|---|
| The day's alerts (`DailyReport` alert lines, the CSV's alerts section) | Each reader the alerts its roles see; border-only alerts only with `ArrivalWaveLanes.View` | Server | `ReportReader.ReadAsync` applies the alert screens' rule and `BorderAlerts`; the on-demand report, its CSV and each scheduled delivery read with their own reader's roles | E2e `api/reports.spec.ts` ("the daily report merges the lane hour and answers per role and site"), `api/desks-below-recommendation.spec.ts`, `api/predicted-breach-border.spec.ts` |
| The CSV's columns | `Report.View` holders | Server | `ReportCsv.Write`: no note, contact or user name; formula cells neutralised; `no-store` | Unit `DailyReportTests.Csv_Should_WriteExactlyTheDocumentedHeaderRow_When_EachSectionIsWritten`, `DailyReportTests.ReportTypes_Should_HaveNoPersonOrFreeTextProperty_When_Reflected` |
| `ReportScheduleViewModel.OwnerId`, `LastSentUtc` | Schedule readers | Server | The owner is the caller who created it | |
| `ReportScheduleViewModel.Recipients`, the recipients list (`GET .../report-schedules/recipients`, `ReportRecipientViewModel`) | Schedule readers; the list with `ReportSchedule.Create` or `.Edit` | `ReportSchedule.Create`, `.Edit` (`ReportScheduleRequest.RecipientIds`) | Only enabled accounts that reach the site and may read reports, never the break-glass account; user names and display names (L3) of those accounts reach BSS and TDM | E2e `api/reports.spec.ts` ("a schedule sends each recipient its report once") |
| `ReportScheduleViewModel.SiteCode` | Schedule readers | `ReportSchedule.Create`, once | `ReportScheduleRequest`: the site is given on creation and never changes | |

### 3.9 Validation

View models: `ValidationTargetsViewModel`, `ValidationZoneViewModel`, `ValidationDeskViewModel`, `ValidationLineViewModel`, `ValidationCampaignSummaryViewModel`, `ValidationCampaignViewModel`, `ManualCountViewModel`, `CaptureLineViewModel`, `CaptureZoneViewModel`, `CaptureDeskViewModel`, `CaptureCampaignViewModel`, `CapturedCountViewModel`, `TracerRunViewModel`, `TracerBatchViewModel`, `CapturedTracerBatchViewModel`, `DeskObservationViewModel`, `DeskObservationBatchViewModel`, `CapturedDeskBatchViewModel`, `CapturedDeskObservationViewModel`, `DeskObservationPageViewModel`, `ValidationResultsViewModel`
Other answer types: `ValidationResultsJson` (the bytes of a `ValidationResultsViewModel` projection, the only form the results leave the service in).

All fields: campaigns, every observer's counts, tracer runs and desk observations read with `Validation.View` (BSS, TDM, SA); campaigns planned, started and closed (close with a second factor) with `Validation.Manage` (BSS, TDM, SA); counts, tracer batches and desk states captured and corrected with `Validation.Capture` (VO only; `NotForAdministrators`). The desk rows follow one rule, `BorderDeskAccess` (`Ariva.Core/Security/BorderDeskAccess.cs`), asked by every desk path through `ValidationServiceBase.DeskAccessAsync`.

| Field or field group | Read by | Written by | Rule and where enforced | Test |
|---|---|---|---|---|
| A campaign's desks (`ValidationCampaignViewModel.Desks`, `DesksIncluded`, `ValidationDeskViewModel`) | `Validation.View` holders with `BorderDesks.View` (`BorderDeskAccess.Sees`: BSS, SA); others get `DesksIncluded` false and no desk | `Validation.Manage` with `BorderDesks.View` (planning desks without it answers 403) | `SvcValidationCampaigns` | Unit `GroundTruthTests.BorderDeskAccess_Should_KeepDesksFromAirportRolesWithoutBorderDesks_When_TheRolesAreRead`; integration `GroundTruthTests.DeskBatch_Should_RecordCorrectAndShowStatesToBorderRolesOnly_When_AnObserverLogsDesks`; e2e `api/validation.spec.ts` |
| `ValidationCampaignViewModel.CreatedById`, `StartedById`, `ClosedById`, `ManualCountViewModel.ObserverId`, `TracerRunViewModel.ObserverId` | As the campaign | Server: the caller's Ariva user id | Pseudonymous ids, never names | |
| The capture view (`CaptureCampaignViewModel`) | VO of the site, for running campaigns | Server | Lines, zones and the bin length only: no target, profile or other observer's count | Integration `ValidationTests.Campaign_Should_RunFromPlanToClose_When_AManagerPlansAndAnObserverCounts`; e2e `api/validation.spec.ts` |
| Desks in the capture view (`CaptureCampaignViewModel.Desks`, `DesksIncluded`, `CaptureDeskViewModel`) and the desk batches, corrections and own desk reads | A caller with `BorderDesks.View` or whose only role is VO (`BorderDeskAccess.Observes`); an account with an airport role and no `BorderDesks.View` sees no desk and gets 403, whatever observer role it also holds | VO, its own states only | `SvcValidationCapture`, `SvcDeskObservations` | Integration `GroundTruthTests.DeskPaths_Should_RefuseAnAirportRoleWithoutBorderDesks_When_ItAlsoHoldsTheObserverRole` |
| An observer's counts, runs and states (`GET .../capture/campaigns/{id}/counts`, `.../tracer-runs`, `.../desk-observations`) | The observer, its own only | The observer, its own only; a correction is a new revision with a reason; never for a campaign it created or started | `SvcValidationCapture`, `SvcTracerRuns`, `SvcDeskObservations`; script 0047 | Integration `ValidationTests.Capture_Should_RefuseTheCampaignsCreatorAndStarter_When_TheyAlsoHoldTheObserverRole`, `ValidationTests.IdempotencyKey_Should_BelongToOneObserver_When_AnotherObserverSendsTheSameKey`; unit `GroundTruthTests.DeskCorrectionProblem_Should_Refuse_When_NotOwnClosedOrWithoutAReason` |
| A campaign's desk observations for managers (`DeskObservationPageViewModel`) | `Validation.View` holders with `BorderDesks.View`; others get `DesksIncluded` false and an empty page | Never by managers | `SvcDeskObservations` | Integration `GroundTruthTests.DeskBatch_Should_RecordCorrectAndShowStatesToBorderRolesOnly_When_AnObserverLogsDesks` |
| Results: the desk section (`ValidationResultsViewModel.Desks`) | `Validation.View` holders with `BorderDesks.View` | Server | `ValidationResultsViewModel.For` with `Reader.Of` from the stored roles; `Audience.DesksIncluded` says it | Unit `ValidationResultsServingTests.For_Should_WithholdDeskStateResultsAndDeskKeys_When_TheReaderIsOnTheAirportSide`; integration `ValidationResultsTests.Served_Should_FollowTheCallersStoredRoles_When_AnAirportRoleAnObserverAndABorderManagerRead` |
| Results: observer-level results (`Observers`, `Desks.ObserverKeys`) | Every observer's: `Validation.View` or `Validation.Manage` holders; its own: a VO of the campaign; none: anyone else | Server | `For`, `ObserverResults` | Unit `ValidationResultsServingTests.For_Should_KeepOnlyTheObserversOwnResults_When_TheReaderIsAnObserver`, `For_Should_KeepOnlyTheirOwnDeskKeys_When_ABorderReaderSeesOnlyItsOwnObserverResults` |
| Results: the shadow nowcast's figures (`Nowcast` zones' and overall `Shadow` and `Both`, `LeftOut.ShadowMinutes` and its keys, the `ProofNotRead` review flag) | `Validation.View` holders only | Server; the shadow itself only by the validation reader login (script 0049) | `For` removes them otherwise; no other type may carry them | Unit `ValidationResultsServingTests.For_Should_WithholdTheShadowsFigures_When_TheReaderDoesNotHoldValidationView`, `ShadowNowcastExposureTests.Signatures_Should_NeverTakeOrReturnTheShadow_When_TheTypeServesScreensRulesReportsOrContracts`, `ValidationResultsExposureTests`; integration `QueueStreamTests` |
| Results: `Revision.Reason`, a new revision | `Validation.View` holders | `Validation.Manage` with a second factor (`POST .../results/revisions`) | `RecomputeValidationResultsRequest`: 1 to 500 characters | Unit `ValidationResultsServingTests.Recompute_Should_NeedAClean1To500CharacterReason_When_Asked` |
| Results: every other section (targets, criteria, review, counts, tracers' zone summaries, track completion, coverage, availability, calibrations without notes or person) | `Validation.View` holders of the site | Server | `For` keeps them for every reader | Unit `ValidationResultsServingTests.For_Should_ServeEverySection_When_TheReaderIsABorderManager` |

### 3.10 Displays and advertising

View models: none
Other answer types: `DisplayViewModel`, `DisplayEntryViewModel`, `DisplayIssuedViewModel`, `DisplayBoardViewModel`, `DisplayBoardEntryViewModel`, `DisplayBoardAdvertisingViewModel`, `DisplayBoardAdvertisingItemViewModel` (`ISvcDisplays.cs`); `InstructionSlideViewModel`, `InstructionTextViewModel`, `DisplayBoardSlideViewModel`, `DisplayBoardSlideTextViewModel` (`ISvcInstructionSlides.cs`); `AdvertisingPolicyViewModel` (`ISvcAdvertisingPolicies.cs`); `AdvertisingMediaViewModel` (`ISvcAdvertisingMedia.cs`); `AdvertisingPlaylistViewModel`, `AdvertisingPlaylistItemViewModel` (`ISvcAdvertisingPlaylists.cs`); `AdvertisingScheduleViewModel`, `AdvertisingTimelineViewModel`, `AdvertisingTimelineSegmentViewModel` (`ISvcAdvertisingSchedules.cs`); `AdvertisingPlaysViewModel` (`ISvcAdvertisingPlays.cs`); `AdvertisingFiguresViewModel`, `AdvertisingFigureViewModel`, `AdvertisingFigureRowViewModel` (`ISvcAdvertisingFigures.cs`); all in `Ariva.Core/Services/Displays`.

All fields: displays read with `Display.View` or `.Search` and written with `Display.Create` (with a second factor), `.Edit`, `.Delete` (BSS, TDM, SA); instruction slides read with `DisplayContent.View` and written with `DisplayContent.Manage` (BSS, TDM, SA); the advertising policy read with `AdvertisingPolicy.View` (BSS, TDM, SA) and written with `AdvertisingPolicy.Edit` and a second factor (SA); advertisements, playlists and schedules read with `Advertising.View` or `.Manage` and written with `Advertising.Manage` (TDM, SA), every advertising endpoint behind `[AdvertisingAllowed]` (403 while the site's policy is off).

| Field or field group | Read by | Written by | Rule and where enforced | Test |
|---|---|---|---|---|
| `DisplayViewModel.SiteCode`, `Code` | Display readers | `Display.Create` with a second factor, once | `SvcDisplays`: "A display stays in its site and keeps its code" (`DisplayErrors.StaysInSite`) | Integration `DisplayServiceTests.Display_Should_RoundTripAndAuditWithoutItsCredential_When_CreatedChangedAndDeleted` |
| `DisplayViewModel.CredentialPrefix`, `CredentialIssuedOn`; `DisplayIssuedViewModel.Credential` | The prefix: display readers. The credential: the caller who created the display or issued a new credential (`Display.Edit` with a second factor), in that answer only | Server | L4 credential shown once, `no-store`; stored as prefix and SHA-256 | Integration `DisplayServiceTests.Display_Should_RoundTripAndAuditWithoutItsCredential_When_CreatedChangedAndDeleted`; unit `DataClassificationTests` |
| `DisplayViewModel.SecondRegion`, `Layout`, `AdvertisingPauseMinutes` | Display readers | `Display.Edit` | A display set to advertising is refused where the site's policy does not allow it (`DisplayErrors.AdvertisingOff`; a database trigger as well) | E2e `api/advertising-policy.spec.ts` |
| The board (`DisplayBoardViewModel`, whole) | The display's own player only (`[DisplayAuthenticated]`: its code and credential, `DisplayAuthentication`) | Server | L1: labels, waits and their age, slides' words, the illustrative flag and, while advertising is allowed, the plan; no alert, staffing, desk or person member; no playlist, advertiser, campaign, file or person name | Unit `DisplayAuthenticationTests.Player_Should_Authenticate_Only_With_ItsOwnCodeAndCredential`, `BorderAlertExposureTests.DisplayBoard_Should_CarryNoAlertOrStaffingMember_When_Reflected`, `BorderAlertExposureTests.DisplayBoard_Should_CarryNoNameOfAPlaylistAdvertiserOrPerson_When_ItPlaysAdvertising`; e2e `functional/displays.spec.ts`, `api/advertising-board.spec.ts` |
| An advertisement's file for a player (`GET api/v1/display/advertising/{id}`) | The display's own player: an approved item of its own site that may play now, while it is a split display set to advertising and the policy allows it | | Any such item of the site, not only the items of the current plan (ARV-143b, decision point 7) | E2e `api/advertising-board.spec.ts` |
| `AdvertisingPolicyViewModel.Reason` | Policy readers (BSS, TDM, SA) | SA with a second factor | L3 free text naming who authorised the change | E2e `api/advertising-policy.spec.ts` |
| `AdvertisingMediaViewModel.CanApprove`, `ReviewToken` | Computed per caller: `CanApprove` is true only for a draft the caller neither uploaded nor last changed | Approving needs `Advertising.Approve` with a second factor and the review token of the item as shown | `SvcAdvertisingMedia`; script 0058 holds the two-person rule for any writer | E2e `api/advertising-media.spec.ts` |
| `AdvertisingMediaViewModel.UploadedBy`, `ApprovedBy`, `Advertiser`, `Campaign`, `OriginalFileName` | Library readers (TDM, SA) | Advertiser, campaign and days: `Advertising.Manage` (`ChangeAdvertisingMediaRequest`); the names of people: server | L3 values; never on the board | E2e `api/advertising-media.spec.ts` |
| `AdvertisingMediaViewModel.Status`, `ApprovedOn`, `Playable` | Library readers | Server, through approve and retire | An item plays only once approved and within its days | E2e `api/advertising-media.spec.ts` |
| Proof of play (`AdvertisingPlaysViewModel`; figures) | Plays: written by the display's own player only (`[DisplayAuthenticated]`). Figures: no endpoint yet; the service answers `Advertising.View` holders of the site only | The player | `SvcAdvertisingPlays`, `SvcAdvertisingFigures` | Integration `AdvertisingPlansServiceTests`; e2e `api/advertising-plays.spec.ts` |

### 3.11 Integration clients

View models: `IntegrationClientViewModel`, `IntegrationClientCredentialsViewModel`, `IntegrationTokenViewModel`, `IntegrationCallerViewModel`, `OutboundEndpointViewModel`
Other answer types: `IntegrationBatchViewModel`, `FlightItemResult` (`Ariva.Core/Integration/IntegrationBatches.cs`, `Ariva.Core/Flights/FlightData.cs`); `ImmigrationBatchViewModel`, `ImmigrationItemResult` (`Ariva.Core/Border/ImmigrationRules.cs`); the AIDX acknowledgement.

All fields: clients and outbound endpoints read with `IntegrationClient.View` or `.Search` and written with `IntegrationClient.Create`, `.Edit`, `.Delete`, every write with a second factor (SA), through `IntegrationClientsController` and `OutboundEndpointsController`, within the administrator's sites.

| Field or field group | Read by | Written by | Rule and where enforced | Test |
|---|---|---|---|---|
| `IntegrationClientViewModel.ClientId`, `Kind` | SA | Server (client id) and `IntegrationClient.Create` (kind), once | `UpdateIntegrationClientRequest` carries neither | Integration `IntegrationClientTests.Client_Should_ShowItsCredentialsOnceAndStoreThemProtected_When_Registered` |
| `IntegrationClientCredentialsViewModel.ClientSecret`, `TotpSecret`, `TotpUri` | The SA who created the client or rotated that part, in that answer only | Server | L4, shown once, `no-store`; stored as PBKDF2 and under Data Protection | As above; unit `DataClassificationTests` |
| `IntegrationClientViewModel.Status`, `Locked`, `LockedUntilUtc`, `FailedAttempts`, `SecretChangedUtc`, `TotpChangedUtc`, `LastTokenUtc` | SA | Server; disable, enable and unlock are their own actions | | E2e `api/integration-auth.spec.ts` |
| `OutboundEndpointViewModel.Code`, `Purpose` | SA | `IntegrationClient.Create`, once | `UpdateOutboundEndpointRequest` carries neither; the authentication kind is fixed for the endpoint's life | Integration `OutboundEndpointTests.Endpoint_Should_BeAdministeredWithinTheCallersSitesAndKeepItsSecretProtected_When_Registered` |
| The secret material (`OutboundSecretRequest`) | Nobody: write only; reads show `HasPinnedCa`, `HasClientCertificate` and `SecretChangedUtc` only | `IntegrationClient.Create` with a second factor (`PUT .../secret`), and with any change of base URL, networks, pinned CA or token path | `OutboundRules`; Data Protection purpose `Ariva.Outbound.v1` | As above |
| `OutboundEndpointViewModel.LastPollUtc`, `LastStatus`, `ConsecutiveFailures` | SA | Server (pollers) | Counts only, never the remote system's text | Unit `AmanPullTests` |
| `IntegrationCallerViewModel` | The calling client itself, about itself (connectivity checks, `[IntegrationScope]`) | Server | Its own id, name, scopes, sites and policy only | E2e `api/integration-auth.spec.ts` |
| `IntegrationTokenViewModel` | The calling client, in the token exchange's answer | Server | L4 | E2e `api/integration-auth.spec.ts` |
| What the Integration API answers about data (`IntegrationBatchViewModel`, `ImmigrationBatchViewModel`) | The calling client, about the items it sent: index, key, applied, errors and warnings | The client, with the endpoint's scope, for its bound sites only | No endpoint reads data back: the `queues:read` and `displays:read` endpoints of [integration](../architecture/integration.md) are not built. The AMAN contracts it takes carry no person, document or officer identifier | Unit `LayeringTests.GetProperties_Should_ExposeNoOfficerOrPersonIdentifier_When_TypeIsPublicBusinessContract`, `AmanFeedPactTests`; e2e `api/flights-api.spec.ts`, `api/aman-feed.spec.ts` |

### 3.12 Jobs and system information

View models: `SystemInfoViewModel`
Other answer types: none.

All fields: `SystemInfoViewModel` read with `SystemInfo.View` (SA), written by nobody. The Cronz jobs have no Ariva endpoint: the TickerQ dashboard answers only an operator key (`JobsDashboardGuard`, `Ariva.Api.Cronz/Jobs/JobsDashboard.cs`), not a role, and is off unless configured (ASVS V6.3.3, ARV-087). A job reads what its purpose needs and answers no caller; the report deliveries build each recipient's report with that recipient's roles (section 3.8).

| Field or field group | Read by | Written by | Rule and where enforced | Test |
|---|---|---|---|---|
| The jobs, their runs and their arguments | The holder of the dashboard key | The holder of the dashboard key (run now) | Not tied to a user or role; each job is idempotent (`ReportJobs`, `StaffingJobs`, `AvailabilityJobs`, `RetentionJobs`, `SecretJobs`) | Unit `JobsDashboardTests.Api_Should_AnswerOnlyWithTheKey_When_TheDashboardIsOn` |

### 3.13 Live data

View models: none
Other answer types: `LiveZoneSnapshot` (`Ariva.Infra/Live/LiveSnapshots.cs`) and `AlertNotice` (`Ariva.Infra/Live/AlertNotices.cs`) on the live hub; `OverflowBandsViewModel`, `OverflowZoneViewModel`, `OverflowBandViewModel` (`Ariva.Core/Services/Live/ISvcOverflowBands.cs`); the data quality reads `Ariva.Core.Services.Quality.ZoneHealthViewModel`, `ZoneHealthBinViewModel` (`ISvcZoneHealth.cs`) and `AvailabilityViewModel` (`ISvcAvailability.cs`). Desk states and the immigration view are in section 3.5, the arrival wave in section 3.6.

All fields: the live hub (`/hubs/live`) and the overflow bands with `LiveQueue.View` (BSS, TDM, HSM, SA); zone health and availability with `DataQuality.View` (BSS, TDM, SA). All are written by the server only.

| Field or field group | Read by | Written by | Rule and where enforced | Test |
|---|---|---|---|---|
| A zone's snapshot (`LiveZoneSnapshot`: queue length, nowcast, throughput) | `LiveQueue.View` holders of the zone's site, for any queue zone of the site's published profile: no role cut, so HSM reads every queue zone, border lane queues included | Server (Ariva.Api.Stream) | `LiveHub.JoinZone`: grant and site read from storage on every join, the zone checked against the published profile; no desk member and no shadow nowcast (built from the published values only, `LiveMinuteSnapshots`) | Unit `LiveHubTests.JoinZone_Should_RefuseTheSameWay_When_TheSiteIsNotTheCallersOrTheGrantIsMissing`, `LiveHubTests.JoinZone_Should_RefuseAnUnknownZone_When_ItIsNotInTheSitesPublishedProfile`, `ShadowNowcastExposureTests.ExposedTypes_Should_NotHoldTheShadow_When_TheyServeScreensRulesOrReports`; e2e `api/live-hub.spec.ts` |
| Alert notices (`AlertNotice`) | The site's alert groups of the caller's roles (`JoinAlerts`, `Alert.View` and the site); a border-only alert's notice only to border roles | Server | `AlertNotice.Audience`, `BorderAlerts.Audience`; no person: who acted is read from the API | Unit `LiveHubTests.Notices_Should_ReachOnlyTheResponsibleRoles_When_Routed`, `LiveHubTests.Notices_Should_ReachBorderRolesOnly_When_TheMetricIsBorderOnly`; e2e `api/alerts.spec.ts` ("alert changes reach the live hub groups of the responsible roles only") |
| Overflow bands (`OverflowBandsViewModel`) | `LiveQueue.View` holders of the site, every queue zone with bands: no role cut, the same zones as the hub | Server (Ariva.Api.Stream, `overflow_minute`) | `OverflowBandsController`, `ISvcOverflowBands`: zone-level occupancy, no desk, track or person | E2e `api/overflow-bands.spec.ts` ("LiveQueue.View and the site decide who reads it"); unit `OverflowBandsEndpointTests` |
| Zone health and availability | `DataQuality.View` holders (BSS, TDM, SA); HSM gets 403 | Server | `ZoneHealthController`, `AvailabilityController`; zone-level counts only | E2e `api/zone-health.spec.ts`, `api/availability.spec.ts`, `functional/overflow-quality.spec.ts` (a handler station manager's zone health read answers 403) |

## 4. Where the server cuts fields, and where only a whole resource is guarded

The web app hides what a role cannot use; the server decides (asvs-l2 V8.3.1; e2e `functional/auth.spec.ts`, "the server refuses the rest"). Every rule of this page is on the server.

The server cuts fields or rows inside one answer, by the caller's stored roles, in these places:

| Answer | What is cut | Where |
|---|---|---|
| Desk states | Border desk rows without `BorderDesks.View`; airport desk rows without `AirportDesks.View` | `SvcDeskStates.GetAsync` (in the SQL) |
| Immigration view | Per-desk and per-gate rows without `BorderDesks.View`; a lane's times from fewer than 3 desks | `SvcImmigrationView.GetAsync`, `Hall` |
| Arrival wave | The lane split and the e-gate rejects without `ArrivalWaveLanes.View` | `SvcArrivalWave.GetAsync`, `LaneCountsViewModel.Of` |
| Validation campaign, capture view, desk observation page | Desks without `BorderDeskAccess.Sees` or `.Observes` | `SvcValidationCampaigns`, `SvcValidationCapture`, `SvcDeskObservations` |
| Validation results | The desk section, observer-level results, the shadow's figures | `ValidationResultsViewModel.For` |
| Alert lists, alert rule lists, daily report and CSV, deliveries, weekly summary, live hub notices | Border-only alerts and rules without `ArrivalWaveLanes.View`; alerts the caller's roles are not responsible for | `BorderAlerts`, `SvcAlerts.Visible`, `SvcAlertRules`, `ReportReader`, `WeeklySummaryReader`, `AlertNotice.Audience` |
| Weekly summary (no endpoint) | Desk rows, small border lane cells, airport lanes, data quality and availability | `WeeklySummaryReader`, `WeeklySummaries.Build` |
| User search, audit entries | Accounts beyond a site-limited administrator's sites, the break-glass account, entries about them | `AdministrationGuards.Administrable`, `SvcUsers.VisibleAsync`, `SvcAuditEntries.VisibleAsync` |
| Advertisement items | `CanApprove` per caller | `SvcAdvertisingMedia` |
| Site configuration export and import | Refused whole when a border-only rule meets a caller without `ArrivalWaveLanes.View`; never exports credentials, contacts or access settings | `SvcSiteConfiguration`, `SiteConfigurationTransfer.Export` |

Everywhere else a permission guards the whole resource, and every holder reads every field of it within its sites. Among them, fields of note:
- Topology, floor plans and zone profiles: every holder of topology read or `ZoneProfile.View`, HSM included, reads every desk, checkpoint and zone of its sites, border ones included, and who created and published each profile.
- Devices: every `Device.View` holder (BSS, TDM, SA) reads the access settings and the credential prefix.
- Alert rules: every `AlertRule.View` holder, HSM included, reads every rule of its sites that is not border-only, with its owner, escalation role and escalation contact.
- Live zone snapshots and overflow bands: every `LiveQueue.View` holder reads every queue zone of its sites.
- The staffing recommendation: guarded whole by `ArrivalWaveLanes.View`, since all of it is border data.
- Report schedules, displays, slides, advertising, integration clients, outbound endpoints, the site calendar, SSIM and CSV imports: whole resource.

## 5. Gaps

Nothing below is fixed by this page.

1. **Handler tenancy is not built (V8.4.1).** Since ARV-095 [wiki 13](../../wiki/13-Security-Guide.md) section 4 states that the site is the tenant boundary, and [wiki 01](../../wiki/01-Product-Overview.md) (tenancy and the role table) marks handler isolation as planned (ARV-213): no organisation binding on users, no RLS policy in any script (asvs-l2 V8.4.1). So an HSM reads, within its sites: every queue zone's live snapshot and overflow bands, border lane queues included (`LiveHub.JoinZone`, `OverflowBandsController`; the ARV-120 note of ARV-095); every desk and checkpoint of the topology, border desks included; every zone profile; every alert rule that is not border-only, with its escalation contact; and the alerts of its role, not of its organisation (`Alert.IsFor` works on roles). It reads no desk state (`AirportDesks.View` is withheld until handler tenancy exists, `RolePermissions.HandlerStationManager`). The owner decides whether handler tenancy is built or the site stays the tenant boundary (ARV-213).
2. **The frozen validation results hold the shadow's zone-level aggregates in a table the runtime role reads.** `validation_result_revision` (script 0050) is readable by the runtime login, so for those aggregates the rule of section 3.9 is enforced by code (only the results service names the table, `ValidationResultsExposureTests`), not by privileges. Owner decision Proposed (docs/product/decisions.md, ARV-104g point 5; asvs-l2 V8.1.2). To confirm.
3. **The display fetch serves any playable item of the site.** A display's credential fetches any approved advertisement of its own site that may play now, not only the items of its current plan (ARV-143b, decision point 7; wiki 13 section 4). Accepted by that decision; listed because it is wider than the board.

To confirm (the code is consistent with the guides, but the guides do not settle the rule):

4. **Escalation contacts reach handler station managers.** `AlertRuleViewModel.EscalationContact` and `AlertViewModel.EscalationContact` are L3 free text ([data classification](data-classification.md), section 3.6) and reach every `AlertRule.View` holder of the site, HSM included, for rules it neither owns nor is escalated to. [Wiki 12](../../wiki/12-User-Guide.md) says handler station managers read the rules; no guide says whether they should read the contacts. To confirm (owner).
5. **Report recipients name accounts to supervisors and duty managers.** `GET .../report-schedules/recipients` and `ReportScheduleViewModel.Recipients` give the user names and display names (L3) of the site's report-eligible accounts to holders of `ReportSchedule.Create`, `.Edit` or `.View` (BSS, TDM). [Wiki 12](../../wiki/12-User-Guide.md) describes it; [data classification](data-classification.md) section 4 gives staff accounts to system administrators only. To confirm that the recipients list is the intended exception, and record it there.
6. **Device access settings reach every device reader.** `DeviceViewModel.AllowedSources` and `ClientCertificateSha256` (configuration, not secrets) are read by BSS and TDM, while changing them needs `Device.Edit` with a second factor. To confirm that reading them needs no more than `Device.View`.
7. **Valid roles per deployment are an administration rule only.** Nothing in code stops an administrator from granting a border role in an airport deployment or the reverse ([wiki 11](../../wiki/11-Administration-Guide.md), section 2). Physical separation of deployments (ADR-0012) is the control. To confirm that no code check is wanted.

Rules without a direct test:

8. **A report schedule keeps its site.** `SvcReportSchedules.UpdateAsync` checks a change against the schedule's own site and does not read `ReportScheduleRequest.SiteCode`, so a change cannot move a schedule to another site. No test was found that sends another site's code in a change and asserts the schedule stays (integration `ReportServiceTests` covers recipients and deliveries). To confirm whether a test is wanted; the alert rules' equivalent refuses the change outright (`SvcAlertRules`, a site code other than the rule's).

## 6. Not placed

View models: `PageViewModel`

`PageViewModel<T>` is the paging envelope of the searches (users, audit entries, alerts, topology and others): `Data`, `TotalCount`, `PageIndex`, `PageSize`. It belongs to no one entity: its rows follow the rules of their element type, and `TotalCount` counts only the rows the caller may read, because every search filters before it counts (for example `SvcAlerts.SearchAsync`, `SvcUsers.SearchAsync`, `SvcAuditEntries.SearchAsync`). The page size is at most 500 (`SvcBase.MaxPageSize`).
