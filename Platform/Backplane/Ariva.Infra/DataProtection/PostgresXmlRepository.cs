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
/// </summary>
internal sealed class PostgresXmlRepository(DatabaseSettings settings) : IXmlRepository
{
    private static readonly XmlReaderSettings SafeXml = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null
    };

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = PostgresConnections.Create(settings.BuildConnectionString());
        connection.OpenAsync().GetAwaiter().GetResult();
        using var command = new NpgsqlCommand("SELECT xml FROM data_protection_key ORDER BY created_on, friendly_name", connection);
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

        using var connection = PostgresConnections.Create(settings.BuildConnectionString());
        connection.OpenAsync().GetAwaiter().GetResult();
        using var command = new NpgsqlCommand(
            "INSERT INTO data_protection_key (friendly_name, xml) VALUES (@name, @xml) ON CONFLICT (friendly_name) DO NOTHING", connection);
        command.Parameters.AddWithValue("name", friendlyName);
        command.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting));
        command.ExecuteNonQueryAsync().GetAwaiter().GetResult();
    }
}
