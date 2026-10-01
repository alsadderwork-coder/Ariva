using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Ariva.Core.Domain.Common;

/// <summary>
/// Base class for every domain entity: identity, equality by identity, domain events and a deterministic hash key.
/// Ported from AMAN's EntityBase with three fixes:
/// <list type="bullet">
/// <item>Two unsaved (transient) entities are never equal unless they are the same object; AMAN compared null ids
/// as equal, so two new entities looked identical in a set.</item>
/// <item>The hash code is computed once and kept, so an entity stays findable in a set after it is saved.</item>
/// <item>Public members are virtual so NHibernate can proxy the entity for lazy loading.</item>
/// </list>
/// </summary>
public abstract class EntityBase<T> : IHasDomainEvents, IDomain<Guid?> where T : EntityBase<T>
{
    #region Fields

    private readonly List<IEvent> _domainEvents = [];
    private int? _cachedHashCode;

    #endregion

    #region Identity

    public virtual Guid? Id { get; set; }

    /// <summary>True until the entity has an identity (it has not been saved and no id was assigned).</summary>
    public virtual bool IsTransient => Id is null || Id == Guid.Empty;

    /// <summary>A new time-ordered (version 7) identity; sequential ids keep PostgreSQL indexes compact.</summary>
    public static Guid NewId() => Guid.CreateVersion7();

    public virtual object GetId() => Id;

    public virtual TId GetId<TId>() => (TId)GetId();

    #endregion

    #region Domain Events

    public virtual IReadOnlyList<IEvent> DomainEvents => _domainEvents.AsReadOnly();

    public virtual void RaiseDomainEvent(IEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    public virtual void ClearDomainEvents() => _domainEvents.Clear();

    public virtual IReadOnlyList<IEvent> DequeueDomainEvents()
    {
        var events = _domainEvents.ToList();
        _domainEvents.Clear();
        return events;
    }

    #endregion

    #region Equality

    public override bool Equals(object obj) => Equals(obj as T);

    public virtual bool Equals(T other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;
        if (IsTransient || other.IsTransient)
            return false;
        return Id == other.Id;
    }

    public override int GetHashCode()
    {
        // Computed once: a transient entity keeps its reference hash after it gets an id, so sets stay consistent.
        _cachedHashCode ??= IsTransient ? RuntimeHelpers.GetHashCode(this) : HashCode.Combine(typeof(T), Id);
        return _cachedHashCode.Value;
    }

    public static bool operator ==(EntityBase<T> left, T right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(EntityBase<T> left, T right) => !(left == right);

    public override string ToString() => $"{typeof(T).Name}:{Id}";

    #endregion

    #region Hash Key

    /// <summary>
    /// A deterministic uppercase hex SHA-256 key from the given parts (for natural or composite keys). Strings are
    /// trimmed and lower-cased; every part is followed by '|', so ("ab", "c") and ("a", "bc") differ.
    /// </summary>
    protected static string GenerateHashKey(params object[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts ?? [])
        {
            var normalized = part switch
            {
                null => string.Empty,
                string s => s.Trim().ToLowerInvariant(),
                Guid g => g.ToString("D"),
                Enum e => Convert.ToString(e, CultureInfo.InvariantCulture).ToLowerInvariant(),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture).Trim().ToLowerInvariant(),
                _ => part.ToString()?.Trim().ToLowerInvariant() ?? string.Empty
            };
            builder.Append(normalized).Append('|');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    #endregion
}
