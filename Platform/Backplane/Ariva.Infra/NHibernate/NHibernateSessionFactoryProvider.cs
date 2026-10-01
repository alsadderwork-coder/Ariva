using System.Reflection;
using Ariva.Infra.NHibernate.Mapping;
using Ariva.Infra.Settings;
using NHibernate;
using NHibernate.Cfg;
using NHibernate.Engine;
using NHibernate.Tool.hbm2ddl;
using NHibernate.Type;
using Environment = NHibernate.Cfg.Environment;

namespace Ariva.Infra.NHibernate;

/// <summary>
/// Builds the NHibernate configuration and session factory once per process (a DI singleton). AMAN kept both in
/// static fields and swallowed build errors, so a mapping mistake surfaced later as a null session; here a failure
/// throws on first use with the original exception, and tests can build factories for other databases side by side.
/// </summary>
internal sealed class NHibernateSessionFactoryProvider
{
    private readonly Lazy<(Configuration Configuration, ISessionFactory Factory)> _built;

    public NHibernateSessionFactoryProvider(DatabaseSettings settings, IEnumerable<Assembly> entityAssemblies = null)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        var assemblies = (entityAssemblies ?? [typeof(Ariva.Core._IAssemblyMark).Assembly]).Distinct().ToArray();
        _built = new Lazy<(Configuration, ISessionFactory)>(() => Build(settings, assemblies), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public DatabaseSettings Settings { get; }

    public ISessionFactory SessionFactory => _built.Value.Factory;

    public Configuration Configuration => _built.Value.Configuration;

    /// <summary>The DDL NHibernate would create, for authoring versioned scripts. Nothing is executed.</summary>
    public string GenerateCreateScript()
    {
        var writer = new StringWriter();
        new SchemaExport(Configuration).SetDelimiter(";").Create(writer, false);
        return writer.ToString();
    }

    /// <summary>Runs SchemaUpdate when Database:AllowSchemaUpdate is true; refuses otherwise.</summary>
    public string UpdateSchema()
    {
        if (!Settings.AllowSchemaUpdate)
            throw new InvalidOperationException("Schema update is disabled (Database:AllowSchemaUpdate is false). Apply the versioned scripts instead.");

        var script = new System.Text.StringBuilder();
        var update = new SchemaUpdate(Configuration);
        update.Execute(sql => script.Append(sql).Append(";\n"), true);

        // SchemaUpdate does not throw: errors are collected. Surface them (AMAN's fix, kept).
        if (update.Exceptions.Count > 0)
        {
            throw new AggregateException(
                $"Schema update failed with {update.Exceptions.Count} error(s): {string.Join(" | ", update.Exceptions.Select(x => x.GetBaseException().Message))}",
                update.Exceptions);
        }

        return script.ToString();
    }

    private static (Configuration, ISessionFactory) Build(DatabaseSettings settings, Assembly[] assemblies)
    {
        var configuration = new Configuration();
        configuration.SetProperty(Environment.ConnectionString, settings.BuildConnectionString());
        configuration.SetProperty(Environment.ConnectionProvider, settings.NHibernate.ConnectionProvider);
        configuration.SetProperty(Environment.ConnectionDriver, settings.NHibernate.ConnectionDriver);
        configuration.SetProperty(Environment.Dialect, settings.NHibernate.Dialect);
        configuration.SetProperty(Environment.UseSecondLevelCache, bool.FalseString);
        configuration.SetProperty(Environment.UseQueryCache, bool.FalseString);
        configuration.SetProperty(Environment.ShowSql, settings.NHibernate.ShowSql ? bool.TrueString : bool.FalseString);
        // Reserved words are quoted by the mapping rules; keyword auto quoting would open a connection at build time.
        configuration.SetProperty(Environment.Hbm2ddlKeyWords, Hbm2DDLKeyWords.None.ToString());
        configuration.DataBaseIntegration(db => db.BatchSize = settings.NHibernate.BatchSize);

        configuration.AddFilterDefinition(new FilterDefinition(
            NHibernateMappingRules.SoftDeleteFilterName, string.Empty, new Dictionary<string, IType>(), true));

        var mapper = new NHibernateConventionModelMapper(new NHibernateMappingRules());
        ApplyOverrides(mapper);

        var rules = new NHibernateMappingRules();
        var entities = assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(rules.IsEntity)
            .Where(type => !rules.IsComponent(type))
            .ToArray();
        configuration.AddMapping(mapper.CompileMappingFor(entities));

        return (configuration, configuration.BuildSessionFactory());
    }

    /// <summary>Explicit mappings for entities that do not fit the conventions. Keep this short.</summary>
    private static void ApplyOverrides(NHibernateConventionModelMapper mapper)
    {
        _ = mapper;
    }
}
