using NHibernate.Driver;

namespace Ariva.Infra.NHibernate.Drivers;

/// <summary>
/// The Npgsql driver Ariva configures by name in appsettings. Unlike AMAN it does not switch on
/// Npgsql.EnableLegacyTimestampBehavior: every instant is a UTC DateTime stored in timestamptz.
/// </summary>
public class NHibernatePostgresClientDriver : NpgsqlDriver
{
}
