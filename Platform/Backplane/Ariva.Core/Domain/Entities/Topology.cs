using System.Text.RegularExpressions;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// Codes in the topology (ARV-013): upper case letters, digits and hyphens, never changed after creation, unique among
/// the live (not deleted) children of one parent.
/// </summary>
public static partial class TopologyCodes
{
    /// <summary>Terminal, level, checkpoint and desk codes: 1 to 16 upper case letters, digits or inner hyphens.</summary>
    public static bool IsValid(string code) => code is not null && Code().IsMatch(code);

    /// <summary>An IATA airport code: three upper case letters.</summary>
    public static bool IsIata(string code) => code is not null && Iata().IsMatch(code);

    /// <summary>An ICAO airport code: four upper case letters.</summary>
    public static bool IsIcao(string code) => code is not null && Icao().IsMatch(code);

    internal static string Require(string code, string paramName)
    {
        if (!IsValid(code))
            throw new ArgumentException("A code is 1 to 16 upper case letters or digits, with single hyphens inside.", paramName);
        return code;
    }

    internal static string RequireName(string name, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, paramName);
        var trimmed = name.Trim();
        if (trimmed.Length > 200)
            throw new ArgumentException("A name is at most 200 characters.", paramName);
        return trimmed;
    }

    internal static void RequireUnique<T>(IEnumerable<T> live, Func<T, string> codeOf, string code, string what)
    {
        if (live.Any(item => string.Equals(codeOf(item), code, StringComparison.Ordinal)))
            throw new InvalidOperationException($"{what} {code} already exists here.");
    }

    [GeneratedRegex("^(?=.{1,16}$)[A-Z0-9]+(-[A-Z0-9]+)*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Code();

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Iata();

    [GeneratedRegex("^[A-Z]{4}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Icao();
}

/// <summary>
/// A lane category code (glossary): the class of traveller a lane serves, site-configurable, 2 to 4 upper case
/// letters. Reference codes: CRW crew and diplomats, CIT citizens, RES residents, VIS visitors, EG e-gate eligible.
/// </summary>
public static partial class LaneCategory
{
    public const string Crew = "CRW";
    public const string Citizens = "CIT";
    public const string Residents = "RES";
    public const string Visitors = "VIS";
    public const string EGateEligible = "EG";

    public static readonly IReadOnlyList<string> Reference = [Crew, Citizens, Residents, Visitors, EGateEligible];

    public static bool IsValid(string code) => code is not null && Shape().IsMatch(code);

    [GeneratedRegex("^[A-Z]{2,4}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Shape();
}

/// <summary>
/// An airport of the deployment (ARV-013). Reference data identified by its IATA code; its terminals belong to sites.
/// </summary>
public class Airport : BaseSoftDeletableEntity<Airport>
{
    protected Airport()
    {
    }

    public Airport(string iataCode, string icaoCode, string name, string timeZoneId)
    {
        if (!TopologyCodes.IsIata(iataCode))
            throw new ArgumentException("An IATA code is three upper case letters.", nameof(iataCode));
        IataCode = iataCode;
        Update(icaoCode, name, timeZoneId);
    }

    public virtual string IataCode { get; protected set; }
    public virtual string IcaoCode { get; protected set; }
    public virtual string Name { get; protected set; }

    /// <summary>IANA time zone of the airport (for example Asia/Amman); local day boundaries and schedules use it.</summary>
    public virtual string TimeZoneId { get; protected set; }

    public virtual IList<Terminal> Terminals { get; protected set; } = [];

    public virtual void Update(string icaoCode, string name, string timeZoneId)
    {
        if (icaoCode is not null && !TopologyCodes.IsIcao(icaoCode))
            throw new ArgumentException("An ICAO code is four upper case letters.", nameof(icaoCode));
        if (string.IsNullOrWhiteSpace(timeZoneId) || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _))
            throw new ArgumentException("Unknown time zone.", nameof(timeZoneId));
        IcaoCode = icaoCode;
        Name = TopologyCodes.RequireName(name, nameof(name));
        TimeZoneId = timeZoneId;
    }

    /// <summary>Adds a terminal bound to a site; the code is unique among the airport's live terminals.</summary>
    public virtual Terminal AddTerminal(string code, string name, string siteCode)
    {
        if (IsDeleted)
            throw new InvalidOperationException("The airport is deleted.");
        TopologyCodes.Require(code, nameof(code));
        TopologyCodes.RequireUnique(Terminals.Where(t => !t.IsDeleted), t => t.Code, code, "Terminal");
        var terminal = new Terminal(this, code, name, siteCode);
        Terminals.Add(terminal);
        return terminal;
    }
}

/// <summary>A terminal: belongs to one airport and one site, which every record below it inherits (ISiteBound).</summary>
public class Terminal : BaseSoftDeletableEntity<Terminal>, ISiteBound
{
    protected Terminal()
    {
    }

    internal Terminal(Airport airport, string code, string name, string siteCode)
    {
        if (!Site.IsValidCode(siteCode))
            throw new ArgumentException("Unknown site code shape.", nameof(siteCode));
        Airport = airport;
        Code = code;
        Name = TopologyCodes.RequireName(name, nameof(name));
        SiteCode = siteCode;
    }

    public virtual Airport Airport { get; protected set; }
    public virtual string Code { get; protected set; }
    public virtual string Name { get; protected set; }
    public virtual string SiteCode { get; protected set; }
    public virtual IList<Level> Levels { get; protected set; } = [];

    public virtual void Rename(string name) => Name = TopologyCodes.RequireName(name, nameof(name));

    /// <summary>Adds a floor; codes are unique among the terminal's live levels, and so are floor numbers.</summary>
    public virtual Level AddLevel(string code, string name, int floorNumber, double widthMetres, double depthMetres)
    {
        if (IsDeleted)
            throw new InvalidOperationException("The terminal is deleted.");
        TopologyCodes.Require(code, nameof(code));
        var live = Levels.Where(l => !l.IsDeleted).ToList();
        TopologyCodes.RequireUnique(live, l => l.Code, code, "Level");
        if (live.Any(l => l.FloorNumber == floorNumber))
            throw new InvalidOperationException($"Floor {floorNumber} already exists in terminal {Code}.");
        var level = new Level(this, code, name, floorNumber, widthMetres, depthMetres);
        Levels.Add(level);
        return level;
    }
}

/// <summary>
/// A floor of a terminal with its extent in metres: the local coordinate system of zones and sensors runs from (0, 0)
/// to (width, depth), and zone coordinates must stay inside it (ARV-016).
/// </summary>
public class Level : BaseSoftDeletableEntity<Level>, ISiteBound
{
    public const int LowestFloor = -10;
    public const int HighestFloor = 50;
    public const double MaxExtentMetres = 2_000;

    protected Level()
    {
    }

    internal Level(Terminal terminal, string code, string name, int floorNumber, double widthMetres, double depthMetres)
    {
        Terminal = terminal;
        SiteCode = terminal.SiteCode;
        Code = code;
        Update(name, floorNumber, widthMetres, depthMetres);
    }

    public virtual Terminal Terminal { get; protected set; }
    public virtual string SiteCode { get; protected set; }
    public virtual string Code { get; protected set; }
    public virtual string Name { get; protected set; }
    public virtual int FloorNumber { get; protected set; }
    public virtual double WidthMetres { get; protected set; }
    public virtual double DepthMetres { get; protected set; }
    public virtual IList<Checkpoint> Checkpoints { get; protected set; } = [];

    public virtual void Update(string name, int floorNumber, double widthMetres, double depthMetres)
    {
        if (floorNumber is < LowestFloor or > HighestFloor)
            throw new ArgumentOutOfRangeException(nameof(floorNumber), floorNumber, $"A floor number is between {LowestFloor} and {HighestFloor}.");
        RequireExtent(widthMetres, nameof(widthMetres));
        RequireExtent(depthMetres, nameof(depthMetres));
        Name = TopologyCodes.RequireName(name, nameof(name));
        FloorNumber = floorNumber;
        WidthMetres = widthMetres;
        DepthMetres = depthMetres;
    }

    /// <summary>True when the floor point lies inside the level (edges included).</summary>
    public virtual bool Contains(double x, double y) => x >= 0 && y >= 0 && x <= WidthMetres && y <= DepthMetres;

    public virtual Checkpoint AddCheckpoint(string code, string name, CheckpointKind kind)
    {
        if (IsDeleted)
            throw new InvalidOperationException("The level is deleted.");
        TopologyCodes.Require(code, nameof(code));
        TopologyCodes.RequireUnique(Checkpoints.Where(c => !c.IsDeleted), c => c.Code, code, "Checkpoint");
        var checkpoint = new Checkpoint(this, code, name, kind);
        Checkpoints.Add(checkpoint);
        return checkpoint;
    }

    private static void RequireExtent(double metres, string paramName)
    {
        if (double.IsNaN(metres) || metres <= 0 || metres > MaxExtentMetres)
            throw new ArgumentOutOfRangeException(paramName, metres, $"An extent is more than 0 and at most {MaxExtentMetres} metres.");
    }
}

/// <summary>A process point on a level: check-in, security, emigration or immigration. Its kind decides which desks it has.</summary>
public class Checkpoint : BaseSoftDeletableEntity<Checkpoint>, ISiteBound
{
    protected Checkpoint()
    {
    }

    internal Checkpoint(Level level, string code, string name, CheckpointKind kind)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown checkpoint kind.");
        Level = level;
        SiteCode = level.SiteCode;
        Code = code;
        Kind = kind;
        Rename(name);
    }

    public virtual Level Level { get; protected set; }
    public virtual string SiteCode { get; protected set; }
    public virtual string Code { get; protected set; }
    public virtual string Name { get; protected set; }
    public virtual CheckpointKind Kind { get; protected set; }
    public virtual IList<Desk> Desks { get; protected set; } = [];

    /// <summary>True for emigration and immigration: border processes with lane categories and e-gates.</summary>
    public virtual bool IsBorder => Kind is CheckpointKind.Emigration or CheckpointKind.Immigration;

    public virtual void Rename(string name) => Name = TopologyCodes.RequireName(name, nameof(name));

    /// <summary>The desk kinds a checkpoint of this kind can have.</summary>
    public static IReadOnlySet<DeskKind> AllowedDeskKinds(CheckpointKind kind) => kind switch
    {
        CheckpointKind.CheckIn => new HashSet<DeskKind> { DeskKind.Counter },
        CheckpointKind.Security => new HashSet<DeskKind> { DeskKind.SecurityLane },
        CheckpointKind.Emigration or CheckpointKind.Immigration => new HashSet<DeskKind> { DeskKind.Desk, DeskKind.EGate },
        _ => new HashSet<DeskKind>()
    };

    public virtual Desk AddDesk(string code, string name, DeskKind kind, IEnumerable<string> laneCategories)
    {
        if (IsDeleted)
            throw new InvalidOperationException("The checkpoint is deleted.");
        TopologyCodes.Require(code, nameof(code));
        TopologyCodes.RequireUnique(Desks.Where(d => !d.IsDeleted), d => d.Code, code, "Desk");
        if (!AllowedDeskKinds(Kind).Contains(kind))
            throw new InvalidOperationException($"A {Kind} checkpoint cannot have a {kind}.");
        var desk = new Desk(this, code, name, kind);
        desk.SetLaneCategories(laneCategories);
        Desks.Add(desk);
        return desk;
    }
}

/// <summary>
/// A service point (glossary Desk): a check-in counter, a security lane, an immigration or emigration desk, or an
/// e-gate. Border desks serve lane categories; an e-gate always serves e-gate eligible travellers; counters and
/// security lanes have none.
/// </summary>
public class Desk : BaseSoftDeletableEntity<Desk>, ISiteBound
{
    public const int MaxLaneCategories = 8;

    protected Desk()
    {
    }

    internal Desk(Checkpoint checkpoint, string code, string name, DeskKind kind)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown desk kind.");
        Checkpoint = checkpoint;
        SiteCode = checkpoint.SiteCode;
        Code = code;
        Kind = kind;
        Name = string.IsNullOrWhiteSpace(name) ? code : TopologyCodes.RequireName(name, nameof(name));
    }

    public virtual Checkpoint Checkpoint { get; protected set; }
    public virtual string SiteCode { get; protected set; }
    public virtual string Code { get; protected set; }
    public virtual string Name { get; protected set; }
    public virtual DeskKind Kind { get; protected set; }

    /// <summary>Stored form of the lane categories: sorted codes joined by commas (for example "CIT,RES").</summary>
    public virtual string LaneCategoryCodes { get; protected set; }

    /// <summary>A desk taken out of service keeps its history; it is not offered for new zone profiles.</summary>
    public virtual bool InService { get; protected set; } = true;

    public virtual IReadOnlyList<string> LaneCategories =>
        string.IsNullOrEmpty(LaneCategoryCodes) ? [] : LaneCategoryCodes.Split(',');

    public virtual void Rename(string name) => Name = string.IsNullOrWhiteSpace(name) ? Code : TopologyCodes.RequireName(name, nameof(name));

    public virtual void SetInService(bool inService) => InService = inService;

    /// <summary>Applies the kind rules: none for counters and security lanes; at least one for border desks; EG for e-gates.</summary>
    public virtual void SetLaneCategories(IEnumerable<string> laneCategories)
    {
        var codes = (laneCategories ?? []).Select(c => c?.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (codes.Any(code => !LaneCategory.IsValid(code)))
            throw new ArgumentException("A lane category is 2 to 4 upper case letters.", nameof(laneCategories));
        if (codes.Count > MaxLaneCategories)
            throw new ArgumentException($"A desk serves at most {MaxLaneCategories} lane categories.", nameof(laneCategories));

        switch (Kind)
        {
            case DeskKind.Counter or DeskKind.SecurityLane when codes.Count > 0:
                throw new InvalidOperationException($"A {Kind} has no lane categories.");
            case DeskKind.Desk when codes.Count == 0:
                throw new InvalidOperationException("A border desk serves at least one lane category.");
            case DeskKind.EGate when !codes.Contains(LaneCategory.EGateEligible):
                throw new InvalidOperationException("An e-gate serves e-gate eligible travellers (EG).");
        }

        LaneCategoryCodes = codes.Count == 0 ? null : string.Join(',', codes);
    }
}
