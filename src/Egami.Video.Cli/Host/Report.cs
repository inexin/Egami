using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;

namespace Egami.Video.Cli.Host;

/// <summary>
/// A report as a tree of named fields, written as JSON or XML with the same fields (Video CLI design Q6): an object holds
/// fields in order; a field's value is text, a number, a flag, null, an object or a list of objects.
/// </summary>
internal sealed class ReportObject
{
    private readonly List<KeyValuePair<string, object?>> _fields = [];

    /// <summary>Gets the fields, in the order they were added.</summary>
    public IReadOnlyList<KeyValuePair<string, object?>> Fields => _fields;

    /// <summary>Adds a field; a <see langword="null"/> value is written as JSON null and left out of XML.</summary>
    public ReportObject Add(string name, object? value)
    {
        _fields.Add(new(name, value));
        return this;
    }

    /// <summary>Adds a field only when the value is not <see langword="null"/>.</summary>
    public ReportObject AddIf(string name, object? value) => value is null ? this : Add(name, value);

    /// <summary>Adds an object field and returns it.</summary>
    public ReportObject Object(string name)
    {
        ReportObject child = new();
        _fields.Add(new(name, child));
        return child;
    }

    /// <summary>Adds a list field and returns it.</summary>
    public List<ReportObject> List(string name)
    {
        List<ReportObject> list = [];
        _fields.Add(new(name, list));
        return list;
    }

    /// <summary>Finds a field's value, or <see langword="null"/>.</summary>
    public object? this[string name] => _fields.FirstOrDefault(f => f.Key == name).Value;
}

/// <summary>Writes report trees as JSON (indented, no reflection) or XML.</summary>
internal static class ReportWriter
{
    private static readonly JsonWriterOptions JsonOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Writes a report as one JSON document, ending with a newline.</summary>
    public static void Json(TextWriter output, ReportObject report)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer, JsonOptions))
        {
            WriteJson(json, report);
        }

        output.Write(Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal));
        output.Write('\n');
    }

    /// <summary>Writes a report as XML: <paramref name="root"/> is the document element; list items are named <paramref name="itemNames"/> gives, else "item".</summary>
    public static void Xml(TextWriter output, string root, ReportObject report, IReadOnlyDictionary<string, string> itemNames)
    {
        StringBuilder text = new();
        using (XmlWriter xml = XmlWriter.Create(text, new XmlWriterSettings { Indent = true, NewLineChars = "\n", OmitXmlDeclaration = false, Encoding = Encoding.UTF8 }))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement(root);
            WriteXmlFields(xml, report, itemNames);
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        output.Write(text.ToString().Replace("utf-16", "utf-8", StringComparison.Ordinal));
        output.Write('\n');
    }

    private static void WriteJson(Utf8JsonWriter json, ReportObject report)
    {
        json.WriteStartObject();
        foreach ((string name, object? value) in report.Fields)
        {
            json.WritePropertyName(name);
            WriteJsonValue(json, value);
        }

        json.WriteEndObject();
    }

    private static void WriteJsonValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null: json.WriteNullValue(); break;
            case string text: json.WriteStringValue(text); break;
            case bool flag: json.WriteBooleanValue(flag); break;
            case int number: json.WriteNumberValue(number); break;
            case long number: json.WriteNumberValue(number); break;
            case double number when double.IsFinite(number): json.WriteNumberValue(number); break;
            case double: json.WriteNullValue(); break;
            case ReportObject child: WriteJson(json, child); break;
            case List<ReportObject> list:
                json.WriteStartArray();
                foreach (ReportObject item in list)
                {
                    WriteJson(json, item);
                }

                json.WriteEndArray();
                break;
            case IReadOnlyDictionary<string, string> map:
                json.WriteStartObject();
                foreach ((string key, string text) in map)
                {
                    json.WriteString(key, text);
                }

                json.WriteEndObject();
                break;
            case IEnumerable<string> texts:
                json.WriteStartArray();
                foreach (string text in texts)
                {
                    json.WriteStringValue(text);
                }

                json.WriteEndArray();
                break;
            default: json.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }

    private static void WriteXmlFields(XmlWriter xml, ReportObject report, IReadOnlyDictionary<string, string> itemNames)
    {
        foreach ((string name, object? value) in report.Fields)
        {
            switch (value)
            {
                case null:
                    break;
                case ReportObject child:
                    xml.WriteStartElement(name);
                    WriteXmlFields(xml, child, itemNames);
                    xml.WriteEndElement();
                    break;
                case List<ReportObject> list:
                    xml.WriteStartElement(name);
                    string item = itemNames.TryGetValue(name, out string? itemName) ? itemName : "item";
                    foreach (ReportObject entry in list)
                    {
                        xml.WriteStartElement(item);
                        WriteXmlFields(xml, entry, itemNames);
                        xml.WriteEndElement();
                    }

                    xml.WriteEndElement();
                    break;
                case IReadOnlyDictionary<string, string> map:
                    xml.WriteStartElement(name);
                    foreach ((string key, string text) in map)
                    {
                        xml.WriteStartElement("tag");
                        xml.WriteAttributeString("key", key);
                        xml.WriteString(text);
                        xml.WriteEndElement();
                    }

                    xml.WriteEndElement();
                    break;
                case IEnumerable<string> texts when value is not string:
                    xml.WriteStartElement(name);
                    foreach (string text in texts)
                    {
                        xml.WriteElementString("value", text);
                    }

                    xml.WriteEndElement();
                    break;
                default:
                    xml.WriteElementString(name, Text(value));
                    break;
            }
        }
    }

    /// <summary>A field value as text, for XML and CSV: invariant numbers, true and false.</summary>
    public static string Text(object? value)
    {
        return value switch
        {
            null => string.Empty,
            bool flag => flag ? "true" : "false",
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    /// <summary>Writes rows as CSV with a header line; fields with commas, quotes or line breaks are quoted.</summary>
    public static void Csv(TextWriter output, IReadOnlyList<ReportObject> rows)
    {
        List<string> columns = [];
        foreach (ReportObject row in rows)
        {
            foreach ((string name, object? value) in row.Fields)
            {
                if (value is not ReportObject and not List<ReportObject> && !columns.Contains(name))
                {
                    columns.Add(name);
                }
            }
        }

        output.Write(string.Join(',', columns.Select(Quote)) + "\n");
        foreach (ReportObject row in rows)
        {
            output.Write(string.Join(',', columns.Select(c => Quote(Text(row[c])))) + "\n");
        }
    }

    private static string Quote(string text)
    {
        return text.AsSpan().IndexOfAny(",\"\n\r") >= 0 ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : text;
    }
}
