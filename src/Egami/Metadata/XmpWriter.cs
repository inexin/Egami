using System.Text;
using System.Xml;

namespace Egami.Metadata;

/// <summary>
/// Writes an <see cref="XmpData"/> as canonical RDF/XML (XMP specification part 1): an xpacket wrapper, x:xmpmeta, one
/// rdf:Description with every property as an element, structures as rdf:parseType="Resource", qualified values with rdf:value,
/// the file's own prefixes where it named them, and 2 KB of padding so other tools can edit in place.
/// </summary>
internal static class XmpWriter
{
    private const string MetaNamespace = "adobe:ns:meta/";
    private const string PacketId = "W5M0MpCehiHzreSzNTczkc9d";
    private const int PaddingLines = 20;

    public static byte[] Write(XmpData data)
    {
        Dictionary<string, string> prefixes = Prefixes(data);
        using MemoryStream stream = new();
        XmlWriterSettings settings = new()
        {
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = true,
            Indent = true,
            IndentChars = " ",
            NewLineChars = "\n",
            ConformanceLevel = ConformanceLevel.Document,
        };
        using (XmlWriter xml = XmlWriter.Create(stream, settings))
        {
            xml.WriteProcessingInstruction("xpacket", $"begin=\"﻿\" id=\"{PacketId}\"");
            xml.WriteStartElement("x", "xmpmeta", MetaNamespace);
            xml.WriteAttributeString("x", "xmptk", MetaNamespace, data.Toolkit ?? "Egami");
            xml.WriteStartElement("rdf", "RDF", XmpNamespaces.Rdf);
            xml.WriteStartElement("rdf", "Description", XmpNamespaces.Rdf);
            xml.WriteAttributeString("rdf", "about", XmpNamespaces.Rdf, data.About ?? string.Empty);
            foreach ((string ns, string prefix) in prefixes.OrderBy(p => p.Value, StringComparer.Ordinal))
            {
                xml.WriteAttributeString("xmlns", prefix, null, ns);
            }

            foreach (XmpField field in data.Fields)
            {
                WriteProperty(xml, prefixes, field.Name, field.Value, field.Qualifiers);
            }

            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        // Padding, then the trailer; written by hand, since the XML writer would indent it.
        StringBuilder tail = new("\n");
        for (int i = 0; i < PaddingLines; i++)
        {
            tail.Append(' ', 100).Append('\n');
        }

        tail.Append("<?xpacket end=\"w\"?>");
        stream.Write(Encoding.UTF8.GetBytes(tail.ToString()));
        return stream.ToArray();
    }

    private static void WriteProperty(XmlWriter xml, Dictionary<string, string> prefixes, XmpName name, object value, XmpQualifiers? qualifiers)
    {
        xml.WriteStartElement(prefixes[name.Namespace], name.Name, name.Namespace);
        WriteValue(xml, prefixes, value, qualifiers);
        xml.WriteEndElement();
    }

    // The inside of a property element or an rdf:li.
    private static void WriteValue(XmlWriter xml, Dictionary<string, string> prefixes, object value, XmpQualifiers? qualifiers)
    {
        if (qualifiers?.Language is string language)
        {
            xml.WriteAttributeString("xml", "lang", XmpNamespaces.Xml, language);
        }

        switch (value)
        {
            case string text when qualifiers is { Others.Count: > 0 }:
                xml.WriteAttributeString("rdf", "parseType", XmpNamespaces.Rdf, "Resource");
                xml.WriteElementString("rdf", "value", XmpNamespaces.Rdf, text);
                foreach ((XmpName qualifier, string q) in qualifiers.Others)
                {
                    xml.WriteElementString(prefixes[qualifier.Namespace], qualifier.Name, qualifier.Namespace, q);
                }

                break;
            case string text when qualifiers?.IsUri == true:
                xml.WriteAttributeString("rdf", "resource", XmpNamespaces.Rdf, text);
                break;
            case string text:
                xml.WriteString(text);
                break;
            case XmpArray array:
                xml.WriteStartElement("rdf", array.Kind switch { XmpArrayKind.Bag => "Bag", XmpArrayKind.Sequence => "Seq", _ => "Alt" }, XmpNamespaces.Rdf);
                foreach ((object item, XmpQualifiers? q) in array.Items)
                {
                    xml.WriteStartElement("rdf", "li", XmpNamespaces.Rdf);
                    WriteValue(xml, prefixes, item, q);
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
                break;
            case XmpStruct structure:
                xml.WriteAttributeString("rdf", "parseType", XmpNamespaces.Rdf, "Resource");
                foreach (XmpField field in structure.Fields)
                {
                    WriteProperty(xml, prefixes, field.Name, field.Value, field.Qualifiers);
                }

                break;
        }
    }

    // A prefix for every namespace the properties use: the file's, else the usual one, else ns1, ns2 and so on, each unique.
    private static Dictionary<string, string> Prefixes(XmpData data)
    {
        HashSet<string> used = [];
        Collect(data, used);
        Dictionary<string, string> prefixes = [];
        HashSet<string> taken = ["x", "rdf", "xml", "xmlns"];
        int next = 1;
        foreach (string ns in used.Order(StringComparer.Ordinal))
        {
            string? prefix = data.Prefixes.GetValueOrDefault(ns) ?? XmpNamespaces.DefaultPrefix(ns);
            if (prefix is null || !XmlConvert.IsStartNCNameChar(prefix[0]) || !taken.Add(prefix))
            {
                do
                {
                    prefix = "ns" + next++;
                }
                while (!taken.Add(prefix));
            }

            prefixes[ns] = prefix;
        }

        return prefixes;
    }

    private static void Collect(object value, HashSet<string> used)
    {
        switch (value)
        {
            case XmpStruct structure:
                foreach (XmpField field in structure.Fields)
                {
                    used.Add(field.Name.Namespace);
                    Collect(field.Value, used);
                    foreach ((XmpName q, string _) in field.Qualifiers?.Others ?? [])
                    {
                        used.Add(q.Namespace);
                    }
                }

                break;
            case XmpArray array:
                foreach ((object item, XmpQualifiers? q) in array.Items)
                {
                    Collect(item, used);
                    foreach ((XmpName name, string _) in q?.Others ?? [])
                    {
                        used.Add(name.Namespace);
                    }
                }

                break;
        }
    }
}
