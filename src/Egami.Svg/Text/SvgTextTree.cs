using System.Text;

namespace Egami.Svg.Text;

/// <summary>
/// The content of a text element as text layout reads it (SVG 1.1 section 10.15 and SVG 2 section 11.4): tspan, a and tref
/// elements as spans, textPath only as a direct child of text, everything else left out; tref replaced by the text of the
/// element it names; and white space processed across the whole element as xml:space says, the way Chromium (and resvg after
/// it) does, since browsers differ there. Translated from resvg's usvg (Apache-2.0 or MIT); see THIRD-PARTY-NOTICES.
/// </summary>
internal sealed class SvgTextTree
{
    private SvgTextTree(SvgElement? element, string kind, SvgTextTree? parent, int depth, bool preserve)
    {
        Element = element;
        Kind = kind;
        Parent = parent;
        Depth = depth;
        Preserve = preserve;
    }

    /// <summary>Gets the element, or <see langword="null"/> for a run of characters.</summary>
    public SvgElement? Element { get; }

    /// <summary>Gets <c>text</c>, <c>tspan</c> (also for a and tref), <c>textPath</c>, or <c>#text</c> for characters.</summary>
    public string Kind { get; }

    public SvgTextTree? Parent { get; }

    public int Depth { get; }

    /// <summary>Gets a value indicating whether xml:space="preserve" applies here.</summary>
    public bool Preserve { get; }

    /// <summary>Gets or sets the characters of a <c>#text</c> node, after white space processing.</summary>
    public string Text { get; set; } = "";

    public List<SvgTextTree> Children { get; } = [];

    public bool IsText => Element is null;

    /// <summary>
    /// Builds the tree of a text element, its white space already processed: as xml:space says, or for wrapped text as the
    /// white-space property says (<paramref name="breaks"/>: pre, pre-wrap and break-spaces keep every space and line break,
    /// pre-line keeps line breaks and collapses spaces).
    /// </summary>
    public static SvgTextTree Build(SvgElement text, SvgDocument document, string? breaks = null)
    {
        bool preserve = Space(text) ?? Ancestors(text).Select(Space).FirstOrDefault(s => s is not null) ?? false;
        SvgTextTree root = new(text, "text", null, 0, preserve);
        Add(root, text, document);
        if (breaks is "pre" or "pre-wrap" or "break-spaces" or "pre-line")
        {
            foreach (SvgTextTree node in root.Descendants().Where(n => n.IsText))
            {
                node.Text = Breaks(node.Source, breaks == "pre-line");
            }
        }
        else
        {
            Trim(root);
        }

        return root;
    }

    /// <summary>Gets the characters as written, before white space processing.</summary>
    private string Source { get; init; } = "";

    /// <summary>Line breaks kept as \n; with <paramref name="collapse"/> runs of spaces and tabs become one space and spaces at a break go.</summary>
    private static string Breaks(string text, bool collapse)
    {
        string s = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!collapse)
        {
            return s;
        }

        StringBuilder b = new(s.Length);
        foreach (char c0 in s)
        {
            char c = c0 == '\t' ? ' ' : c0;
            if (c == ' ' && (b.Length == 0 || b[^1] is ' ' or '\n'))
            {
                continue;
            }

            if (c == '\n' && b.Length > 0 && b[^1] == ' ')
            {
                b.Length--;
            }

            b.Append(c);
        }

        return b.ToString();
    }

    /// <summary>The number of characters (Unicode scalar values) in a node and its descendants.</summary>
    public int CharCount()
    {
        if (IsText)
        {
            return Count(Text);
        }

        int n = 0;
        foreach (SvgTextTree child in Children)
        {
            n += child.CharCount();
        }

        return n;
    }

    /// <summary>The node and its descendants, in document order.</summary>
    public IEnumerable<SvgTextTree> Descendants()
    {
        yield return this;
        foreach (SvgTextTree child in Children)
        {
            foreach (SvgTextTree d in child.Descendants())
            {
                yield return d;
            }
        }
    }

    public static int Count(string text)
    {
        int n = 0;
        foreach (Rune _ in text.EnumerateRunes())
        {
            n++;
        }

        return n;
    }

    /// <summary>The SVG 1.1 and SVG 2 element names; a tref names only one of these, as resvg reads it.</summary>
    private static readonly HashSet<string> KnownElements = new(StringComparer.Ordinal)
    {
        "a", "circle", "clipPath", "defs", "desc", "ellipse", "feBlend", "feColorMatrix", "feComponentTransfer", "feComposite",
        "feConvolveMatrix", "feDiffuseLighting", "feDisplacementMap", "feDistantLight", "feDropShadow", "feFlood", "feFuncA",
        "feFuncB", "feFuncG", "feFuncR", "feGaussianBlur", "feImage", "feMerge", "feMergeNode", "feMorphology", "feOffset",
        "fePointLight", "feSpecularLighting", "feSpotLight", "feTile", "feTurbulence", "filter", "foreignObject", "g", "image",
        "line", "linearGradient", "marker", "mask", "metadata", "path", "pattern", "polygon", "polyline", "radialGradient", "rect",
        "script", "stop", "style", "svg", "switch", "symbol", "text", "textPath", "title", "tref", "tspan", "use", "view",
    };

    private static IEnumerable<SvgElement> Ancestors(SvgElement element)
    {
        for (SvgElement? e = element.Parent as SvgElement; e is not null; e = e.Parent as SvgElement)
        {
            yield return e;
        }
    }

    private static bool? Space(SvgElement element) =>
        element.Attributes.Get("space", SvgNamespaces.Xml) is string value ? value == "preserve" : null;

    private static void Add(SvgTextTree parent, SvgElement element, SvgDocument document)
    {
        foreach (SvgContent item in element.Children)
        {
            if (item is SvgTextContent content)
            {
                parent.Children.Add(new SvgTextTree(null, "#text", parent, parent.Depth + 1, parent.Preserve) { Text = Collapse(content.Value, parent.Preserve), Source = content.Value });
                continue;
            }

            if (item is not SvgElement child || !child.IsSvgOrBare(document))
            {
                continue;
            }

            string kind = child.Name switch
            {
                "tspan" or "a" or "tref" => "tspan",
                "textPath" when parent.Kind == "text" => "textPath",
                _ => "",
            };
            if (kind.Length == 0)
            {
                continue;
            }

            bool preserve = Space(child) ?? parent.Preserve;
            SvgTextTree node = new(child, kind, parent, parent.Depth + 1, preserve);
            parent.Children.Add(node);
            if (child.Name == "tref")
            {
                // All character data inside the referenced element, markup and all.
                string? href = child.Attributes.Get("href", SvgNamespaces.XLink) ?? child.Attributes["href"];
                if (href is not null && Styling.SvgStyleValues.LocalId(href) is string id && document.FindById(id) is SvgElement target && target.IsSvgOrBare(document) && KnownElements.Contains(target.Name))
                {
                    string referenced = target.Text;
                    if (referenced.Length > 0)
                    {
                        node.Children.Add(new SvgTextTree(null, "#text", node, node.Depth + 1, preserve) { Text = Collapse(referenced, preserve), Source = referenced });
                    }
                }
            }
            else
            {
                Add(node, child, document);
            }
        }
    }

    /// <summary>Tabs and line breaks become spaces; without xml:space="preserve" runs of spaces become one.</summary>
    private static string Collapse(string text, bool preserve)
    {
        StringBuilder s = new(text.Length);
        char previous = '\0';
        foreach (char c0 in text)
        {
            char c = c0 is '\r' or '\n' or '\t' ? ' ' : c0;
            if (!preserve && c == ' ' && previous == ' ')
            {
                continue;
            }

            previous = c;
            s.Append(c);
        }

        return s.ToString();
    }

    /// <summary>Removes the spaces that only separate markup: at the start and end of the element, and doubled across nodes.</summary>
    private static void Trim(SvgTextTree root)
    {
        List<SvgTextTree> nodes = [.. root.Descendants().Where(n => n.IsText)];
        if (nodes.Count == 1)
        {
            SvgTextTree only = nodes[0];
            if (!root.Preserve)
            {
                if (only.Text == " ")
                {
                    only.Text = "";
                }
                else if (only.Text.Length >= 2)
                {
                    only.Text = only.Text.Trim(' ') == only.Text ? only.Text : TrimOne(only.Text);
                }
            }

            return;
        }

        SvgTextTree? lastNonEmpty = null;
        for (int i = 0; i + 1 < nodes.Count; i++)
        {
            SvgTextTree node1 = nodes[i];
            SvgTextTree node2 = nodes[i + 1];
            if (node1.Text.Length == 0 && lastNonEmpty is not null)
            {
                node1 = lastNonEmpty;
            }

            bool preserve1 = node1.Parent!.Preserve, preserve2 = node2.Parent!.Preserve;
            char? c1 = First(node1.Text), c2 = Last(node1.Text), c3 = First(node2.Text);
            if (node1.Depth < node2.Depth)
            {
                if (c3 == ' ' && !preserve2)
                {
                    node2.Text = node2.Text[1..];
                }
            }
            else if (c2 == ' ' && c3 == ' ')
            {
                if (!preserve1 && !preserve2)
                {
                    node1.Text = node1.Text[..^1];
                }
                else if (preserve1 && !preserve2)
                {
                    node2.Text = node2.Text[1..];
                }
            }

            char? c4 = Last(node2.Text);
            bool isFirst = i == 0, isLast = i == nodes.Count - 2;
            if (isFirst && First(node1.Text) == ' ' && !preserve1 && node1.Text.Length > 0)
            {
                node1.Text = node1.Text[1..];
            }
            else if (isLast && c4 == ' ' && node2.Text.Length > 0 && !preserve2)
            {
                node2.Text = node2.Text[..^1];
            }

            if (isLast && Last(node1.Text) == ' ' && node1.Text.Length > 0 && node2.Text.Length == 0)
            {
                node1.Text = node1.Text[..^1];
            }

            if (node1.Text.Trim().Length > 0)
            {
                lastNonEmpty = node1;
            }
        }
    }

    private static string TrimOne(string text)
    {
        if (text.StartsWith(' '))
        {
            text = text[1..];
        }

        if (text.EndsWith(' '))
        {
            text = text[..^1];
        }

        return text;
    }

    private static char? First(string text) => text.Length > 0 ? text[0] : null;

    private static char? Last(string text) => text.Length > 0 ? text[^1] : null;
}
