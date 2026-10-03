using Ariva.Core;
using Ariva.Core.Security;
using Ariva.Core.Services.Border;
using Ariva.Infra.Services.Border;
using FluentAssertions;

namespace Ariva.UnitTests.Live;

/// <summary>
/// ARV-057: a hall's figures from AMAN's interval aggregates. Means are weighted by the people processed; desks staffed
/// come from the latest session events; e-gate utilisation is the used gates' busy time over the configured gates' time;
/// the extra manual load is each reject at a manual desk's mean service time. Desk and gate rows only for border roles.
/// </summary>
public sealed class ImmigrationViewTests
{
    private static readonly DateTime Minute = new(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);

    private static SvcImmigrationView.DeskIntervalRow Interval(string desk, string lane, int minute, int transactions, double service, double p90 = 0, double cycle = 0) =>
        new() { Kind = "Immigration", Desk = desk, Lane = lane, Start = Minute.AddMinutes(minute), Transactions = transactions, MeanService = service, P90Service = p90, MeanCycle = cycle };

    private static SvcImmigrationView.SessionRow Session(string desk, string state, string lane) => new() { Kind = "Immigration", Desk = desk, State = state, Lane = lane };

    private static SvcImmigrationView.GateRow Gate(string gate, int attempts, int rejected, int documentRead, int technical, double busy) =>
        new()
        {
            Kind = "Immigration", Gate = gate, Attempts = attempts, Accepted = attempts - rejected, Rejected = rejected, DocumentRead = documentRead, Technical = technical,
            Other = rejected - documentRead - technical, BusySeconds = busy
        };

    private static ImmigrationHallViewModel Hall(bool border) => SvcImmigrationView.Hall("Immigration",
        [
            Interval("AR-02", "CIT", 0, 4, 40, 70, 50), Interval("AR-02", "CIT", 1, 6, 50, 90, 60),
            Interval("AR-08", "VIS", 0, 2, 120, 200, 140), Interval("AR-09", "VIS", 0, 0, 0)
        ],
        [Session("AR-02", "Opened", "CIT"), Session("AR-08", "Opened", "VIS"), Session("AR-09", "Paused", "VIS"), Session("AR-10", "Closed", null)],
        [Gate("AG-1", 30, 6, 3, 3, 900), Gate("AG-2", 0, 0, 0, 0, 0)],
        6, border);

    [Fact]
    public void Hall_Should_WeighMeansByPeopleAndCountStaffedDesks_When_IntervalsAndSessionsArrive()
    {
        var hall = Hall(border: true);

        hall.Lanes.Select(l => l.Lane).Should().Equal("CIT", "VIS");
        hall.Lanes[0].Should().Be(new ImmigrationLaneViewModel("CIT", 1, 0, 10, 46, 90, 56));
        hall.Lanes[1].Should().Be(new ImmigrationLaneViewModel("VIS", 1, 1, 2, 120, 200, 140), "an interval with nobody processed adds no P90");
    }

    [Fact]
    public void Lanes_Should_LeaveOutTimesFromFewerThanThreeDesks_When_TheCallerHasNoDeskData()
    {
        // One or two desks' times are those desks' own figures: a duty manager gets the counts only.
        var airport = Hall(border: false);
        airport.Lanes[0].Should().Be(new ImmigrationLaneViewModel("CIT", 1, 0, 10, null, null, null));
        airport.Lanes[1].Should().Be(new ImmigrationLaneViewModel("VIS", 1, 1, 2, null, null, null));

        var three = SvcImmigrationView.Hall("Immigration",
            [Interval("AR-02", "CIT", 0, 2, 40), Interval("AR-03", "CIT", 0, 2, 60), Interval("AR-04", "CIT", 0, 1, 50)], [], [], 0, false);
        three.Lanes[0].MeanServiceSeconds.Should().Be(50, "three desks make a lane figure");
    }

    [Fact]
    public void EGates_Should_GiveUtilisationRejectsAndTheExtraManualLoad_When_GatesReported()
    {
        var gates = Hall(border: false).EGates;

        gates.Should().Match<ImmigrationEGatesViewModel>(g => g.GatesConfigured == 6 && g.GatesUsed == 1 && g.Attempts == 30 && g.Accepted == 24 && g.Rejected == 6);
        gates.RejectRate.Should().Be(0.2);
        gates.Utilisation.Should().Be(Math.Round(900 / (6 * 900.0), 3), "busy seconds over six gates for fifteen minutes");
        gates.Rejects.Should().Be(new ImmigrationRejectsViewModel(3, 0, 0, 0, 3, 0));
        // Manual mean service over the window: (4*40 + 6*50 + 2*120) / 12 = 58.3 s; six rejects take 5.8 desk-minutes,
        // spread over the two desks open now.
        gates.ExtraManualDeskMinutes.Should().Be(Math.Round(6 * 58.3 / 60, 1));
        gates.ExtraManualWaitMinutes.Should().Be(Math.Round(gates.ExtraManualDeskMinutes / 2, 1));
    }

    [Fact]
    public void Desks_Should_BeListedForBorderRolesOnly_When_TheHallIsBuilt()
    {
        var airport = Hall(border: false);
        airport.Desks.Should().BeEmpty("a terminal duty manager sees lane aggregates only");
        airport.Gates.Should().BeEmpty();

        var border = Hall(border: true);
        border.Desks.Select(d => (d.Desk, d.Lane, d.State, d.Transactions)).Should().Equal(
            ("AR-02", "CIT", "Opened", 10), ("AR-08", "VIS", "Opened", 2), ("AR-09", "VIS", "Paused", 0), ("AR-10", null, "Closed", 0));
        border.Desks[0].LastIntervalUtc.Should().Be(Minute.AddMinutes(1));
        border.Gates.Select(g => (g.Gate, g.Attempts, g.Rejected, g.Utilisation, g.MeanCycleSeconds)).Should().Equal(("AG-1", 30, 6, 1.0, 30.0), ("AG-2", 0, 0, 0.0, null));
    }

    [Fact]
    public void Immigration_Should_BeForBorderRolesAndDutyManagers_When_RolesAreSeeded()
    {
        var permission = Global.Defaults.Permissions.ViewImmigration;
        RolePermissions.BorderShiftSupervisor.Should().Contain(permission);
        RolePermissions.TerminalDutyManager.Should().Contain(permission, "duty managers see the border's lane aggregates");
        RolePermissions.SystemAdministrator.Should().Contain(permission);
        RolePermissions.HandlerStationManager.Should().NotContain(permission, "handlers do not see immigration");
        permission.Code.Should().Be("Immigration.View");
    }
}
