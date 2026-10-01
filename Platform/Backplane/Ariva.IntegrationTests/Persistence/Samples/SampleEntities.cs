using Ariva.Core.Domain.Common;
using Ariva.Core.Domain.Components;

namespace Ariva.IntegrationTests.Persistence.Samples;

public enum SampleZoneKind
{
    SnakeQueue,
    ServiceArea,
    OverflowBand
}

/// <summary>Soft deletable aggregate with children, an enum and a domain event. Not a real Ariva entity.</summary>
public class SampleZone : BaseSoftDeletableEntity<SampleZone>
{
    protected SampleZone()
    {
    }

    public SampleZone(string code, string name, SampleZoneKind kind)
    {
        Code = code;
        Name = name;
        Kind = kind;
    }

    public virtual string Code { get; protected set; }
    public virtual string Name { get; protected set; }
    public virtual SampleZoneKind Kind { get; protected set; }
    public virtual int Capacity { get; protected set; }
    public virtual DateTime? OpenedAt { get; protected set; }
    public virtual IList<SampleDesk> Desks { get; protected set; } = [];

    public virtual void Open(int capacity, DateTime utcNow)
    {
        Capacity = capacity;
        OpenedAt = utcNow;
        RaiseDomainEvent(new SampleZoneOpened { ZoneCode = Code, Capacity = capacity, OccurredOn = utcNow });
    }

    public virtual void Rename(string name) => Name = name;
}

/// <summary>Child of <see cref="SampleZone"/>, one to many through <see cref="Zone"/>.</summary>
public class SampleDesk : BaseAuditableEntity<SampleDesk>
{
    protected SampleDesk()
    {
    }

    public SampleDesk(SampleZone zone, int number)
    {
        Zone = zone;
        Number = number;
    }

    public virtual SampleZone Zone { get; protected set; }
    public virtual int Number { get; protected set; }
}

/// <summary>Named like a PostgreSQL reserved word, to prove table names are quoted.</summary>
public class User : EntityBase<User>
{
    protected User()
    {
    }

    public User(string userName) => UserName = userName;

    public virtual string UserName { get; protected set; }
}

/// <summary>What the recording outbox writes: one row per domain event, in the unit of work's transaction.</summary>
public class SampleOutboxRow : EntityBase<SampleOutboxRow>
{
    public virtual Guid EventId { get; set; }
    public virtual string EventType { get; set; }
    public virtual string PartitionKey { get; set; }
}

public sealed class SampleZoneOpened : EventBase
{
    public string ZoneCode { get; set; }
    public int Capacity { get; set; }

    public override string GetPartitionKey() => ZoneCode;
}

/// <summary>Row shape for ExecuteSqlAsync (aliases match property names).</summary>
public sealed class ZoneNameRow
{
    public string Code { get; set; }
    public string Name { get; set; }
}
