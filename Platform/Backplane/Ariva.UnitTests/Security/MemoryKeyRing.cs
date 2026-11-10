using System.Xml.Linq;
using Ariva.Infra.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Ariva.UnitTests.Security;

/// <summary>
/// The table data_protection_key in memory (ARV-211, scripts 0002 and 0073): rows as text with their row signature, one per
/// friendly name (a second insert under a name does nothing, as ON CONFLICT DO NOTHING). Through <see cref="IKeyRingRowStore"/>
/// it is what the hosts read and write behind the trusted filter; through <see cref="IXmlRepository"/> it is a writer with INSERT
/// alone or a host before ARV-211: the rows it adds carry no row signature.
/// </summary>
internal sealed class MemoryKeyRing : IXmlRepository, IKeyRingRowStore
{
    private readonly List<KeyRingRow> _rows = [];

    public IReadOnlyList<KeyRingRow> GetAllRows()
    {
        lock (_rows)
            return [.. _rows];
    }

    public void StoreRow(KeyRingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_rows)
        {
            if (_rows.All(r => !string.Equals(r.FriendlyName, row.FriendlyName, StringComparison.Ordinal)))
                _rows.Add(row);
        }
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (_rows)
            return [.. _rows.Select(r => XElement.Parse(r.Xml))];
    }

    public void StoreElement(XElement element, string friendlyName) =>
        StoreRow(new KeyRingRow(friendlyName, element.ToString(SaveOptions.DisableFormatting), null));

    /// <summary>Adds a row as a writer with INSERT could: any name, text and signature.</summary>
    public void Add(KeyRingRow row) => StoreRow(row);

    /// <summary>
    /// Changes the stored rows' text in place, as a database writer with UPDATE could (the runtime role has none): each row keeps
    /// its signature, which nobody without the signing key can remake.
    /// </summary>
    public void Mutate(Action<List<XElement>> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_rows)
        {
            var elements = _rows.Select(r => XElement.Parse(r.Xml)).ToList();
            var before = elements.Select(e => e.ToString(SaveOptions.DisableFormatting)).ToList();
            change(elements);
            for (var i = 0; i < _rows.Count; i++)
            {
                var after = elements[i].ToString(SaveOptions.DisableFormatting);
                if (!string.Equals(after, before[i], StringComparison.Ordinal))
                    _rows[i] = _rows[i] with { Xml = after };
            }
        }
    }
}
