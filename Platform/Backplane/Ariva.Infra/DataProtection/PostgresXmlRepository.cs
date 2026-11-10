using System.Xml;
using System.Xml.Linq;
using Ariva.Infra.Settings;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;

namespace Ariva.Infra.DataProtection;

/// <summary>
/// One row of the key ring as stored (ARV-211, script 0073): its friendly name, the element's exact text and the row
/// signature the hosts write (<see cref="TrustedKeyRingRepository"/>), null on a row written before ARV-211.
/// </summary>
public sealed record KeyRingRow(string FriendlyName, string Xml, string Signature);

/// <summary>
/// The key ring's rows as text, before anything parses them (ARV-211): the trusted filter checks a row's signature over
/// its exact text, then parses it.
/// </summary>
public interface IKeyRingRowStore
{
    IReadOnlyList<KeyRingRow> GetAllRows();

    void StoreRow(KeyRingRow row);
}

/// <summary>
/// Stores the Data Protection key ring in PostgreSQL (table data_protection_key, scripts 0002 and 0073) so every host shares
/// it (ARV-008). Elements arrive here already encrypted with the key-protection certificate, and since ARV-211 with the row
/// signature <see cref="TrustedKeyRingRepository"/> made. Parameterised SQL only; the XML is parsed without DTDs or external
/// resolution (CWE-611). ARV-242 (CWE-400): the interface is synchronous, but the connection is opened and read with Npgsql's
/// asynchronous calls, waited on here: Npgsql's own timer then ends a database that accepts and never answers at the
/// connection timeout, where its synchronous open could wait for ever (less than a millisecond of that timeout left becomes no
/// socket timeout at all). A bounded run reaches this when it unprotects a stored secret.
/// ARV-294 (CWE-400): a reload of the key ring can come inside any transaction of a host (a secret protected or read there), which
/// waits meanwhile, so each statement here waits at most <see cref="KeyRingCommandSeconds"/> and each connection opens within at most
/// <see cref="KeyRingConnectSeconds"/>, whatever <c>Database:CommandTimeoutSeconds</c> and <c>Database:ConnectTimeoutSeconds</c> allow:
/// the table holds a handful of rows. Both are the hosts' own values at the shipped defaults (30 s and 15 s), so nothing changes there.
/// </summary>
internal sealed class PostgresXmlRepository(DatabaseSettings settings) : IXmlRepository, IKeyRingRowStore
{
    /// <summary>The longest one statement on the key ring waits, in seconds (the hosts' command timeout when it is shorter).</summary>
    public const int KeyRingCommandSeconds = 30;

    /// <summary>The longest a connection to the key ring takes to open, in seconds (the hosts' connection timeout when it is shorter).</summary>
    public const int KeyRingConnectSeconds = 50;

    /// <summary>The command timeout of a statement here: <c>Database:CommandTimeoutSeconds</c>, at most <see cref="KeyRingCommandSeconds"/>.</summary>
    public static int CommandTimeoutSeconds(DatabaseSettings database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return Math.Min(database.CommandTimeoutSeconds, KeyRingCommandSeconds);
    }

    /// <summary>The connection timeout of a connection here: <c>Database:ConnectTimeoutSeconds</c>, at most <see cref="KeyRingConnectSeconds"/>.</summary>
    public static int ConnectTimeoutSeconds(DatabaseSettings database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return Math.Min(database.ConnectTimeoutSeconds, KeyRingConnectSeconds);
    }

    /// <summary>
    /// The hosts' runtime connection string, unchanged (the same pool) while <c>Database:ConnectTimeoutSeconds</c> is within
    /// <see cref="KeyRingConnectSeconds"/>; above it, the same with the connection timeout bounded.
    /// </summary>
    internal static string KeyRingConnectionString(DatabaseSettings database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var connectionString = database.BuildConnectionString();
        return database.ConnectTimeoutSeconds <= KeyRingConnectSeconds
            ? connectionString
            : new NpgsqlConnectionStringBuilder(connectionString) { Timeout = KeyRingConnectSeconds }.ConnectionString;
    }

    private static readonly XmlReaderSettings SafeXml = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null
    };

    public IReadOnlyList<KeyRingRow> GetAllRows()
    {
        using var connection = PostgresConnections.Create(KeyRingConnectionString(settings));
        connection.OpenAsync().GetAwaiter().GetResult();
        using var command = new NpgsqlCommand("SELECT friendly_name, xml, signature FROM data_protection_key ORDER BY created_on, friendly_name", connection);
        command.CommandTimeout = CommandTimeoutSeconds(settings);
        using var reader = command.ExecuteReaderAsync().GetAwaiter().GetResult();

        var rows = new List<KeyRingRow>();
        while (reader.ReadAsync().GetAwaiter().GetResult())
            rows.Add(new KeyRingRow(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return rows;
    }

    public void StoreRow(KeyRingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.FriendlyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.Xml);

        using var connection = PostgresConnections.Create(KeyRingConnectionString(settings));
        connection.OpenAsync().GetAwaiter().GetResult();
        using var command = new NpgsqlCommand(
            "INSERT INTO data_protection_key (friendly_name, xml, signature) VALUES (@name, @xml, @signature) ON CONFLICT (friendly_name) DO NOTHING", connection);
        command.CommandTimeout = CommandTimeoutSeconds(settings);
        command.Parameters.AddWithValue("name", row.FriendlyName);
        command.Parameters.AddWithValue("xml", row.Xml);
        command.Parameters.AddWithValue("signature", (object)row.Signature ?? DBNull.Value);
        command.ExecuteNonQueryAsync().GetAwaiter().GetResult();
    }

    /// <summary>Every row parsed, signed or not: for tools and tests; the hosts read the ring through the trusted filter.</summary>
    public IReadOnlyCollection<XElement> GetAllElements() => [.. GetAllRows().Select(row => Parse(row.Xml))];

    /// <summary>A row without a signature, as a host before ARV-211 wrote it; the hosts store through the trusted filter.</summary>
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        StoreRow(new KeyRingRow(friendlyName, element.ToString(SaveOptions.DisableFormatting), null));
    }

    /// <summary>A row's text as an element, without DTDs or external resolution (CWE-611).</summary>
    internal static XElement Parse(string xml)
    {
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, SafeXml);
        return XElement.Load(reader);
    }
}
