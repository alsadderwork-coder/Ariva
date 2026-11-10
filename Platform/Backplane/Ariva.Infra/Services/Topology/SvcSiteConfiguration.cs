using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Domain.InputModels;
using Ariva.Core.Domain.ViewModels;
using Ariva.Core.Security;
using Ariva.Core.Services.Sensing;
using Ariva.Core.Services.Storage;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Foundation;
using Ariva.Infra.Storage;
using Microsoft.Extensions.DependencyInjection;
using NHibernate.Linq;
using ZiggyCreatures.Caching.Fusion;

namespace Ariva.Infra.Services.Topology;

/// <summary>
/// Site configuration export and import (ARV-122a). A site outside the caller's sites answers like one that does not
/// exist (CWE-863). An export reads the site in one repeatable-read snapshot, refuses a site it cannot carry whole (never
/// cutting it short) and audits the bundle hash and the counts. An import trusts nothing in the bundle (CWE-501): the
/// manifest, a malware scan of every decoded part (ARV-092b, <see cref="UploadScreening"/>), every part's rules, the
/// entities' rules and the ARV-018 floor plan checks run before anything is kept; it
/// locks the site row, imports into an empty site only, creates every record with a new id in one transaction (all or
/// nothing), and audits the bundle hash, the counts and the outcome, a refusal in a transaction of its own. Neither
/// audit entry holds a part's content. ARV-122b: devices, alert rules and displays travel without any credential,
/// calibration, access setting, escalation contact or pairing secret; each device is registered through the registration
/// path itself (its rules, its credential issuing, its audit entry) in the import's transaction, and its new credential is
/// answered once and never logged. ARV-294 (CWE-400): no transaction waits on the disk or on a second connection: an export reads the
/// plans' files after its snapshot ends, an import writes them before it takes the site's row (a rollback removes them), and a
/// refusal ends the import's transaction before its audit entry is written.
/// </summary>
internal sealed class SvcSiteConfiguration(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ISiteScope siteScope,
    IFileStorage storage,
    AuditTrail audit,
    IServiceScopeFactory scopes,
    IFusionCache cache,
    ISvcDevices devices,
    CallerRoles callerRoles,
    UploadScreening screening,
    ILogger<SvcSiteConfiguration> logger) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcSiteConfiguration
{
    private const string Target = "SiteConfiguration";

    #region Export

    public async Task<Result<SiteConfigurationExportViewModel>> ExportAsync(string siteCode, CancellationToken ct = default)
    {
        if (!await ReachableAsync(siteCode, ct))
            return Result.Error<SiteConfigurationExportViewModel>(TopologyErrors.NotFound);

        // One snapshot for every read, so a change committed during the export cannot split the bundle.
        var snapshot = !UnitOfWork.StorageProvider.IsTransactionActive();
        if (snapshot)
            await UnitOfWork.StorageProvider.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

        var source = await SourceAsync(siteCode, ct);
        // ARV-181b2, ARV-187 (defence in depth; Transfer is held by administrators, who see border data): rules on a
        // border-only metric travel only to a caller who sees border data. A rule the import would refuse (one from before
        // ARV-187 on the predicted nowcast without a border owner) refuses the export in Export (RulesNotImportable).
        if (source.AlertRules.Any(r => BorderAlerts.IsBorderOnly(r.Metric)) && !BorderAlerts.Receives(await callerRoles.GetAsync(ct)))
            return await RefuseExportAsync(siteCode, Ariva.Core.Services.Alerting.AlertRuleErrors.BorderOnly, ct);
        var exported = SiteConfigurationTransfer.Export(source);
        if (exported.HasErrors)
            return await RefuseExportAsync(siteCode, exported.ErrorMessages.First(), ct);
        var data = exported.Data;

        var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [SiteConfigurationBundles.Topology] = SiteConfigurationParts.Write(data.Topology),
            [SiteConfigurationBundles.DeskCodeMappings] = SiteConfigurationParts.Write(data.DeskCodeMappings),
            [SiteConfigurationBundles.ZoneProfile] = SiteConfigurationParts.Write(data.ZoneProfiles),
            [SiteConfigurationBundles.FloorPlans] = SiteConfigurationParts.Write(data.FloorPlans),
            [SiteConfigurationBundles.Devices] = SiteConfigurationParts.Write(data.Devices),
            [SiteConfigurationBundles.AlertRules] = SiteConfigurationParts.Write(data.AlertRules),
            [SiteConfigurationBundles.Displays] = SiteConfigurationParts.Write(data.Displays)
        };
        if (parts.Values.Any(part => part.Length > SiteConfigurationLimits.MaxJsonPartBytes))
            return await RefuseExportAsync(siteCode, SiteConfigurationErrors.TooLargeToExport, ct);

        // ARV-294 (CWE-400): every record is read, so the snapshot ends here (it wrote nothing) and no transaction waits on the disk
        // while the plans' files are read. A plan replaced meanwhile has its old file removed after that change commits: the read then
        // finds it missing and the export is refused (FloorPlanFileUnreadable), as for a file lost from storage, and is asked again.
        if (snapshot)
            await UnitOfWork.StorageProvider.RollbackTransactionAsync(ct);

        // Files are read one at a time against the bundle's budget; one beyond it stops the export before it is read.
        long size = parts.Values.Sum(part => (long)SiteConfigurationLimits.Base64Length(part.Length));
        for (var index = 0; index < data.FloorPlanFiles.Count; index++)
        {
            var plan = data.FloorPlanFiles[index];
            if (size + SiteConfigurationLimits.Base64Length((int)Math.Min(plan.SizeBytes, SiteConfigurationLimits.MaxFileBytes)) > SiteConfigurationLimits.MaxBundleBytes)
                return await RefuseExportAsync(siteCode, SiteConfigurationErrors.TooLargeToExport, ct);
            var bytes = await ReadFileAsync(plan, ct);
            if (bytes is null)
                return await RefuseExportAsync(siteCode, SiteConfigurationErrors.FloorPlanFileUnreadable, ct);
            size += SiteConfigurationLimits.Base64Length(bytes.Length);
            parts[SiteConfigurationBundles.FloorPlanFile(index + 1)] = bytes;
        }

        var bundle = SiteConfigurationBundles.Build(siteCode, UtcNow, parts);
        // The document an operator posts back must fit the import's body limit, as the web serialiser writes it.
        if (JsonSerializer.SerializeToUtf8Bytes(bundle, JsonSerializerOptions.Web).LongLength > SiteConfigurationLimits.MaxBundleBytes)
            return await RefuseExportAsync(siteCode, SiteConfigurationErrors.TooLargeToExport, ct);

        var bundleSha256 = SiteConfigurationBundles.BundleSha256(bundle.FormatVersion, bundle.SourceSite, bundle.ExportedAtUtc, bundle.Manifest);
        await audit.RecordAsync($"{Target}.Exported", "Site", null, siteCode, null,
            Summary("exported", null, bundleSha256, bundle.FormatVersion, siteCode, bundle.Manifest.Count, data.Counts), ct);
        IReadOnlyList<string> notes = data.ZoneProfiles.Count == 0 ? [SiteConfigurationErrors.NoPublishedProfile] : [];
        return new Result<SiteConfigurationExportViewModel>(new SiteConfigurationExportViewModel(bundleSha256, data.ZoneProfiles.Count == 1, data.ProfileVersion,
            data.GeometryHash, data.PortableGeometryHash, data.Counts, notes, bundle));
    }

    /// <summary>
    /// The site's live records, each read bounded at its limit plus one (the export refuses beyond, never cuts short), and
    /// its published zone profile. Parents are read first, so each record's parent comes from the session.
    /// </summary>
    private async Task<SiteConfigurationSource> SourceAsync(string siteCode, CancellationToken ct)
    {
        var terminals = await Query<Terminal>().Where(t => t.SiteCode == siteCode && t.DeletedOn == null)
            .Take(SiteConfigurationLimits.MaxTerminals + 1).Fetch(t => t.Airport).ToListAsync(ct);
        var levels = await Query<Level>().Where(l => l.SiteCode == siteCode && l.DeletedOn == null).Take(SiteConfigurationLimits.MaxLevels + 1).ToListAsync(ct);
        var checkpoints = await Query<Checkpoint>().Where(c => c.SiteCode == siteCode && c.DeletedOn == null).Take(SiteConfigurationLimits.MaxCheckpoints + 1).ToListAsync(ct);
        var desks = await Query<Desk>().Where(d => d.SiteCode == siteCode && d.DeletedOn == null).Take(SiteConfigurationLimits.MaxDesks + 1).ToListAsync(ct);
        var mappings = await Query<DeskCodeMapping>().Where(m => m.SiteCode == siteCode && m.DeletedOn == null && m.Desk.DeletedOn == null)
            .Take(SiteConfigurationLimits.MaxDeskCodeMappings + 1).ToListAsync(ct);
        var published = await Query<ZoneProfile>().FirstOrDefaultAsync(p => p.SiteCode == siteCode && p.Status == ZoneProfileStatus.Published, ct);
        var plans = await Query<FloorPlan>().Where(p => p.SiteCode == siteCode && p.DeletedOn == null && p.Level.DeletedOn == null)
            .Take(SiteConfigurationLimits.MaxFloorPlans + 1).ToListAsync(ct);
        // ARV-122b: devices that are not retired (a retired one is history: it stays at the source), live alert rules and displays.
        var siteDevices = await Query<Device>().Where(d => d.SiteCode == siteCode && d.DeletedOn == null && d.State != DeviceState.Retired)
            .Take(SiteConfigurationLimits.MaxDevices + 1).ToListAsync(ct);
        var rules = await Query<AlertRule>().Where(r => r.SiteCode == siteCode && r.DeletedOn == null).Take(SiteConfigurationLimits.MaxAlertRules + 1).ToListAsync(ct);
        var displays = await Query<Display>().Where(d => d.SiteCode == siteCode && d.DeletedOn == null).Take(SiteConfigurationLimits.MaxDisplays + 1).ToListAsync(ct);
        return new SiteConfigurationSource(terminals, levels, checkpoints, desks, mappings, published, plans, siteDevices, rules, displays);
    }

    /// <summary>A stored plan's bytes, read to at most the floor plan limit; null when missing, larger or not the bytes its record hashed.</summary>
    private async Task<byte[]> ReadFileAsync(FloorPlan plan, CancellationToken ct)
    {
        await using var stream = await storage.OpenReadAsync(plan.StorageKey, ct);
        if (stream is null)
            return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > SiteConfigurationLimits.MaxFileBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        return plan.Sha256 is null || string.Equals(SiteConfigurationBundles.Sha256(bytes), plan.Sha256, StringComparison.Ordinal) ? bytes : null;
    }

    private async Task<Result<SiteConfigurationExportViewModel>> RefuseExportAsync(string siteCode, string error, CancellationToken ct)
    {
        await audit.RecordAsync($"{Target}.ExportRefused", "Site", null, siteCode, null, Summary("refused", error, null, null, null, 0, null), ct);
        return Result.Error<SiteConfigurationExportViewModel>(error);
    }

    #endregion

    #region Import

    /// <summary>The audit reason of an import rolled back after its records were written, a failed commit included.</summary>
    private const string RolledBack = "The import was rolled back after its records were written.";

    public async Task<Result<SiteConfigurationImportViewModel>> ImportAsync(string siteCode, ImportSiteConfigurationRequest request, string airportCode, CancellationToken ct = default)
    {
        // A site the caller cannot reach answers like one that does not exist, and is not audited (as everywhere).
        if (!await ReachableAsync(siteCode, ct))
            return Result.Error<SiteConfigurationImportViewModel>(TopologyErrors.NotFound);

        // From here every outcome is audited: a refusal and an unexpected failure each in a transaction of their own (the
        // import's is rolled back), a success with the import's records.
        var attempt = new ImportAttempt();
        try
        {
            return await ImportIntoReachableSiteAsync(siteCode, request, airportCode, attempt, ct);
        }
        catch (OperationCanceledException)
        {
            UnitOfWork.PromiseNotToCommit();
            throw;
        }
        catch (Exception)
        {
            // Anything unexpected (the database, storage, the file checks) keeps nothing: never commit what was promised,
            // record the failure, and let the global handler answer 500 without details.
            await RefuseImportAsync(siteCode, null, attempt.Bundle, failed: true);
            throw;
        }
    }

    private async Task<Result<SiteConfigurationImportViewModel>> ImportIntoReachableSiteAsync(string siteCode, ImportSiteConfigurationRequest request, string airportCode,
        ImportAttempt attempt, CancellationToken ct)
    {
        if (airportCode is not null && !TopologyCodes.IsIata(airportCode))
            return await RefuseImportAsync(siteCode, SiteConfigurationErrors.InvalidAirportCode, null);

        var opened = SiteConfigurationBundles.Open(request);
        if (opened.HasErrors)
            return await RefuseImportAsync(siteCode, opened.ErrorMessages.First(), null);
        var bundle = opened.Data;
        attempt.Bundle = bundle;
        // ARV-092b: every decoded part, the floor plan files and the JSON parts, scanned before anything reads, plans or keeps
        // it, under one deadline for the whole bundle.
        var screened = await screening.ScreenAllAsync(Ariva.Core.Domain.Enums.UploadKind.SiteConfiguration,
            bundle.Parts.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value), ct);
        if (screened.HasErrors)
            return await RefuseImportAsync(siteCode, screened.ErrorMessages.First(), bundle);

        var content = SiteConfigurationParts.Read(bundle);
        if (content.HasErrors)
            return await RefuseImportAsync(siteCode, content.ErrorMessages.First(), bundle);
        var valid = await SiteConfigurationRules.Content().ValidateAllAsync(content.Data);
        if (valid.HasErrors)
            return await RefuseImportAsync(siteCode, valid.ErrorMessages.FirstOrDefault() ?? SiteConfigurationErrors.InvalidTopology, bundle);

        // Airports are reference data: the bundle's (or the one the caller names) must exist here; none is created.
        List<string> codes = airportCode is null ? [.. content.Data.Topology.Terminals.Select(t => t.Airport).Distinct(StringComparer.Ordinal)] : [airportCode];
        var airports = await Query<Airport>().Where(a => codes.Contains(a.IataCode) && a.DeletedOn == null).ToListAsync(ct);
        if (airports.Count != codes.Count)
            return await RefuseImportAsync(siteCode, SiteConfigurationErrors.UnknownAirport, bundle);

        // The plan and its floor plans need no lock (the bundle and the airports alone); their refusals still come in the order below.
        var planned = SiteConfigurationTransfer.Plan(siteCode, content.Data, airports.ToDictionary(a => a.IataCode, StringComparer.Ordinal), airportCode);
        var (floorPlans, floorPlanRefusal) = planned.HasErrors ? (null, null) : FloorPlansOf(planned.Data);
        // ARV-294 (CWE-400): the plans' files before the import's transaction takes the site's row, so no transaction and no lock waits
        // on the disk; every refusal below rolls back, which removes them.
        if (floorPlans is not null)
            await StoreFilesAsync(floorPlans, ct);

        // Two imports of one site queue on its row; the second sees the first's records once it holds the lock. The wait is
        // bounded (ARV-250): an import holds the host's one upload permit while it waits, so past the wait it answers SiteBusy.
        if (!await TryLockSiteAsync(siteCode, SiteConfigurationLimits.SiteLockWait, ct))
            return await RefuseImportAsync(siteCode, SiteConfigurationErrors.SiteBusy, bundle);
        if (!await IsEmptyAsync(siteCode, ct))
            return await RefuseImportAsync(siteCode, SiteConfigurationErrors.SiteNotEmpty, bundle);

        if (planned.HasErrors)
            return await RefuseImportAsync(siteCode, planned.ErrorMessages.First(), bundle);
        var plan = planned.Data;

        // The rule service's role check: a role the caller does not hold is neither given nor taken, and a rule without an
        // owner (every role's) is an administrator's; Transfer is held by administrators, who pass it. ARV-181b2 (defence in
        // depth beside the entity's border checks): a rule on a border-only metric only from a caller who sees border data.
        if (plan.AlertRules.Count > 0)
        {
            var held = (await callerRoles.GetAsync(ct)).ToHashSet(StringComparer.Ordinal);
            if (!held.Contains(Ariva.Core.RoleCodes.SystemAdministrator) &&
                plan.AlertRules.Any(r => r.OwnerRole is null || !held.Contains(r.OwnerRole) || (r.EscalateToRole is not null && !held.Contains(r.EscalateToRole))))
                return await RefuseImportAsync(siteCode, SiteConfigurationErrors.InvalidAlertRules, bundle);
            if (plan.AlertRules.Any(r => BorderAlerts.IsBorderOnly(r.Metric)) && !BorderAlerts.Receives(held))
                return await RefuseImportAsync(siteCode, SiteConfigurationErrors.InvalidAlertRules, bundle);
        }

        // ARV-018 again (checked before the lock, above): each file typed by its bytes, SVG sanitised, rasters within their bounds,
        // the name without a path.
        if (floorPlanRefusal is not null)
            return await RefuseImportAsync(siteCode, floorPlanRefusal, bundle);

        // Display codes are unique among the live displays of the deployment, not the site (script 0035). Checked after the
        // bundle's checks above and before anything is written; the registration path's own device rules run later, after
        // the records and files are written, so a bundle with a taken code and a device it refuses answers this conflict.
        var displayCodes = plan.Displays.Select(d => d.Code).ToList();
        if (displayCodes.Count > 0 && await Query<Display>().AnyAsync(d => displayCodes.Contains(d.Code) && d.DeletedOn == null, ct))
            return await RefuseImportAsync(siteCode, SiteConfigurationErrors.DisplayCodeTaken, bundle);

        var credentials = new List<SiteConfigurationDeviceCredentialViewModel>();
        var linesOutOfReach = new List<string>();
        try
        {
            await SaveAsync(plan, floorPlans, ct);
            // Constraint violations surface here, inside the service, rather than at the commit; the registration path below
            // finds the draft's zones in the database.
            await FlushAsync(ct);

            // ARV-122b: each device through the registration path itself (its rules, its credential issuing, its audit entry),
            // in this transaction: Commissioning, a new credential shown once.
            var registeredDevices = new List<DeviceViewModel>(plan.Devices.Count);
            foreach (var registration in plan.Devices)
            {
                var registered = await devices.RegisterAsync(registration, ct);
                if (registered.HasErrors)
                    return await RefuseImportAsync(siteCode, SiteConfigurationErrors.InvalidDevices, bundle);
                credentials.Add(new SiteConfigurationDeviceCredentialViewModel(registered.Data.Device.Code, registered.Data.Credential));
                registeredDevices.Add(registered.Data.Device);
            }

            await FlushAsync(ct);

            // ARV-276: each device's line declaration through the registry's own path (its checks against the imported draft, its
            // audit entry); a device that counts every line keeps the registration's default. A line its footprint does not reach
            // is a warning, as when the lines are declared by hand: the device is listed in the answer, the declaration kept.
            for (var row = 0; row < plan.DeviceLines.Count; row++)
            {
                if (string.Equals(plan.DeviceLines[row].Mode, nameof(DeviceLineCoverage.All), StringComparison.Ordinal))
                    continue;
                var covered = await devices.SetCoverageAsync(registeredDevices[row].Id, plan.DeviceLines[row], ct);
                // Defence in depth: the plan has checked the lines against the same draft, so the registry refuses none here.
                if (covered.HasErrors)
                    return await RefuseImportAsync(siteCode, SiteConfigurationErrors.DeviceLineNotInProfile(row + 1), bundle);
                if (covered.Data.Warnings.Count > 0)
                    linesOutOfReach.Add(registeredDevices[row].Code);
            }

            await FlushAsync(ct);
        }
        catch (global::NHibernate.Exceptions.GenericADOException e) when (e.InnerException is global::Npgsql.PostgresException { SqlState: "23505" } unique)
        {
            // A terminal or display code taken meanwhile, or a record created at the site meanwhile by another endpoint.
            var error = unique.ConstraintName switch
            {
                "ux_terminal_airport_code" => SiteConfigurationErrors.TerminalTaken,
                "ux_display_code" => SiteConfigurationErrors.DisplayCodeTaken,
                _ => SiteConfigurationErrors.SiteNotEmpty
            };
            return await RefuseImportAsync(siteCode, error, bundle);
        }

        await audit.RecordAsync($"{Target}.Imported", "Site", null, siteCode, null,
            Summary("imported", null, bundle.BundleSha256, bundle.FormatVersion, bundle.SourceSite, bundle.Parts.Count, plan.Counts), ct);
        RegisterPostCommitAction(() => cache.RemoveByTagAsync(SvcTopology.CacheTag).AsTask());
        // The commit comes after this answer (the unit of work's filter). Should it fail, or anything after this roll the
        // import back, the Imported entry goes with it; the unit of work then runs its post-rollback actions, and this one
        // records the import as failed in a transaction of its own (a failure to write it is logged by the unit of work).
        var rolledBack = Summary("failed", RolledBack, bundle.BundleSha256, bundle.FormatVersion, bundle.SourceSite, bundle.Parts.Count, null);
        RegisterPostRollbackAction(() => WriteOwnAuditAsync($"{Target}.ImportFailed", siteCode, rolledBack));
        var notes = new List<string> { plan.Draft is null ? SiteConfigurationErrors.BundleCarriesNoProfile : SiteConfigurationErrors.ProfileArrivesAsDraft };
        if (plan.Devices.Count > 0)
            notes.Add(SiteConfigurationErrors.DevicesArriveCommissioning);
        if (plan.DevicesNeedingAccessSettings.Count > 0)
            notes.Add(SiteConfigurationErrors.AccessSettingsWithheld);
        if (plan.Displays.Count > 0)
            notes.Add(SiteConfigurationErrors.DisplaysArriveUnpaired);
        if (plan.AlertRules.Count > 0 || plan.Displays.Count > 0)
            notes.Add(SiteConfigurationErrors.RulesAndDisplaysWaitForPublication);
        if (plan.RulesNeedingEscalationContact.Count > 0)
            notes.Add(SiteConfigurationErrors.EscalationContactsWithheld);
        if (linesOutOfReach.Count > 0)
            notes.Add(SiteConfigurationErrors.DeviceLinesOutOfReach);
        return new Result<SiteConfigurationImportViewModel>(new SiteConfigurationImportViewModel(bundle.BundleSha256, bundle.SourceSite, plan.Draft?.Id,
            plan.PortableGeometryHash, plan.Counts, credentials, plan.DevicesNeedingAccessSettings, plan.RulesNeedingEscalationContact, notes, linesOutOfReach));
    }

    /// <summary>
    /// ARV-018 again: each plan's file typed by its bytes, SVG sanitised, rasters within their bounds, the name without a path; the
    /// plans with the bytes to store, or the refusal.
    /// </summary>
    private static (List<(FloorPlan Plan, byte[] Bytes)> Plans, string Refusal) FloorPlansOf(SiteConfigurationPlan plan)
    {
        var floorPlans = new List<(FloorPlan Plan, byte[] Bytes)>();
        foreach (var item in plan.FloorPlans)
        {
            var file = FloorPlanFiles.Inspect(item.File);
            var fileName = SvcFloorPlans.SafeFileName(item.Row.FileName);
            if (file is null || fileName is null)
                return (null, SiteConfigurationErrors.FloorPlanFileRefused);
            try
            {
                floorPlans.Add((new FloorPlan(item.Level, $"{Guid.CreateVersion7():N}.{file.Extension}", file.ContentType, file.Bytes.Length,
                    Convert.ToHexStringLower(SHA256.HashData(file.Bytes)), fileName, file.WidthPixels, file.HeightPixels,
                    item.Row.MetresPerPixel, item.Row.OriginX, item.Row.OriginY), file.Bytes));
            }
            catch (ArgumentException)
            {
                return (null, SiteConfigurationErrors.InvalidFloorPlans);
            }
        }

        return (floorPlans, null);
    }

    /// <summary>
    /// ARV-294: each plan's file under its own new key, outside any transaction, each removed when the import rolls back (a refusal, a
    /// failure, a failed commit).
    /// </summary>
    private async Task StoreFilesAsync(List<(FloorPlan Plan, byte[] Bytes)> floorPlans, CancellationToken ct)
    {
        foreach (var (floorPlan, bytes) in floorPlans)
        {
            var key = floorPlan.StorageKey;
            RegisterPostRollbackAction(() => storage.DeleteAsync(key));
            await using var content = new MemoryStream(bytes, writable: false);
            await storage.SaveAsync(key, content, ct);
        }
    }

    /// <summary>Parents before children; queue zones before the zones that hang off them; each file stored before the import's transaction (<see cref="StoreFilesAsync"/>).</summary>
    private async Task SaveAsync(SiteConfigurationPlan plan, List<(FloorPlan Plan, byte[] Bytes)> floorPlans, CancellationToken ct)
    {
        foreach (var terminal in plan.Terminals)
            await SaveAsync(terminal, ct);
        foreach (var level in plan.Levels)
            await SaveAsync(level, ct);
        foreach (var checkpoint in plan.Checkpoints)
            await SaveAsync(checkpoint, ct);
        foreach (var desk in plan.Desks)
            await SaveAsync(desk, ct);
        foreach (var mapping in plan.DeskCodeMappings)
            await SaveAsync(mapping, ct);
        if (plan.Draft is { } draft)
        {
            await SaveAsync(draft, ct);
            foreach (var zone in draft.Zones.OrderBy(z => z.QueueZone is not null))
                await SaveAsync(zone, ct);
            foreach (var line in draft.Lines)
                await SaveAsync(line, ct);
        }

        foreach (var (floorPlan, _) in floorPlans)
            await SaveAsync(floorPlan, ct);

        // ARV-122b: alert rules without escalation contacts, displays unpaired (no credential).
        foreach (var rule in plan.AlertRules)
            await SaveAsync(rule, ct);
        foreach (var display in plan.Displays)
            await SaveAsync(display, ct);
    }

    /// <summary>
    /// Empty: no live terminal, level, checkpoint, desk, floor plan, device, desk code mapping, alert rule or display, and no
    /// zone profile of any status.
    /// </summary>
    private async Task<bool> IsEmptyAsync(string siteCode, CancellationToken ct) =>
        !await Query<Terminal>().AnyAsync(t => t.SiteCode == siteCode && t.DeletedOn == null, ct) &&
        !await Query<Level>().AnyAsync(l => l.SiteCode == siteCode && l.DeletedOn == null, ct) &&
        !await Query<Checkpoint>().AnyAsync(c => c.SiteCode == siteCode && c.DeletedOn == null, ct) &&
        !await Query<Desk>().AnyAsync(d => d.SiteCode == siteCode && d.DeletedOn == null, ct) &&
        !await Query<ZoneProfile>().AnyAsync(p => p.SiteCode == siteCode, ct) &&
        !await Query<FloorPlan>().AnyAsync(p => p.SiteCode == siteCode && p.DeletedOn == null, ct) &&
        !await Query<Device>().AnyAsync(d => d.SiteCode == siteCode && d.DeletedOn == null, ct) &&
        !await Query<DeskCodeMapping>().AnyAsync(m => m.SiteCode == siteCode && m.DeletedOn == null, ct) &&
        !await Query<AlertRule>().AnyAsync(r => r.SiteCode == siteCode && r.DeletedOn == null, ct) &&
        !await Query<Display>().AnyAsync(d => d.SiteCode == siteCode && d.DeletedOn == null, ct);

    /// <summary>
    /// A refused (or failed) import: nothing it wrote is kept, and the outcome is audited in a transaction of its own (the
    /// import's is rolled back) with the bundle hash when the manifest checked out, the counts never.
    /// </summary>
    private async Task<Result<SiteConfigurationImportViewModel>> RefuseImportAsync(string siteCode, string error, OpenedSiteBundle bundle, bool failed = false)
    {
        // ARV-294 (CWE-400): the import's transaction ends first (rolled back, with the site's row and the files it stored), so neither
        // it nor its locks wait while the audit entry's own connection opens and writes.
        await RollBackBeforeOwnAuditAsync(logger);

        try
        {
            await WriteOwnAuditAsync(failed ? $"{Target}.ImportFailed" : $"{Target}.ImportRefused", siteCode,
                Summary(failed ? "failed" : "refused", error, bundle?.BundleSha256, bundle?.FormatVersion, bundle?.SourceSite, bundle?.Parts.Count ?? 0, null));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The refusal is what the caller sees; the audit failure is logged beside it, never silent.
            logger.LogError(e, "The audit entry of a refused or failed site configuration import of {Site} could not be written", siteCode);
        }

        return Result.Error<SiteConfigurationImportViewModel>(error);
    }

    /// <summary>
    /// An audit entry in a transaction of its own, as this request's caller (the new scope is not the request's). It
    /// throws when it cannot be written; each caller logs that.
    /// </summary>
    private async Task WriteOwnAuditAsync(string action, string siteCode, string summary)
    {
        await using var scope = scopes.CreateAsyncScope();
        var auditUnitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        try
        {
            await new AuditTrail(auditUnitOfWork, CurrentUser, TimeProvider).RecordAsync(action, "Site", null, siteCode, null, summary, CancellationToken.None);
            auditUnitOfWork.PromiseToCommit();
            await auditUnitOfWork.EndAsync(CancellationToken.None);
        }
        catch
        {
            await auditUnitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>What an import has learnt so far, for the audit entry of a failure.</summary>
    private sealed class ImportAttempt
    {
        public OpenedSiteBundle Bundle { get; set; }
    }

    #endregion

    #region Helpers

    private async Task<bool> ReachableAsync(string siteCode, CancellationToken ct) =>
        siteCode is not null && (await siteScope.GetAsync(ct)).Allows(siteCode) && await Query<Site>().AnyAsync(s => s.Code == siteCode, ct);

    /// <summary>
    /// The audit entry's summary: the outcome, a refusal's fixed text, the bundle hash, the envelope's format version and
    /// source site (both checked before they are recorded), the number of parts and the counts. Never a part's content.
    /// </summary>
    private static string Summary(string outcome, string reason, string bundleSha256, int? formatVersion, string sourceSite, int parts, SiteConfigurationCountsViewModel counts) =>
        JsonSerializer.Serialize(new { outcome, reason, bundleSha256, formatVersion, sourceSite, parts, counts }, JsonSerializerOptions.Web);

    #endregion
}
