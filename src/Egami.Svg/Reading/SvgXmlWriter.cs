using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

using Egami.Drawing;

namespace Egami.Svg.Reading;

/// <summary>
/// Writes the element tree back as XML: the declaration and DOCTYPE the file had, comments and processing instructions around
/// the root, then every element with its attributes (namespace declarations included, so prefixes come back as they were),
/// text, CDATA, comments and processing instructions in order. Entities were expanded on reading, so they are written out.
/// </summary>
internal static class SvgXmlWriter
{
    private static readonly string LineFeed = new((char)10, 1);

    public static void Write(SvgDocument document, Stream stream, SvgSaveOptions options, SvgXmlContent? source)
    {
        Stream output = options.Gzip ? new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true) : stream;
        try
        {
            XmlWriterSettings settings = new()
            {
                Encoding = new UTF8Encoding(false),
                Indent = false,
                OmitXmlDeclaration = source?.Declaration is null,
                CloseOutput = false,
                NewLineHandling = NewLineHandling.Entitize,
                NamespaceHandling = NamespaceHandling.OmitDuplicates,
            };

            using (XmlWriter writer = XmlWriter.Create(output, settings))
            {
                if (source?.Declaration is not null)
                {
                    writer.WriteStartDocument();
                }

                if (source?.DocumentType is SvgDocumentType type)
                {
                    writer.WriteDocType(type.Name, type.PublicId, type.SystemId, type.InternalSubset);
                }

                foreach (SvgContent item in source?.Prolog ?? [])
                {
                    WriteContent(writer, item, options);
                }

                WriteElement(writer, document.Root, options);
                foreach (SvgContent item in source?.Epilog ?? [])
                {
                    WriteContent(writer, item, options);
                }

                writer.WriteEndDocument();
            }
        }
        finally
        {
            if (!ReferenceEquals(output, stream))
            {
                output.Dispose();
            }
        }
    }

    // Indentation goes only where whitespace means nothing: between the children of an element that holds no text, outside
    // text content elements (inside them, a line break between two tspans would draw as a space).
    private static void WriteElement(XmlWriter writer, SvgElement element, SvgSaveOptions options)
    {
        Stack<(SvgElement Element, int Next, bool Indent)> open = new();
        Start(writer, element, options);
        open.Push((element, 0, options.Indent && Indentable(element)));
        while (open.Count > 0)
        {
            (SvgElement current, int next, bool indent) = open.Pop();
            SvgContentList children = current.Children;
            bool descended = false;
            for (int i = next; i < children.Count; i++)
            {
                if (indent)
                {
                    writer.WriteWhitespace(LineFeed + new string(' ', 2 * open.Count + 2));
                }

                if (children[i] is SvgElement child)
                {
                    open.Push((current, i + 1, indent));
                    Start(writer, child, options);
                    open.Push((child, 0, indent && Indentable(child)));
                    descended = true;
                    break;
                }

                WriteContent(writer, children[i], options);
            }

            if (!descended)
            {
                if (children.Count == 0)
                {
                    writer.WriteEndElement();
                }
                else
                {
                    if (indent)
                    {
                        writer.WriteWhitespace(LineFeed + new string(' ', 2 * open.Count));
                    }

                    writer.WriteFullEndElement();
                }
            }
        }
    }

    private static bool Indentable(SvgElement element)
    {
        if (element.IsSvg && element.Name is "text" or "tspan" or "textPath" or "title" or "desc" or "style" or "script" or "foreignObject" or "a" or "flowRoot" or "flowPara")
        {
            return false;
        }

        foreach (SvgContent item in element.Children)
        {
            if (item is SvgTextContent)
            {
                return false;
            }
        }

        return true;
    }

    private static void Start(XmlWriter writer, SvgElement element, SvgSaveOptions options)
    {
        writer.WriteStartElement(element.Prefix, element.Name, element.Namespace ?? "");
        foreach (SvgAttribute attribute in element.Attributes)
        {
            string value = Format(attribute, options);
            if (string.Equals(attribute.Namespace, SvgNamespaces.Xmlns, StringComparison.Ordinal))
            {
                if (string.Equals(attribute.Name, "xmlns", StringComparison.Ordinal))
                {
                    if (string.Equals(value, element.Namespace ?? "", StringComparison.Ordinal) || element.Prefix is not null)
                    {
                        writer.WriteAttributeString("xmlns", SvgNamespaces.Xmlns, value);
                    }
                }
                else if (!string.Equals(attribute.Name, element.Prefix, StringComparison.Ordinal) || string.Equals(value, element.Namespace, StringComparison.Ordinal))
                {
                    writer.WriteAttributeString("xmlns", attribute.Name, SvgNamespaces.Xmlns, value);
                }

                continue;
            }

            if (attribute.Namespace is null)
            {
                writer.WriteAttributeString(attribute.Name, value);
            }
            else
            {
                string? prefix = attribute.Prefix ?? writer.LookupPrefix(attribute.Namespace) ?? KnownPrefix(attribute.Namespace);
                writer.WriteAttributeString(prefix, attribute.Name, attribute.Namespace, value);
            }
        }
    }

    private static void WriteContent(XmlWriter writer, SvgContent item, SvgSaveOptions options)
    {
        switch (item)
        {
            case SvgTextContent { IsCData: true } cdata:
                string data = cdata.Value;
                int split;
                while ((split = data.IndexOf("]]>", StringComparison.Ordinal)) >= 0)
                {
                    writer.WriteCData(data[..(split + 2)]);
                    data = data[(split + 2)..];
                }

                writer.WriteCData(data);
                break;
            case SvgTextContent text:
                if (string.IsNullOrWhiteSpace(text.Value) && text.Value.Length > 0 && IsXmlSpace(text.Value))
                {
                    writer.WriteWhitespace(text.Value);
                }
                else
                {
                    writer.WriteString(text.Value);
                }

                break;
            case SvgComment comment:
                writer.WriteComment(SafeComment(comment.Value));
                break;
            case SvgProcessingInstruction pi:
                writer.WriteProcessingInstruction(pi.Target, pi.Data.Replace("?>", "? >", StringComparison.Ordinal));
                break;
            case SvgElement element:
                WriteElement(writer, element, options);
                break;
        }
    }

    private static string Format(SvgAttribute attribute, SvgSaveOptions options)
    {
        if (attribute.PathData is Path path)
        {
            return path.ToSvg(SvgPathFormat.Default with { Precision = options.Precision });
        }

        if (attribute.Number is double number)
        {
            string text = Math.Round(number, options.Precision, MidpointRounding.AwayFromZero).ToString(options.Precision == 0 ? "0" : "0." + new string('#', options.Precision), CultureInfo.InvariantCulture);
            return text == "-0" ? "0" : text;
        }

        return attribute.Value;
    }

    private static string? KnownPrefix(string ns) => ns switch
    {
        SvgNamespaces.XLink => "xlink",
        SvgNamespaces.Xml => "xml",
        _ => null,
    };

    private static bool IsXmlSpace(string text)
    {
        foreach (char c in text)
        {
            if (c is not (' ' or '\t' or '\r' or '\n'))
            {
                return false;
            }
        }

        return true;
    }

    // XML forbids "--" inside a comment and a comment ending in "-".
    private static string SafeComment(string text)
    {
        string safe = text.Replace("--", "- -", StringComparison.Ordinal);
        return safe.EndsWith('-') ? safe + " " : safe;
    }
}
