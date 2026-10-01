namespace Ariva.Core.Domain.Contracts;

/// <summary>
/// Anything persisted with an identity. Ported from AMAN (Aman.Core.Domain.Contracts.IDomain).
/// </summary>
public interface IDomain
{
    object GetId();
    TId GetId<TId>();
}

/// <summary>
/// A persisted object with a typed identity.
/// </summary>
public interface IDomain<TId> : IDomain
{
    TId Id { get; set; }
}

/// <summary>
/// A domain entity that is not a lookup.
/// </summary>
public interface IEntity<TId> : IDomain<TId>
{
}
