using System.Collections;
using System.Reflection;
using System.Text;

namespace Ariva.Infra.NHibernate.Mapping;

/// <summary>
/// The conventions the mapper applies, ported from AMAN's NHibernateMappingRules and trimmed to what Ariva uses.
/// <para>
/// Naming differs from AMAN on purpose: tables and columns are snake_case and tables are singular
/// (<c>zone_profile</c>, <c>created_on</c>, <c>site_id</c>), the style of the hand-written versioned scripts
/// (ARV-006) and of the tables ADR-0018 names (<c>outbox_message</c>, <c>processed_event</c>). AMAN's lowercase
/// plural names without separators (<c>zoneprofiles</c>, <c>createdon</c>) are hard to read in SQL.
/// </para>
/// <para>
/// Dropped from AMAN: encryption chosen by property name (any member named *Password* or *Username* was encrypted,
/// which breaks unique indexes and case-insensitive lookups; Ariva stores password hashes, ADR-0026) and the per
/// entity JSON, big string and ignore lists, which named AMAN entities.
/// </para>
/// </summary>
internal sealed class NHibernateMappingRules
{
    public const string SoftDeleteFilterName = "SoftDeleteFilter";

    /// <summary>Ariva keeps AMAN's eager default: no lazy proxies, references fetched by join.</summary>
    public bool EnableLazyLoading { get; init; }
    public bool EnableDynamicUpdate { get; init; } = true;
    public bool EnableDynamicInsert { get; init; } = true;
    public int DefaultStringLength { get; init; } = 1_000;

    public string IdColumn => "id";
    public string SoftDeleteColumn => ToSnakeCase(nameof(ISoftDeletable.DeletedOn));

    /// <summary>A concrete IDomain type whose base types are not mapped entities themselves.</summary>
    public bool IsRootEntity(Type type) =>
        IsEntity(type) && !HasConcreteBaseImplementing(type, typeof(IDomain));

    public bool IsEntity(Type type) =>
        typeof(IDomain).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract;

    /// <summary>Value objects stored in the owner's table; marked with <see cref="IComponent"/>.</summary>
    public bool IsComponent(Type type) => typeof(IComponent).IsAssignableFrom(type);

    /// <summary>Readable and writable properties are persisted; getter-only (computed) properties are not.</summary>
    public bool IsPersistentProperty(MemberInfo member) =>
        member is PropertyInfo { CanRead: true, CanWrite: true } property && property.GetSetMethod(nonPublic: true) is not null;

    /// <summary>A collection of entities (not strings, not simple values).</summary>
    public bool IsBag(MemberInfo member) =>
        member is PropertyInfo property &&
        property.PropertyType != typeof(string) &&
        typeof(IEnumerable).IsAssignableFrom(property.PropertyType) &&
        property.PropertyType.GenericTypeArguments.Any(IsEntity);

    public string TableName(Type type) => Quote(ToSnakeCase(type.Name));

    public string ColumnName(string propertyName) => Quote(ToSnakeCase(propertyName));

    /// <summary>A component column is prefixed with the owning property: <c>Address.City</c> is <c>address_city</c>.</summary>
    public string ComponentColumnName(string ownerProperty, string propertyName) =>
        Quote(ToSnakeCase(ownerProperty) + "_" + ToSnakeCase(propertyName));

    /// <summary>
    /// PostgreSQL reserved key words (https://www.postgresql.org/docs/current/sql-keywords-appendix.html) cannot be
    /// bare identifiers; NHibernate quotes a name written in backticks. A <c>User</c> entity maps to <c>"user"</c>.
    /// </summary>
    public static string Quote(string identifier) =>
        ReservedWords.Contains(identifier) ? "`" + identifier + "`" : identifier;

    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "all", "analyse", "analyze", "and", "any", "array", "as", "asc", "asymmetric", "authorization", "binary", "both",
        "case", "cast", "check", "collate", "collation", "column", "concurrently", "constraint", "create", "cross",
        "current_catalog", "current_date", "current_role", "current_schema", "current_time", "current_timestamp",
        "current_user", "default", "deferrable", "desc", "distinct", "do", "else", "end", "except", "false", "fetch",
        "for", "foreign", "freeze", "from", "full", "grant", "group", "having", "ilike", "in", "initially", "inner",
        "intersect", "into", "is", "isnull", "join", "lateral", "leading", "left", "like", "limit", "localtime",
        "localtimestamp", "natural", "not", "notnull", "null", "offset", "on", "only", "or", "order", "outer",
        "overlaps", "placing", "primary", "references", "returning", "right", "select", "session_user", "similar",
        "some", "symmetric", "system_user", "table", "tablesample", "then", "to", "trailing", "true", "union", "unique",
        "user", "using", "variadic", "verbose", "when", "where", "window", "with"
    };

    /// <summary>Foreign key column for a reference property: <c>Site</c> becomes <c>site_id</c>.</summary>
    public string ForeignKeyColumn(string propertyName) => Quote(ToSnakeCase(propertyName) + "_" + IdColumn);

    /// <summary>Conjunction table for a many-to-many: <c>{source}_{property}</c>, unique per property.</summary>
    public string ConjunctionTable(Type source, MemberInfo member) => Quote(ToSnakeCase(source.Name) + "_" + ToSnakeCase(member.Name));

    /// <summary>
    /// PascalCase to snake_case, keeping acronyms together: <c>CreatedById</c> is <c>created_by_id</c>,
    /// <c>IPAddress</c> is <c>ip_address</c>, <c>Level2Name</c> is <c>level2_name</c>.
    /// </summary>
    public static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];
            if (char.IsUpper(current))
            {
                var previous = i > 0 ? name[i - 1] : '\0';
                var next = i + 1 < name.Length ? name[i + 1] : '\0';
                var startsWord = i > 0 && (char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && char.IsLower(next)));
                if (startsWord && builder.Length > 0 && builder[^1] != '_')
                    builder.Append('_');
                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }

    private static bool HasConcreteBaseImplementing(Type type, Type contract)
    {
        var baseType = type.BaseType;
        while (baseType is not null && baseType != typeof(object))
        {
            if (contract.IsAssignableFrom(baseType) && !baseType.IsAbstract)
                return true;
            baseType = baseType.BaseType;
        }

        return false;
    }
}
