using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ariva.Core.Domain.Components;
using Ariva.Core.Domain.Enums;

namespace Ariva.Core.Domain.Entities;

/// <summary>
/// The complete set of zones and lines of a site as one version (glossary Zone profile, ADR-0003, ARV-016). A draft is
/// edited; publishing validates it, numbers it and fixes a geometry hash; a published version never changes and is
/// replaced by publishing a newer one; a retired version is kept for recomputation. Zones and lines are in floor
/// coordinates of their level, in metres.
/// <para>
/// Names from the prototype: "snake queue" is <see cref="ZoneKind.Queue"/>, "service area" <see cref="ZoneKind.Service"/>,
/// "overflow band" <see cref="ZoneKind.Overflow"/>; a "count line" is a <see cref="Line"/> with <see cref="LineRole.Count"/>.
/// </para>
/// </summary>
public class ZoneProfile : BaseAuditableEntity<ZoneProfile>, ISiteBound
{
    public const int MaxZones = 500;
    public const int MaxLines = 1_000;

    protected ZoneProfile()
    {
    }

    /// <summary>A new, empty draft for a site.</summary>
    public ZoneProfile(string siteCode, string name)
    {
        if (!Site.IsValidCode(siteCode))
            throw new ArgumentException("Unknown site code shape.", nameof(siteCode));
        SiteCode = siteCode;
        Name = TopologyCodes.RequireName(name, nameof(name));
        Status = ZoneProfileStatus.Draft;
    }

    public virtual string SiteCode { get; protected set; }
    public virtual string Name { get; protected set; }
    public virtual ZoneProfileStatus Status { get; protected set; }

    /// <summary>The published version number (1, 2, ...); null while a draft.</summary>
    public virtual int? Version { get; protected set; }

    /// <summary>The version the draft was copied from, if any.</summary>
    public virtual int? BasedOnVersion { get; protected set; }

    /// <summary>SHA-256 (hex) of the canonical geometry, fixed at publishing; equal geometry gives an equal hash.</summary>
    public virtual string GeometryHash { get; protected set; }

    public virtual DateTime? PublishedOn { get; protected set; }
    public virtual string PublishedBy { get; protected set; }
    public virtual DateTime? RetiredOn { get; protected set; }

    public virtual IList<Zone> Zones { get; protected set; } = [];
    public virtual IList<Line> Lines { get; protected set; } = [];

    public virtual bool IsDraft => Status == ZoneProfileStatus.Draft;

    #region Drafting

    /// <summary>A new draft with copies of this version's zones and lines (new ids), for the next change.</summary>
    public virtual ZoneProfile CreateDraft(string name)
    {
        if (Status == ZoneProfileStatus.Draft)
            throw new InvalidOperationException("Copy a published or retired version, not a draft.");
        var draft = new ZoneProfile(SiteCode, name ?? Name) { BasedOnVersion = Version };
        var map = new Dictionary<Zone, Zone>();
        foreach (var zone in Zones.Where(z => z.QueueZone is null).Concat(Zones.Where(z => z.QueueZone is not null)))
        {
            var copy = new Zone(draft, zone.Name, zone.Kind, zone.LevelId, zone.Points, zone.QueueZone is null ? null : map[zone.QueueZone], zone.DeskId)
            {
                LaneCategory = zone.LaneCategory
            };
            draft.Zones.Add(copy);
            map[zone] = copy;
        }

        foreach (var line in Lines)
            draft.Lines.Add(new Line(draft, line.Name, line.Role, line.Zone is null ? null : map[line.Zone], line.LevelId, line.Start, line.End));
        return draft;
    }

    public virtual void Rename(string name)
    {
        EnsureDraft();
        Name = TopologyCodes.RequireName(name, nameof(name));
    }

    /// <summary>
    /// Adds a zone on a level of this site. Service, staff and overflow zones hang off a queue zone of the same level;
    /// service and staff zones may name their desk.
    /// </summary>
    public virtual Zone AddZone(string name, ZoneKind kind, Level level, IReadOnlyList<FloorPoint> polygon, Zone queueZone = null, Guid? deskId = null)
    {
        EnsureDraft();
        ArgumentNullException.ThrowIfNull(level);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown zone kind.");
        if (level.SiteCode != SiteCode)
            throw new InvalidOperationException("The level belongs to another site.");
        if (level.Id is null || level.IsDeleted)
            throw new InvalidOperationException("The level must exist.");
        if (Zones.Count >= MaxZones)
            throw new InvalidOperationException($"A profile holds at most {MaxZones} zones.");
        var trimmed = TopologyCodes.RequireName(name, nameof(name));
        if (Zones.Any(z => string.Equals(z.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Zone {trimmed} already exists here.");
        CheckLink(kind, queueZone, level.Id.GetValueOrDefault());
        if (deskId is not null && kind is not (ZoneKind.Service or ZoneKind.Staff))
            throw new InvalidOperationException("Only service and staff zones name a desk.");

        var zone = new Zone(this, trimmed, kind, level.Id.GetValueOrDefault(), Zone.RequirePolygon(polygon, level), queueZone, deskId);
        Zones.Add(zone);
        return zone;
    }

    /// <summary>Moves a zone's vertices; its entry, exit and overflow entry lines must still lie on its edges.</summary>
    public virtual void MoveZone(Zone zone, Level level, IReadOnlyList<FloorPoint> polygon)
    {
        EnsureDraft();
        var own = Own(zone);
        if (level is null || level.Id != own.LevelId)
            throw new InvalidOperationException("A zone stays on its level.");
        var points = Zone.RequirePolygon(polygon, level);
        var stranded = Lines.Where(l => l.Zone == own && l.Role != LineRole.Count && Geometry.EdgeContaining(points, l.Start, l.End) < 0).ToList();
        if (stranded.Count > 0)
            throw new InvalidOperationException($"Line {stranded[0].Name} would no longer lie on the zone's edge; move or remove it first.");
        own.SetPoints(points);
    }

    public virtual void RenameZone(Zone zone, string name)
    {
        EnsureDraft();
        var own = Own(zone);
        var trimmed = TopologyCodes.RequireName(name, nameof(name));
        if (Zones.Any(z => z != own && string.Equals(z.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Zone {trimmed} already exists here.");
        own.Rename(trimmed);
    }

    /// <summary>
    /// Says which lane category a queue zone is the queue of (ARV-057): the immigration screen shows a hall's waits per
    /// lane from it, as configured per site. Null for a zone that is no lane's queue (check-in, security). Not part of
    /// the geometry hash: it changes no count or wait.
    /// </summary>
    public virtual void SetZoneLane(Zone zone, string laneCategory)
    {
        EnsureDraft();
        var own = Own(zone);
        if (laneCategory is null)
        {
            own.LaneCategory = null;
            return;
        }

        if (own.Kind != ZoneKind.Queue)
            throw new InvalidOperationException("Only a queue zone is the queue of a lane.");
        if (!LaneCategory.IsValid(laneCategory))
            throw new ArgumentException("A lane category is 2 to 4 capital letters, such as CIT.", nameof(laneCategory));
        own.LaneCategory = laneCategory;
    }

    /// <summary>Removes a zone and its lines; refused while other zones hang off it.</summary>
    public virtual IReadOnlyList<Line> RemoveZone(Zone zone)
    {
        EnsureDraft();
        var own = Own(zone);
        if (Zones.Any(z => z.QueueZone == own))
            throw new InvalidOperationException($"Zones hang off {own.Name}; remove them first.");
        var lines = Lines.Where(l => l.Zone == own).ToList();
        foreach (var line in lines)
            Lines.Remove(line);
        Zones.Remove(own);
        return lines;
    }

    /// <summary>
    /// Adds a line. Entry, exit and overflow entry lines belong to a zone (queue zones for entry and exit, overflow zones
    /// for overflow entry) and lie on one of its edges; count lines lie anywhere on a level of the site.
    /// </summary>
    public virtual Line AddLine(string name, LineRole role, Level level, FloorPoint start, FloorPoint end, Zone zone = null)
    {
        EnsureDraft();
        ArgumentNullException.ThrowIfNull(level);
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown line role.");
        if (level.SiteCode != SiteCode)
            throw new InvalidOperationException("The level belongs to another site.");
        if (level.Id is null || level.IsDeleted)
            throw new InvalidOperationException("The level must exist.");
        if (Lines.Count >= MaxLines)
            throw new InvalidOperationException($"A profile holds at most {MaxLines} lines.");
        var trimmed = TopologyCodes.RequireName(name, nameof(name));
        if (Lines.Any(l => string.Equals(l.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Line {trimmed} already exists here.");

        var a = start.Rounded();
        var b = end.Rounded();
        if (!a.IsFinite || !b.IsFinite || !level.Contains(a.X, a.Y) || !level.Contains(b.X, b.Y))
            throw new ArgumentException("A line lies inside its level.", nameof(start));
        if (Geometry.Length(a, b) < Geometry.Tolerance)
            throw new ArgumentException("A line has length.", nameof(end));

        var own = zone is null ? null : Own(zone);
        switch (role)
        {
            case LineRole.Entry or LineRole.Exit when own?.Kind != ZoneKind.Queue:
                throw new InvalidOperationException("Entry and exit lines belong to a queue zone.");
            case LineRole.OverflowEntry when own?.Kind != ZoneKind.Overflow:
                throw new InvalidOperationException("An overflow entry line belongs to an overflow zone.");
            case LineRole.Exit when Lines.Any(l => l.Zone == own && l.Role == LineRole.Exit):
                throw new InvalidOperationException($"Zone {own.Name} already has its exit line.");
        }

        if (own is not null && own.LevelId != level.Id)
            throw new InvalidOperationException("A line lies on its zone's level.");
        if (role != LineRole.Count && Geometry.EdgeContaining(own.Points, a, b) < 0)
            throw new InvalidOperationException($"Line {trimmed} must lie on an edge of zone {own.Name}.");

        var line = new Line(this, trimmed, role, own, level.Id.GetValueOrDefault(), a, b);
        Lines.Add(line);
        return line;
    }

    public virtual void RemoveLine(Line line)
    {
        EnsureDraft();
        if (line is null || !Lines.Remove(line))
            throw new InvalidOperationException("The line is not in this profile.");
    }

    #endregion

    #region Validation and lifecycle

    /// <summary>
    /// What stops this draft from being published: no zones; a queue zone without an entry line or without exactly one
    /// exit line; an overflow zone without its overflow entry line; a hanging zone whose queue zone is missing; geometry
    /// outside its level (levels may have shrunk since the zone was drawn).
    /// </summary>
    public virtual IReadOnlyList<string> Validate(IReadOnlyDictionary<Guid, Level> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        var problems = new List<string>();
        if (Zones.Count == 0)
            problems.Add("The profile has no zones.");

        foreach (var zone in Zones.OrderBy(z => z.Name, StringComparer.Ordinal))
        {
            if (!levels.TryGetValue(zone.LevelId, out var level) || level.IsDeleted)
            {
                problems.Add($"Zone {zone.Name}: its level no longer exists.");
                continue;
            }

            if (zone.Points.Any(p => !level.Contains(p.X, p.Y)))
                problems.Add($"Zone {zone.Name}: a vertex lies outside level {level.Code}.");

            var lines = Lines.Where(l => l.Zone == zone).ToList();
            switch (zone.Kind)
            {
                case ZoneKind.Queue:
                    if (!lines.Any(l => l.Role == LineRole.Entry))
                        problems.Add($"Zone {zone.Name}: a queue zone needs at least one entry line.");
                    if (lines.Count(l => l.Role == LineRole.Exit) != 1)
                        problems.Add($"Zone {zone.Name}: a queue zone needs exactly one exit line.");
                    break;
                case ZoneKind.Overflow:
                    if (!lines.Any(l => l.Role == LineRole.OverflowEntry))
                        problems.Add($"Zone {zone.Name}: an overflow zone needs an overflow entry line.");
                    break;
            }

            if (zone.Kind != ZoneKind.Queue && (zone.QueueZone is null || !Zones.Contains(zone.QueueZone)))
                problems.Add($"Zone {zone.Name}: it must hang off a queue zone.");
        }

        foreach (var line in Lines.Where(l => l.Zone is null).OrderBy(l => l.Name, StringComparer.Ordinal))
        {
            if (!levels.TryGetValue(line.LevelId, out var level) || level.IsDeleted)
                problems.Add($"Line {line.Name}: its level no longer exists.");
            else if (!level.Contains(line.Start.X, line.Start.Y) || !level.Contains(line.End.X, line.End.Y))
                problems.Add($"Line {line.Name}: it lies outside level {level.Code}.");
        }

        return problems;
    }

    /// <summary>
    /// Publishes the draft as <paramref name="version"/> (the service gives the site's next number) after validation;
    /// from then on the profile is immutable. Returns the problems instead when there are any.
    /// </summary>
    public virtual IReadOnlyList<string> Publish(int version, IReadOnlyDictionary<Guid, Level> levels, string publishedBy, DateTime utcNow, int? replacesVersion = null)
    {
        EnsureDraft();
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        EnsureUtc(utcNow);
        var problems = Validate(levels);
        if (problems.Count > 0)
            return problems;

        Version = version;
        GeometryHash = ComputeGeometryHash();
        PublishedOn = utcNow;
        PublishedBy = publishedBy;
        Status = ZoneProfileStatus.Published;
        // ARV-017: the unit of work writes it to the outbox in this transaction; consumers switch to this geometry.
        RaiseDomainEvent(new Events.ZoneProfilePublished
        {
            ProfileId = Id.GetValueOrDefault(),
            SiteCode = SiteCode,
            Version = version,
            ReplacesVersion = replacesVersion,
            GeometryHash = GeometryHash,
            PublishedBy = publishedBy,
            ZoneCount = Zones.Count,
            LineCount = Lines.Count,
            OccurredOn = utcNow
        });
        return [];
    }

    /// <summary>Takes a published version out of use; it stays for recomputation and evidence packs.</summary>
    public virtual void Retire(DateTime utcNow)
    {
        EnsureUtc(utcNow);
        if (Status != ZoneProfileStatus.Published)
            throw new InvalidOperationException("Only a published version is retired.");
        Status = ZoneProfileStatus.Retired;
        RetiredOn = utcNow;
    }

    /// <summary>
    /// SHA-256 over the canonical geometry: zones by name with kind, level, queue zone, desk and vertices; lines by name
    /// with role, zone, level and ends; coordinates at millimetre precision in invariant culture; no ids of the profile
    /// itself, so the same geometry always gives the same hash.
    /// </summary>
    public virtual string ComputeGeometryHash()
    {
        var text = new StringBuilder("ariva-zone-profile-v1\n");
        foreach (var zone in Zones.OrderBy(z => z.Name, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"zone|{zone.Name}|{zone.Kind}|{zone.LevelId:N}|{zone.QueueZone?.Name}|{zone.DeskId:N}|{Geometry.FormatRing(zone.Points)}\n");
        }

        foreach (var line in Lines.OrderBy(l => l.Name, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"line|{line.Name}|{line.Role}|{line.Zone?.Name}|{line.LevelId:N}|{line.Start}|{line.End}\n");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    #endregion

    private void EnsureDraft()
    {
        if (Status != ZoneProfileStatus.Draft)
            throw new InvalidOperationException("A published or retired zone profile never changes; create a draft from it.");
    }

    private Zone Own(Zone zone) =>
        zone is not null && Zones.Contains(zone) ? zone : throw new InvalidOperationException("The zone is not in this profile.");

    private void CheckLink(ZoneKind kind, Zone queueZone, Guid levelId)
    {
        if (kind == ZoneKind.Queue)
        {
            if (queueZone is not null)
                throw new InvalidOperationException("A queue zone does not hang off another zone.");
            return;
        }

        if (queueZone is null)
            throw new InvalidOperationException($"A {kind} zone hangs off a queue zone.");
        var owner = Own(queueZone);
        if (owner.Kind != ZoneKind.Queue)
            throw new InvalidOperationException("Zones hang off a queue zone only.");
        if (owner.LevelId != levelId)
            throw new InvalidOperationException("A zone hangs off a queue zone on the same level.");
    }
}

/// <summary>A polygon in a zone profile with one role in a process (glossary Zone).</summary>
public class Zone : EntityBase<Zone>
{
    public const int MaxVertices = 200;

    /// <summary>200 vertices of "2000.000 2000.000," (levels are at most 2,000 m) fit with room.</summary>
    public const int PolygonLength = 4000;

    protected Zone()
    {
    }

    internal Zone(ZoneProfile profile, string name, ZoneKind kind, Guid levelId, IReadOnlyList<FloorPoint> points, Zone queueZone, Guid? deskId)
    {
        Profile = profile;
        Name = name;
        Kind = kind;
        LevelId = levelId;
        QueueZone = queueZone;
        DeskId = deskId;
        SetPoints(points);
    }

    public virtual ZoneProfile Profile { get; protected set; }
    public virtual string Name { get; protected set; }
    public virtual ZoneKind Kind { get; protected set; }
    public virtual Guid LevelId { get; protected set; }

    /// <summary>The queue zone a service, staff or overflow zone hangs off (it shares its Kafka key).</summary>
    public virtual Zone QueueZone { get; protected set; }

    public virtual Guid? DeskId { get; protected set; }

    /// <summary>The lane category whose queue this queue zone is (CIT, RES, VIS, CRW, EG ...), or null (ARV-057).</summary>
    public virtual string LaneCategory { get; protected internal set; }

    /// <summary>Stored vertices: "x y,x y,..." in metres at millimetre precision.</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(PolygonLength)]
    public virtual string Polygon { get; protected set; }

    public virtual IReadOnlyList<FloorPoint> Points => Geometry.ParseRing(Polygon, MaxVertices) ?? [];

    public virtual double AreaSquareMetres => Math.Abs(Geometry.SignedArea(Points));

    protected internal virtual void Rename(string name) => Name = name;

    protected internal virtual void SetPoints(IReadOnlyList<FloorPoint> points) => Polygon = Geometry.FormatRing(points);

    /// <summary>The rounded, validated polygon: 3 to 200 vertices, simple, inside the level.</summary>
    internal static IReadOnlyList<FloorPoint> RequirePolygon(IReadOnlyList<FloorPoint> polygon, Level level)
    {
        if (polygon is null || polygon.Count < 3)
            throw new ArgumentException("A zone has at least three points.", nameof(polygon));
        if (polygon.Count > MaxVertices)
            throw new ArgumentException($"A zone has at most {MaxVertices} points.", nameof(polygon));
        var points = polygon.Select(p => p.Rounded()).ToList();
        if (points.Any(p => !p.IsFinite || !level.Contains(p.X, p.Y)))
            throw new ArgumentException($"Every point lies inside level {level.Code} (0 to {level.WidthMetres} by 0 to {level.DepthMetres} m).", nameof(polygon));
        if (!Geometry.IsSimplePolygon(points))
            throw new ArgumentException("A zone is a simple polygon: no repeated points and no crossing or touching edges.", nameof(polygon));
        return points;
    }
}

/// <summary>A line in a zone profile (glossary Entry line, Exit line, Count line, Overflow band).</summary>
public class Line : EntityBase<Line>
{
    protected Line()
    {
    }

    internal Line(ZoneProfile profile, string name, LineRole role, Zone zone, Guid levelId, FloorPoint start, FloorPoint end)
    {
        Profile = profile;
        Name = name;
        Role = role;
        Zone = zone;
        LevelId = levelId;
        StartX = start.X;
        StartY = start.Y;
        EndX = end.X;
        EndY = end.Y;
    }

    public virtual ZoneProfile Profile { get; protected set; }
    public virtual string Name { get; protected set; }
    public virtual LineRole Role { get; protected set; }

    /// <summary>The zone the line belongs to; null for count lines that stand alone.</summary>
    public virtual Zone Zone { get; protected set; }

    public virtual Guid LevelId { get; protected set; }
    public virtual double StartX { get; protected set; }
    public virtual double StartY { get; protected set; }
    public virtual double EndX { get; protected set; }
    public virtual double EndY { get; protected set; }

    public virtual FloorPoint Start => new(StartX, StartY);
    public virtual FloorPoint End => new(EndX, EndY);
    public virtual double LengthMetres => Geometry.Length(Start, End);
}
