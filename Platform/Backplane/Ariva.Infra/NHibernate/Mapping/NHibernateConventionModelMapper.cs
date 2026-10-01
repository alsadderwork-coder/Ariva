using System.Reflection;
using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Type;

namespace Ariva.Infra.NHibernate.Mapping;

/// <summary>
/// Maps every entity by convention, ported from AMAN's NHibernateConventionModelMapper. Conventions live in
/// <see cref="NHibernateMappingRules"/>; an entity that needs something else gets an explicit override in
/// <c>NHibernateSessionFactoryProvider.ApplyOverrides</c>.
/// <list type="bullet">
/// <item>Ids are version 7 GUIDs in column <c>id</c>.</item>
/// <item>DateTime is stored as timestamptz and read back as UTC (AMAN dropped milliseconds for Oracle; Ariva keeps
/// full precision, event ordering depends on it).</item>
/// <item>Enums are stored by name; strings default to 1,000 characters.</item>
/// <item>Soft deleted rows are hidden by a filter and a where clause (the where clause covers Get, which ignores
/// filters).</item>
/// <item>References never get database foreign keys from the mapper; the versioned scripts declare them.</item>
/// </list>
/// </summary>
internal sealed class NHibernateConventionModelMapper : ConventionModelMapper
{
    private readonly NHibernateMappingRules _rules;

    public NHibernateConventionModelMapper(NHibernateMappingRules rules)
    {
        _rules = rules;

        IsRootEntity((type, _) => _rules.IsRootEntity(type));
        IsEntity((type, _) => _rules.IsEntity(type));
        IsComponent((type, _) => _rules.IsComponent(type));
        IsPersistentProperty((member, _) => _rules.IsPersistentProperty(member));
        IsBag((member, _) => _rules.IsBag(member));
        IsManyToOne((member, _) => _rules.IsEntity(MemberType(member)) && _rules.IsPersistentProperty(member));
        IsOneToMany((member, _) => _rules.IsBag(member) && !IsManyToManyMember(member));
        IsManyToMany((member, _) => _rules.IsBag(member) && IsManyToManyMember(member));

        BeforeMapClass += OnBeforeMapClass;
        BeforeMapProperty += OnBeforeMapProperty;
        BeforeMapManyToOne += OnBeforeMapManyToOne;
        BeforeMapBag += OnBeforeMapBag;
        BeforeMapManyToMany += OnBeforeMapManyToMany;
    }

    #region Classes

    private void OnBeforeMapClass(IModelInspector inspector, Type type, IClassAttributesMapper map)
    {
        map.Table(_rules.TableName(type));
        map.Id(id =>
        {
            id.Column(_rules.IdColumn);
            id.Generator(GuidVersion7GeneratorDef.Instance);
            id.UnsavedValue(null);
        });

        map.Lazy(_rules.EnableLazyLoading);
        map.DynamicUpdate(_rules.EnableDynamicUpdate);
        map.DynamicInsert(_rules.EnableDynamicInsert);

        if (typeof(ISoftDeletable).IsAssignableFrom(type))
        {
            var condition = _rules.SoftDeleteColumn + " IS NULL";
            map.Filter(NHibernateMappingRules.SoftDeleteFilterName, filter => filter.Condition(condition));
            map.Where(condition);
        }
    }

    #endregion

    #region Properties

    private void OnBeforeMapProperty(IModelInspector inspector, PropertyPath member, IPropertyMapper map)
    {
        var property = (PropertyInfo)member.LocalMember;
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        // A component property is prefixed with the owning property: Address.City becomes address_city.
        var column = member.PreviousPath is not null && _rules.IsComponent(property.ReflectedType)
            ? _rules.ComponentColumnName(member.PreviousPath.LocalMember.Name, property.Name)
            : _rules.ColumnName(property.Name);

        if (type.IsEnum)
        {
            // Stored by name, so reordering enum members never changes stored meaning.
            map.Type(typeof(EnumStringType<>).MakeGenericType(type), null);
            map.Column(column);
            map.Length(100);
        }
        else if (type == typeof(string))
        {
            map.Column(column);
            map.Length(_rules.DefaultStringLength);
        }
        else if (type == typeof(DateTime))
        {
            map.Type(NHibernateUtil.UtcDateTime);
            map.Column(c =>
            {
                c.Name(column);
                c.SqlType("timestamptz");
            });
        }
        else
        {
            map.Column(column);
        }
    }

    #endregion

    #region Relations

    private void OnBeforeMapManyToOne(IModelInspector inspector, PropertyPath member, IManyToOneMapper map)
    {
        map.Column(_rules.ForeignKeyColumn(member.LocalMember.Name));
        map.Fetch(FetchKind.Join);
        map.Cascade(Cascade.None);
        map.ForeignKey("none");

        // A soft deleted referenced row reads as null instead of failing the whole load (AMAN behaviour).
        map.NotFound(NotFoundMode.Ignore);
    }

    private void OnBeforeMapBag(IModelInspector inspector, PropertyPath member, IBagPropertiesMapper map)
    {
        var elementType = ElementType(member.LocalMember);
        var owner = member.LocalMember.ReflectedType;
        map.Fetch(CollectionFetchMode.Subselect);

        if (IsManyToManyMember(member.LocalMember))
        {
            map.Table(_rules.ConjunctionTable(owner, member.LocalMember));
            map.Key(key =>
            {
                key.Column(_rules.ForeignKeyColumn(owner.Name));
                key.ForeignKey("none");
            });
            map.Cascade(Cascade.None);
            return;
        }

        // One to many: the child holds the key, named after its reference back to the owner.
        var backReference = elementType.GetProperties().FirstOrDefault(p => p.PropertyType.IsAssignableFrom(owner));
        map.Key(key =>
        {
            key.Column(_rules.ForeignKeyColumn(backReference?.Name ?? owner.Name));
            key.ForeignKey("none");
        });
        map.Inverse(true);

        // AMAN parity: children are saved by the service explicitly; merge follows the graph.
        map.Cascade(Cascade.Merge);

        // Only for one to many: the condition targets the child table. On a many-to-many it would name a column of
        // the conjunction table, which has none (the bug AMAN fixed in UserContext.Roles).
        if (typeof(ISoftDeletable).IsAssignableFrom(elementType))
        {
            var condition = _rules.SoftDeleteColumn + " IS NULL";
            map.Filter(NHibernateMappingRules.SoftDeleteFilterName, filter => filter.Condition(condition));
            map.Where(condition);
        }
    }

    private void OnBeforeMapManyToMany(IModelInspector inspector, PropertyPath member, IManyToManyMapper map)
    {
        map.Column(_rules.ForeignKeyColumn(ElementType(member.LocalMember).Name));
        map.ForeignKey("none");
    }

    #endregion

    #region Helpers

    private static Type MemberType(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => typeof(object)
    };

    private static Type ElementType(MemberInfo member) => MemberType(member).GenericTypeArguments.First();

    /// <summary>
    /// Many to many when the element type has no single reference back to the owner (unidirectional), or holds a
    /// collection of the owner (bidirectional). Otherwise the element's reference makes it one to many.
    /// </summary>
    private static bool IsManyToManyMember(MemberInfo member)
    {
        var owner = member.ReflectedType;
        var element = ElementType(member);
        var properties = element.GetProperties();

        var hasBackReference = properties.Any(p => p.PropertyType.IsAssignableFrom(owner) && p.PropertyType != typeof(object));
        var hasBackCollection = properties.Any(p => p.PropertyType.IsGenericType &&
                                                    p.PropertyType.GenericTypeArguments.Length == 1 &&
                                                    p.PropertyType.GenericTypeArguments[0].IsAssignableFrom(owner));
        return hasBackCollection || !hasBackReference;
    }

    #endregion
}
