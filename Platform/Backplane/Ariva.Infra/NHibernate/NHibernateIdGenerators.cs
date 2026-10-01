using NHibernate.Engine;
using NHibernate.Id;
using NHibernate.Mapping.ByCode;

namespace Ariva.Infra.NHibernate;

/// <summary>
/// Version 7 (time ordered) GUIDs, matching <see cref="EntityBase{T}.NewId"/>. An id already assigned by the domain
/// is kept, so an entity can raise events that carry its id before it is saved.
/// </summary>
internal sealed class GuidVersion7Generator : IIdentifierGenerator
{
    public object Generate(ISessionImplementor session, object obj) =>
        ExistingId(session, obj) ?? Guid.CreateVersion7();

    public Task<object> GenerateAsync(ISessionImplementor session, object obj, CancellationToken cancellationToken) =>
        Task.FromResult(Generate(session, obj));

    private static object ExistingId(ISessionImplementor session, object obj)
    {
        var id = session.GetEntityPersister(null, obj).GetIdentifier(obj);
        return id is Guid guid && guid != Guid.Empty ? guid : null;
    }
}

internal sealed class GuidVersion7GeneratorDef : IGeneratorDef
{
    public static readonly GuidVersion7GeneratorDef Instance = new();

    public string Class => typeof(GuidVersion7Generator).AssemblyQualifiedName;
    public object Params => null;
    public Type DefaultReturnType => typeof(Guid);
    public bool SupportedAsCollectionElementId => true;
}
