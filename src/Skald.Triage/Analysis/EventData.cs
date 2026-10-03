using System.Xml.Linq;

namespace Skald.Triage;

public static class EventData
{
    // Named <Data> fields of a Windows event record; empty when the record has no XML (for example WMI copies).
    public static IReadOnlyDictionary<string, string> Named(string raw)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var element in XElement.Parse(raw).Descendants().Where(item => item.Name.LocalName == "Data"))
                if ((string?)element.Attribute("Name") is { Length: > 0 } name) fields.TryAdd(name, element.Value.Trim());
        }
        catch (System.Xml.XmlException) { }
        return fields;
    }
}
