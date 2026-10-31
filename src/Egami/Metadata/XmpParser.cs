using System.Xml;

namespace Egami.Metadata;

/// <summary>
/// Reads an XMP packet's RDF/XML (XMP specification part 1, chapter 7) into an <see cref="XmpData"/>: properties as elements or
/// as attributes of rdf:Description, several rdf:Description blocks, rdf:parseType="Resource" structures, structures as nested
/// rdf:Description or as attributes of an empty property element, rdf:resource values, arrays of any of these, and qualifiers
/// (rdf:value). The XML reader prohibits DTDs and resolves nothing.
/// </summary>
internal static class XmpParser
{
    private const int MaxDepth = 64;
    private const string MetaNamespace = "adobe:ns:meta/";
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = false,
        MaxCharactersFromEntities = 1024,
    };

    /// <summary>Fills <paramref name="data"/> from the packet; returns a sentence saying what was wrong, or null.</summary>
    public static string? Read(byte[] packet, XmpData data)
    {
        Node root;
        try
        {
            using MemoryStream stream = new(packet, writable: false);
            using XmlReader reader = XmlReader.Create(stream, Settings);
            root = ReadTree(reader, data.Prefixes) ?? throw new XmlException("The packet has no XML element.");
        }
        catch (XmlException e)
        {
            return $"The XMP packet is not well-formed XML ({e.Message}).";
        }
        catch (InvalidDataException e)
        {
            return $"The XMP packet cannot be read: {e.Message}";
        }

        if (root.Is(MetaNamespace, "xmpmeta"))
        {
            data.Toolkit = root.Attribute(MetaNamespace, "xmptk");
        }

        Node? rdf = Find(root, XmpNamespaces.Rdf, "RDF", 0);
        if (rdf is null)
        {
            return "The XMP packet has no rdf:RDF element.";
        }

        foreach (Node description in rdf.Children)
        {
            if (description.Is(XmpNamespaces.Rdf, "Description"))
            {
                data.About ??= description.Attribute(XmpNamespaces.Rdf, "about");
                ReadFields(description, data, 0);
            }
        }

        return null;
    }

    private static Node? Find(Node node, string ns, string name, int depth)
    {
        if (node.Is(ns, name))
        {
            return node;
        }

        if (depth > 2)
        {
            return null;
        }

        foreach (Node child in node.Children)
        {
            if (Find(child, ns, name, depth + 1) is Node found)
            {
                return found;
            }
        }

        return null;
    }

    // The properties of an rdf:Description (or a parseType="Resource" element): its property attributes, then its children.
    private static void ReadFields(Node node, XmpStruct target, int depth)
    {
        foreach ((string ns, string name, string value) in node.PropertyAttributes())
        {
            target.AddParsed(new XmpName(ns, name), value, null);
        }

        foreach (Node child in node.Children)
        {
            if (child.Namespace == XmpNamespaces.Rdf || child.Namespace.Length == 0)
            {
                continue;
            }

            (object value, XmpQualifiers? qualifiers) = ReadValue(child, depth + 1);
            target.AddParsed(new XmpName(child.Namespace, child.Name), value, qualifiers);
        }
    }

    // A property element or an rdf:li: a simple value, an array, a structure or a qualified value.
    private static (object Value, XmpQualifiers? Qualifiers) ReadValue(Node node, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException("The XMP packet nests deeper than " + MaxDepth + " levels.");
        }

        string? language = node.Attribute(XmpNamespaces.Xml, "lang");
        if (node.Attribute(XmpNamespaces.Rdf, "resource") is string uri)
        {
            return (uri, new XmpQualifiers(language, [], true));
        }

        string? parseType = node.Attribute(XmpNamespaces.Rdf, "parseType");
        if (parseType == "Resource")
        {
            return Structure(node, language, depth);
        }

        Node? only = node.Children.Count == 1 ? node.Children[0] : null;
        if (only is not null && only.Namespace == XmpNamespaces.Rdf && only.Name is "Bag" or "Seq" or "Alt")
        {
            XmpArray array = new(only.Name switch { "Bag" => XmpArrayKind.Bag, "Seq" => XmpArrayKind.Sequence, _ => XmpArrayKind.Alternative });
            foreach (Node li in only.Children)
            {
                if (li.Is(XmpNamespaces.Rdf, "li"))
                {
                    (object item, XmpQualifiers? q) = ReadValue(li, depth + 1);
                    array.AddParsed(item, q);
                }
            }

            return (array, Qualified(language));
        }

        if (only is not null && only.Is(XmpNamespaces.Rdf, "Description"))
        {
            return Structure(only, language, depth);
        }

        if (node.Children.Count > 0 || node.HasPropertyAttributes() || node.Attribute(XmpNamespaces.Rdf, "value") is not null)
        {
            return Structure(node, language, depth);
        }

        return (node.Text, Qualified(language));
    }

    // Fields of a structure; with rdf:value it is a simple value whose other fields are qualifiers.
    private static (object Value, XmpQualifiers? Qualifiers) Structure(Node node, string? language, int depth)
    {
        XmpStruct structure = new();
        ReadFields(node, structure, depth);
        string? value = node.Attribute(XmpNamespaces.Rdf, "value") ?? node.Children.Find(c => c.Is(XmpNamespaces.Rdf, "value"))?.Text;
        if (value is null)
        {
            return (structure, Qualified(language));
        }

        List<KeyValuePair<XmpName, string>> others = [];
        string? lang = language;
        foreach (XmpField field in structure.Fields)
        {
            if (field.Value is string text)
            {
                if (field.Name.Namespace == XmpNamespaces.Xml && field.Name.Name == "lang")
                {
                    lang = text;
                }
                else
                {
                    others.Add(new(field.Name, text));
                }
            }
        }

        return (value, new XmpQualifiers(lang, others, false));
    }

    private static XmpQualifiers? Qualified(string? language) => language is null ? null : new XmpQualifiers(language, [], false);

    // The element tree, with each element's text; namespace prefixes are noted for writing back.
    private static Node? ReadTree(XmlReader reader, Dictionary<string, string> prefixes)
    {
        Stack<Node> open = [];
        Node? root = null;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    {
                        if (open.Count > MaxDepth * 3)
                        {
                            throw new InvalidDataException("The XMP packet nests too deeply.");
                        }

                        Node node = new(reader.NamespaceURI, reader.LocalName);
                        Note(prefixes, reader.NamespaceURI, reader.Prefix);
                        bool empty = reader.IsEmptyElement;
                        if (reader.MoveToFirstAttribute())
                        {
                            do
                            {
                                if (reader.NamespaceURI == XmlnsNamespace)
                                {
                                    Note(prefixes, reader.Value, reader.Prefix.Length == 0 ? string.Empty : reader.LocalName);
                                }
                                else
                                {
                                    node.AddAttribute(reader.NamespaceURI, reader.LocalName, reader.Value);
                                    Note(prefixes, reader.NamespaceURI, reader.Prefix);
                                }
                            }
                            while (reader.MoveToNextAttribute());
                            reader.MoveToElement();
                        }

                        if (open.TryPeek(out Node? parent))
                        {
                            parent.AddChild(node);
                        }
                        else
                        {
                            root ??= node;
                        }

                        if (!empty)
                        {
                            open.Push(node);
                        }

                        break;
                    }

                case XmlNodeType.EndElement:
                    open.Pop();
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace:
                    if (open.TryPeek(out Node? owner))
                    {
                        owner.AppendText(reader.Value);
                    }

                    break;
            }
        }

        return root;
    }

    private static void Note(Dictionary<string, string> prefixes, string ns, string prefix)
    {
        if (ns.Length > 0 && prefix.Length > 0 && ns != XmpNamespaces.Xml)
        {
            prefixes.TryAdd(ns, prefix);
        }
    }

    private sealed class Node(string ns, string name)
    {
        private static readonly List<(string, string, string)> NoAttributes = [];
        private static readonly List<Node> NoChildren = [];

        private string? _text;
        private System.Text.StringBuilder? _more;
        private List<(string Namespace, string Name, string Value)>? _attributes;
        private List<Node>? _children;

        public string Namespace { get; } = ns;

        public string Name { get; } = name;

        public List<(string Namespace, string Name, string Value)> Attributes => _attributes ?? NoAttributes;

        public List<Node> Children => _children ?? NoChildren;

        public string Text => _more?.ToString() ?? _text ?? string.Empty;

        public bool Is(string ns, string name) => Namespace == ns && Name == name;

        public string? Attribute(string ns, string name)
        {
            foreach ((string Namespace, string Name, string Value) a in Attributes)
            {
                if (a.Namespace == ns && a.Name == name)
                {
                    return a.Value;
                }
            }

            return null;
        }

        public IEnumerable<(string Namespace, string Name, string Value)> PropertyAttributes()
        {
            foreach ((string Namespace, string Name, string Value) a in Attributes)
            {
                if (a.Namespace.Length > 0 && a.Namespace != XmpNamespaces.Rdf && a.Namespace != XmpNamespaces.Xml)
                {
                    yield return a;
                }
            }
        }

        public bool HasPropertyAttributes()
        {
            foreach ((string Namespace, string Name, string Value) a in Attributes)
            {
                if (a.Namespace.Length > 0 && a.Namespace != XmpNamespaces.Rdf && a.Namespace != XmpNamespaces.Xml)
                {
                    return true;
                }
            }

            return false;
        }

        public void AddAttribute(string ns, string name, string value) => (_attributes ??= []).Add((ns, name, value));

        public void AddChild(Node child) => (_children ??= []).Add(child);

        public void AppendText(string text)
        {
            if (_more is not null)
            {
                _more.Append(text);
            }
            else if (_text is null)
            {
                _text = text;
            }
            else
            {
                _more = new System.Text.StringBuilder(_text).Append(text);
            }
        }
    }
}
