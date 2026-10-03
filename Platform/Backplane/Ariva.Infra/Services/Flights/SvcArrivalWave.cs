using Ariva.Core.Flights;
using Ariva.Core.Security;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Flights;
using Ariva.Core.Services.Flights;
using Ariva.Core.Services.Topology;
using Ariva.Infra.Services.Foundation;

namespace Ariva.Infra.Services.Flights;

/// <summary>
/// The arrival-wave projection (ARV-047, <see cref="ISvcArrivalWave"/>): the site's arriving legs
/// (<see cref="ArrivingLegs"/>) projected with <see cref="ArrivalWave"/>.
/// </summary>
internal sealed class SvcArrivalWave(IUnitOfWork unitOfWork, ICurrentUser currentUser, TimeProvider timeProvider, ArrivalWaveSettings settings,
    ISiteScope siteScope, CallerRoles callerRoles, Ariva.Core.Border.EgateCouplingSettings coupling) : SvcBase(unitOfWork, currentUser, timeProvider), ISvcArrivalWave
{
    public async Task<Result<ArrivalWaveViewModel>> GetAsync(string siteCode, int windowMinutes, CancellationToken ct = default)
    {
        if (windowMinutes is < ArrivalWave.MinWindow or > ArrivalWave.MaxWindow)
            return Result.Error<ArrivalWaveViewModel>($"minutes is {ArrivalWave.MinWindow} to {ArrivalWave.MaxWindow}.");
        if (siteCode is null || !(await siteScope.GetAsync(ct)).Allows(siteCode) || (await ExecuteSqlAsync<CodeRow>("""SELECT code AS "Code" FROM site WHERE code = :site""",
                new Dictionary<string, object> { ["site"] = siteCode }, ct)).Count == 0)
            return Result.Error<ArrivalWaveViewModel>(TopologyErrors.NotFound);

        var lanes = RolePermissions.For(await callerRoles.GetAsync(ct)).Contains(Ariva.Core.Global.Defaults.Permissions.ViewArrivalWaveLanes);
        var now = UtcNow;
        var flights = await ArrivingLegs.ReadAsync(UnitOfWork.StorageProvider, siteCode, now, windowMinutes, settings, ct);
        var projection = ArrivalWave.Project(flights, now, windowMinutes, settings);
        var gates = lanes ? await EgateRejects.ReadAsync(UnitOfWork.StorageProvider, siteCode, now, coupling, ct) : null;
        return new Result<ArrivalWaveViewModel>(new ArrivalWaveViewModel(siteCode, now, projection.WindowMinutes, projection.DelayMinutes,
            projection.Flights.Select(f => View(f, lanes)).ToList(),
            projection.Minutes.Select(m => new ArrivalWaveMinuteViewModel(m.MinuteUtc, LaneCountsViewModel.Of(m.Lanes, lanes))).ToList(),
            LaneCountsViewModel.Of(projection.AlertWindow, lanes), projection.FlightsWithoutPassengers, flights.Count >= ArrivingLegs.MaxLegs,
            gates is null ? null : Math.Round(gates.Rate, 4), gates?.Measured, gates is null ? null : coupling.RejectLane));
    }

    private static ArrivalWaveFlightViewModel View(FlightWave wave, bool lanes) =>
        new(wave.Flight.FlightKey, wave.Flight.Carrier + wave.Flight.Number + wave.Flight.Suffix, wave.Flight.Origin, wave.Flight.Terminal, wave.Flight.Stand,
            wave.Flight.ScheduledUtc, wave.InBlockUtc, wave.InBlockSource.ToString(), wave.Landed, wave.Passengers is { } p ? Math.Round(p, 2) : null,
            wave.PassengerSource?.ToString(), lanes ? wave.LaneSource?.ToString() : null, LaneCountsViewModel.Of(wave.Lanes, lanes), wave.HallFirstUtc, wave.HallLastUtc);

    private sealed class CodeRow
    {
        public string Code { get; set; }
    }
}
