using System.Data.Common;
using Ariva.Infra.Settings;
using NHibernate.Driver;
using Npgsql;

namespace Ariva.Infra.NHibernate.Drivers;

/// <summary>
/// The Npgsql driver Ariva configures by name in appsettings. Unlike AMAN it does not switch on
/// Npgsql.EnableLegacyTimestampBehavior: every instant is a UTC DateTime stored in timestamptz. Its connections download no
/// certificate while their TLS handshake builds the server's chain, as every connection Ariva opens (ARV-226,
/// <see cref="PostgresConnections"/>).
/// </summary>
public class NHibernatePostgresClientDriver : NpgsqlDriver
{
    public override DbConnection CreateConnection() => PostgresConnections.Harden((NpgsqlConnection)base.CreateConnection());
}
