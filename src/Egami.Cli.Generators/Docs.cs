using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

using Microsoft.CodeAnalysis;

namespace Egami.Cli.Generators;

/// <summary>The XML documentation of the referenced Egami assemblies, as plain one-line text for help output.</summary>
internal sealed class Docs
{
    private readonly Dictionary<string, XElement> _members = new(StringComparer.Ordinal);

    public static Docs Read(ImmutableArray<string> files)
    {
        Docs docs = new();
        foreach (string file in files)
        {
            if (file.Length == 0)
            {
                continue;
            }

            try
            {
                XDocument document = XDocument.Parse(file);
                foreach (XElement member in document.Descendants("member"))
                {
                    if (member.Attribute("name")?.Value is string name)
                    {
                        docs._members[name] = member;
                    }
                }
            }
            catch (XmlException)
            {
                // Not a documentation file; help text is left empty.
            }
        }

        return docs;
    }

    /// <summary>The first sentence of a symbol's summary.</summary>
    public string Summary(ISymbol symbol)
    {
        string text = FirstSentence(Text(Member(symbol)?.Element("summary")));
        if (symbol is IPropertySymbol)
        {
            // "Gets or sets the quality" reads as "The quality" in a settings list.
            foreach (string lead in new[] { "Gets or sets ", "Gets " })
            {
                if (text.StartsWith(lead, StringComparison.Ordinal) && text.Length > lead.Length)
                {
                    text = char.ToUpperInvariant(text[lead.Length]) + text.Substring(lead.Length + 1);
                    break;
                }
            }
        }

        return text;
    }

    /// <summary>The text of a method's parameter.</summary>
    public string Param(IMethodSymbol method, string name)
    {
        foreach (XElement param in Member(method)?.Elements("param") ?? [])
        {
            if (param.Attribute("name")?.Value == name)
            {
                return FirstSentence(Text(param));
            }
        }

        return string.Empty;
    }

    private XElement? Member(ISymbol symbol)
    {
        string? id = symbol.GetDocumentationCommentId();
        return id is not null && _members.TryGetValue(id, out XElement? member) ? member : null;
    }

    private static string Text(XElement? element)
    {
        if (element is null)
        {
            return string.Empty;
        }

        StringBuilder text = new();
        Append(element, text);
        return Regex.Replace(text.ToString(), @"\s+", " ").Trim();
    }

    private static void Append(XElement element, StringBuilder text)
    {
        foreach (XNode node in element.Nodes())
        {
            switch (node)
            {
                case XText t:
                    text.Append(t.Value);
                    break;
                case XElement { Name.LocalName: "see" or "seealso" } see:
                    string? reference = see.Attribute("cref")?.Value ?? see.Attribute("langword")?.Value ?? see.Attribute("href")?.Value;
                    if (see.Value.Length > 0)
                    {
                        text.Append(see.Value);
                    }
                    else if (reference is not null)
                    {
                        text.Append(ShortName(reference));
                    }

                    break;
                case XElement { Name.LocalName: "paramref" or "typeparamref" } param:
                    text.Append(param.Attribute("name")?.Value);
                    break;
                case XElement inner:
                    Append(inner, text);
                    break;
            }
        }
    }

    private static string ShortName(string reference)
    {
        int colon = reference.IndexOf(':');
        string name = colon >= 0 ? reference.Substring(colon + 1) : reference;
        int paren = name.IndexOf('(');
        if (paren >= 0)
        {
            name = name.Substring(0, paren);
        }

        int dot = name.LastIndexOf('.');
        return dot >= 0 ? name.Substring(dot + 1) : name;
    }

    private static string FirstSentence(string text)
    {
        for (int i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] == '.' && text[i + 1] == ' ' && (i < 2 || text.Substring(i - 2, 2) is not ("e." or "g.")))
            {
                return text.Substring(0, i + 1);
            }
        }

        return text;
    }
}
