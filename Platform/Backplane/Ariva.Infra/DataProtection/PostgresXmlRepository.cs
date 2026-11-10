using System.Xml;
using System.Xml.Linq;
using Ariva.Infra.Settings;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;

namespace Ariva.Infra.DataProtection;

/// <summary>
/// Stores the Data Protection key ring in PostgreSQL (table data_protection_key, script 0002) so every host shares
/// it (ARV-008). Elements arrive here already encrypted with the key-protection certificate. Parameterised SQL only;
/// the XML is parsed without DTDs or external resolution (CWE-611). ARV-242 (CWE-400): the interface is synchronous, but the
/// connection is opened and read with Npgsql's asynchronous calls, waited on here: Npgsql's own timer then ends a database that
/// accepts and never answers at the connection timeout, where its synchronous open could wait for ever (less than a millisecond
/// of that timeout left becomes no socket timeout at all). A bounded run reaches this when it unprotects a stored secret.
/// ARV-294 (CWE-400): a reload of the key ring can come inside any transaction of a host (a secret protected or read there), which
/// waits meanwhile, so each statement here waits at most <see cref="KeyRingCommandSeconds"/> and each connection opens within at most
/// <see cref="KeyRingConnectSeconds"/>, whatever <c>Database:CommandTimeoutSeconds</c> and <c>Database:ConnectTimeoutSeconds</c> allow:
/// the table holds a handful of rows. Both are the hosts' own values at the shipped defaults (30 s and 15 s), so nothing changes there.
/// </summary>
internal sealed class PostgresXmlRepository(DatabaseSettings settings) : IXmlRepository
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

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = PostgresConnections.Create(KeyRingConnectionString(settings));
        connection.OpenAsync().GetAwaiter().GetResult();
        using var command = new NpgsqlCommand("SELECT xml FROM data_protection_key ORDER BY created_on, friendly_name", connection);
        command.CommandTimeout = CommandTimeoutSeconds(settings);
        using var reader = command.ExecuteReaderAsync().GetAwaiter().GetResult();

        var elements = new List<XElement>();
        while (reader.ReadAsync().GetAwaiter().GetResult())
        {
            using var text = new StringReader(reader.GetString(0));
            using var xml = XmlReader.Create(text, SafeXml);
            elements.Add(XElement.Load(xml));
        }

        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendlyName);

        using var connection = PostgresConnections.Create(KeyRingConnectionString(settings));
        connection.OpenAsync().GetAwaiter().GetResult();
        using var command = new NpgsqlCommand(
            "INSERT INTO data_protection_key (friendly_name, xml) VALUES (@name, @xml) ON CONFLICT (friendly_name) DO NOTHING", connection);
        command.CommandTimeout = CommandTimeoutSeconds(settings);
        command.Parameters.AddWithValue("name", friendlyName);
        command.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting));
        command.ExecuteNonQueryAsync().GetAwaiter().GetResult();
    }
}
