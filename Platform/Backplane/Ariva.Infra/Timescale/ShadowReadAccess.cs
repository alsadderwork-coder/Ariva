using Npgsql;

namespace Ariva.Infra.Timescale;

/// <summary>
/// Whether a database login can read a value of the shadow nowcast (ARV-104g1, CWE-269, CWE-863): asked by every host's start-up
/// guard of its own runtime login and by the migration job of the hosts' runtime login. A login can when it is a MEMBER of
/// <c>ariva_validation_reader</c> or of <c>pg_read_all_data</c> (any grant, inherited or not, through any chain of roles; a
/// superuser is a member of every role), or when it may SELECT any value column (every column but the key columns
/// <c>zone_key</c> and <c>minute_utc</c>, which the stream's upsert needs) of <c>queue_minute_shadow</c> or of one of its chunks,
/// which <c>has_column_privilege</c> answers for a grant to the login, to a role it holds or to PUBLIC, for a table-level grant,
/// for the table's owner and for a superuser. Names only reach the server as a bind parameter (CWE-89).
/// </summary>
internal static class ShadowReadAccess
{
    #region Constants

    internal const string Query = """
        WITH login AS (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @login),
             shadow AS (SELECT pg_catalog.to_regclass('public.queue_minute_shadow') AS rel),
             relations AS (SELECT rel AS oid FROM shadow WHERE rel IS NOT NULL
                           UNION SELECT i.inhrelid FROM pg_catalog.pg_inherits i JOIN shadow s ON i.inhparent = s.rel)
        SELECT EXISTS (SELECT FROM login l, pg_catalog.pg_roles g
                        WHERE g.rolname IN ('ariva_validation_reader', 'pg_read_all_data') AND pg_catalog.pg_has_role(l.oid, g.oid, 'MEMBER'))
            OR EXISTS (SELECT FROM login l, relations r JOIN pg_catalog.pg_attribute a ON a.attrelid = r.oid
                        WHERE a.attnum > 0 AND NOT a.attisdropped AND a.attname NOT IN ('zone_key', 'minute_utc')
                          AND pg_catalog.has_column_privilege(l.oid, r.oid, a.attnum, 'SELECT'))
        """;

    #endregion

    #region Methods

    /// <summary>Whether <paramref name="login"/> can read a value of the shadow nowcast (false for a login that does not exist).</summary>
    internal static async Task<bool> CanReadAsync(NpgsqlConnection connection, string login, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(Query, connection);
        command.Parameters.AddWithValue("login", login ?? string.Empty);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>The login of this session (current_user), the one the guard asks about.</summary>
    internal static async Task<string> CurrentLoginAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand("SELECT current_user::text", connection);
        return (string)await command.ExecuteScalarAsync(ct);
    }

    #endregion
}
