using System.Xml;
using System.Xml.Linq;
using Ariva.Infra.Settings;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;

namespace Ariva.Infra.DataProtection;

/// <summary>
/// Stores the Data Protection key ring in PostgreSQL (table data_protection_key, script 0002) so every host shares
/// it (ARV-008). Elements arrive here already encrypted with the key-protection certificate. Parameterised SQL only;
/// the XML is parsed without DTDs or external resolution (CWE-611).
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
        using var connection = new NpgsqlConnection(settings.BuildConnectionString());
        connection.Open();
        using var command = new NpgsqlCommand("SELECT xml FROM data_protection_key ORDER BY created_on, friendly_name", connection);
        using var reader = command.ExecuteReader();

        var elements = new List<XElement>();
        while (reader.Read())
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

        using var connection = new NpgsqlConnection(settings.BuildConnectionString());
        connection.Open();
        using var command = new NpgsqlCommand(
            "INSERT INTO data_protection_key (friendly_name, xml) VALUES (@name, @xml) ON CONFLICT (friendly_name) DO NOTHING", connection);
        command.Parameters.AddWithValue("name", friendlyName);
        command.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting));
        command.ExecuteNonQuery();
    }
}
