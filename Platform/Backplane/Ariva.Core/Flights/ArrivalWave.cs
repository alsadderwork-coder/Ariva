namespace Ariva.Core.Flights;

/// <summary>Where a flight's in-block time T_f comes from (F14): the actual on-block, a landing plus taxi-in, the estimate, or the schedule.</summary>
public enum InBlockSource
{
    OnBlock,
    Landed,
    Estimated,
    Scheduled
}

/// <summary>Where a flight's arriving passengers P_f come from (F14).</summary>
public enum PassengerSource
{
    /// <summary>AMAN's boarded total (<c>InboundFlightLaneDemand</c>).</summary>
    Aman,

    /// <summary>The flight feed's passenger estimate.</summary>
    PaxEstimate,

    /// <summary>Seats times the load factor.</summary>
    Seats
}

/// <summary>Where a flight's lane split comes from (F14): AMAN's lane demand, or the site's default mix.</summary>
public enum LaneSplitSource
{
    Aman,
    DefaultMix
}

/// <summary>Passengers per arrival lane category: CIT, RES, VIS and CRW at manual desks, and e-gate eligible.</summary>
public sealed record LaneCounts(double Cit, double Res, double Vis, double Crw, double EGate)
{
    public static LaneCounts Zero { get; } = new(0, 0, 0, 0, 0);

    public double Total => Cit + Res + Vis + Crw + EGate;

    public LaneCounts Plus(LaneCounts other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(Cit + other.Cit, Res + other.Res, Vis + other.Vis, Crw + other.Crw, EGate + other.EGate);
    }

    public LaneCounts Times(double factor) => new(Cit * factor, Res * factor, Vis * factor, Crw * factor, EGate * factor);

    /// <summary>Each count rounded to <paramref name="digits"/> decimals, for an answer.</summary>
    public LaneCounts Rounded(int digits = 2) =>
        new(Math.Round(Cit, digits), Math.Round(Res, digits), Math.Round(Vis, digits), Math.Round(Crw, digits), Math.Round(EGate, digits));
}

/// <summary>
/// The default lane mix of arriving passengers (F14, used without AMAN's lane demand): shares of crew, citizens,
/// residents, visitors and transfers (summing to 1), and the e-gate share of eligible citizens and residents.
/// </summary>
public sealed record LaneMix(double Cit, double Res, double Vis, double Crw, double Trf, double EGateShare)
{
    /// <summary>The reference mix (formulas.md F14): CIT 0.35, RES 0.20, VIS 0.35, CRW 0.02, TRF 0.08, e-gate 0.40.</summary>
    public static LaneMix Reference { get; } = new(0.35, 0.20, 0.35, 0.02, 0.08, 0.40);

    public IEnumerable<string> Problems(string name)
    {
        double[] shares = [Cit, Res, Vis, Crw, Trf, EGateShare];
        if (shares.Any(s => !double.IsFinite(s) || s is < 0 or > 1))
            yield return $"{name}: every share is 0 to 1.";
        else if (Math.Abs(Cit + Res + Vis + Crw + Trf - 1) > 1e-6)
            yield return $"{name}: Cit, Res, Vis, Crw and Trf add up to 1.";
    }

    /// <summary>The lane split of <paramref name="passengers"/>: transfers removed, the e-gate share taken from citizens and residents.</summary>
    public LaneCounts Split(double passengers) =>
        new(passengers * Cit * (1 - EGateShare), passengers * Res * (1 - EGateShare), passengers * Vis, passengers * Crw, passengers * (Cit + Res) * EGateShare);
}

/// <summary>The arrival-wave settings (<c>Flights:ArrivalWave</c>, F14): delay, taxi-in, load factor and the default mix.</summary>
public sealed record ArrivalWaveSettings
{
    public const string SectionName = "Flights:ArrivalWave";

    /// <summary>The reference settings (F14): delay 11, taxi-in 5, load factor 0.8, the reference mix.</summary>
    public static ArrivalWaveSettings Default { get; } = new();

    /// <summary>d_f: on-block to the first arrival at the hall, 8 to 15 minutes (reference midpoint 11 when unknown).</summary>
    public int DelayMinutes { get; init; } = 11;

    /// <summary>Landing to on-block, for a flight that has landed and has no on-block yet (Proposed: 5).</summary>
    public int TaxiInMinutes { get; init; } = 5;

    /// <summary>Seats times this when a flight has no passenger figure (Proposed: 0.8).</summary>
    public double LoadFactor { get; init; } = 0.8;

    public LaneMix Mix { get; init; } = LaneMix.Reference;

    public IEnumerable<string> Problems()
    {
        if (DelayMinutes is < ArrivalWave.MinDelay or > ArrivalWave.MaxDelay)
            yield return $"{SectionName}:DelayMinutes is {ArrivalWave.MinDelay} to {ArrivalWave.MaxDelay}.";
        if (TaxiInMinutes is < 0 or > 60)
            yield return $"{SectionName}:TaxiInMinutes is 0 to 60.";
        if (!double.IsFinite(LoadFactor) || LoadFactor is <= 0 or > 1)
            yield return $"{SectionName}:LoadFactor is above 0 and at most 1.";
        if (Mix is null)
            yield return $"{SectionName}:Mix is required.";
        else
            foreach (var problem in Mix.Problems($"{SectionName}:Mix"))
                yield return problem;
    }
}

/// <summary>AMAN's lane demand for one inbound flight (<c>InboundFlightLaneDemand</c>, as stored by ARV-048).</summary>
public sealed record AmanLaneDemand(int BoardedTotal, int Cit, int Res, int Vis, int Crw, int EGateEligible, DateTime ComputedUtc);

/// <summary>An arriving leg as the projection needs it: its milestones, passenger figures and AMAN's lane demand when known.</summary>
public sealed record ArrivingFlight(
    string FlightKey,
    string Carrier,
    string Number,
    string Suffix,
    string Origin,
    string Terminal,
    string Stand,
    DateTime ScheduledUtc,
    DateTime? EstimatedUtc,
    DateTime? LandedUtc,
    DateTime? OnBlockUtc,
    int? Seats,
    int? PaxEstimate,
    AmanLaneDemand Aman);

/// <summary>One flight's part of the wave: in-block, passengers, lane split and when it reaches the hall; or why it has no passengers.</summary>
public sealed record FlightWave(
    ArrivingFlight Flight,
    DateTime InBlockUtc,
    InBlockSource InBlockSource,
    bool Landed,
    double? Passengers,
    PassengerSource? PassengerSource,
    LaneSplitSource? LaneSource,
    LaneCounts Lanes,
    DateTime HallFirstUtc,
    DateTime HallLastUtc);

/// <summary>Hall arrivals per lane category in one minute (F14's A_l(t)).</summary>
public sealed record MinuteDemand(DateTime MinuteUtc, LaneCounts Lanes);

/// <summary>
/// The arrival-wave projection: the flights whose passengers reach the hall from this minute on (landing within the
/// window, or landed and still arriving), the hall arrivals per minute and lane, and the alert window's sum.
/// </summary>
public sealed record ArrivalWaveProjection(
    DateTime NowUtc,
    int WindowMinutes,
    int DelayMinutes,
    IReadOnlyList<FlightWave> Flights,
    IReadOnlyList<MinuteDemand> Minutes,
    LaneCounts AlertWindow,
    int FlightsWithoutPassengers);

/// <summary>
/// The arrival hall curve (formulas.md F14, ARV-047): each arriving flight's passengers P_f, split by lane (AMAN's
/// lane demand when present, the default mix otherwise), reach the hall from T_f + d_f over 12 minutes with the
/// reference weights omega. Pure: no I/O, the time passed in.
/// </summary>
public static class ArrivalWave
{
    public const int MinDelay = 8;
    public const int MaxDelay = 15;
    public const int MinWindow = 5;
    public const int MaxWindow = 120;
    public const int DefaultWindow = 30;

    /// <summary>The alert window of F14: the 20 minutes from now + 5 to now + 25 (the minutes starting at now + 5 to now + 24).</summary>
    public const int AlertFrom = 5;

    public const int AlertTo = 25;

    /// <summary>omega_j for j = 0 to 11: the reference 12-minute spread, summing to 1.</summary>
    public static IReadOnlyList<double> Spread { get; } = [0.03, 0.06, 0.09, 0.11, 0.12, 0.12, 0.11, 0.10, 0.08, 0.07, 0.06, 0.05];

    /// <summary>T_f: the actual on-block; after landing without one, landing plus taxi-in; else the estimate; else the schedule.</summary>
    public static (DateTime At, InBlockSource Source) InBlock(ArrivingFlight flight, ArrivalWaveSettings settings)
    {
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(settings);
        if (flight.OnBlockUtc is { } onBlock)
            return (onBlock, InBlockSource.OnBlock);
        if (flight.LandedUtc is { } landed)
        {
            var taxied = landed.AddMinutes(settings.TaxiInMinutes);
            // An estimate later than landing plus taxi-in is a better guess of the block time (a remote stand, a wait for a gate).
            return flight.EstimatedUtc is { } estimate && estimate > taxied ? (estimate, InBlockSource.Estimated) : (taxied, InBlockSource.Landed);
        }

        return flight.EstimatedUtc is { } estimated ? (estimated, InBlockSource.Estimated) : (flight.ScheduledUtc, InBlockSource.Scheduled);
    }

    /// <summary>P_f and the lane split: AMAN's when present, else the passenger estimate or seats times the load factor with the default mix.</summary>
    public static (double? Passengers, PassengerSource? Source, LaneSplitSource? Split, LaneCounts Lanes) Demand(ArrivingFlight flight, ArrivalWaveSettings settings)
    {
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(settings);
        if (flight.Aman is { } aman)
            return (aman.BoardedTotal, PassengerSource.Aman, LaneSplitSource.Aman, new LaneCounts(aman.Cit, aman.Res, aman.Vis, aman.Crw, aman.EGateEligible));
        if (flight.PaxEstimate is { } pax)
            return (pax, PassengerSource.PaxEstimate, LaneSplitSource.DefaultMix, settings.Mix.Split(pax));
        if (flight.Seats is { } seats)
        {
            var passengers = seats * settings.LoadFactor;
            return (passengers, PassengerSource.Seats, LaneSplitSource.DefaultMix, settings.Mix.Split(passengers));
        }

        return (null, null, null, LaneCounts.Zero);
    }

    /// <summary>
    /// The projection at <paramref name="now"/>: flights with T_f at most <paramref name="windowMinutes"/> ahead whose
    /// last hall minute is not yet past, and the hall arrivals per minute from this minute until the last of them.
    /// T_f is taken to the minute (floored), so each passenger counts in exactly one minute.
    /// </summary>
    public static ArrivalWaveProjection Project(IEnumerable<ArrivingFlight> flights, DateTime now, int windowMinutes, ArrivalWaveSettings settings)
    {
        ArgumentNullException.ThrowIfNull(flights);
        ArgumentNullException.ThrowIfNull(settings);
        if (windowMinutes is < MinWindow or > MaxWindow)
            throw new ArgumentOutOfRangeException(nameof(windowMinutes), $"The window is {MinWindow} to {MaxWindow} minutes.");
        var minute = Floor(now);
        var horizon = windowMinutes + settings.DelayMinutes + Spread.Count;
        var curve = new LaneCounts[horizon];
        Array.Fill(curve, LaneCounts.Zero);
        var waves = new List<FlightWave>();
        var withoutPassengers = 0;
        foreach (var flight in flights)
        {
            if (flight is null)
                continue;
            var (inBlock, source) = InBlock(flight, settings);
            var first = Floor(inBlock).AddMinutes(settings.DelayMinutes);
            var last = first.AddMinutes(Spread.Count - 1);
            if (inBlock > now.AddMinutes(windowMinutes) || last < minute)
                continue;
            var (passengers, passengerSource, split, lanes) = Demand(flight, settings);
            waves.Add(new FlightWave(flight, inBlock, source, flight.OnBlockUtc is not null || flight.LandedUtc is not null, passengers, passengerSource, split,
                lanes, first, last));
            if (passengers is null)
            {
                withoutPassengers++;
                continue;
            }

            for (var j = 0; j < Spread.Count; j++)
            {
                var index = (int)(first.AddMinutes(j) - minute).TotalMinutes;
                if (index >= 0 && index < horizon)
                    curve[index] = curve[index].Plus(lanes.Times(Spread[j]));
            }
        }

        var minutes = curve.Select((lanes, i) => new MinuteDemand(minute.AddMinutes(i), lanes)).ToList();
        var alert = minutes.Skip(AlertFrom).Take(AlertTo - AlertFrom).Aggregate(LaneCounts.Zero, (sum, m) => sum.Plus(m.Lanes));
        return new ArrivalWaveProjection(now, windowMinutes, settings.DelayMinutes,
            waves.OrderBy(w => w.InBlockUtc).ThenBy(w => w.Flight.FlightKey, StringComparer.Ordinal).ToList(), minutes, alert, withoutPassengers);
    }

    private static DateTime Floor(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
}
