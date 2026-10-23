using System.Text;

namespace Egami.Video.Subtitles;

/// <summary>A node of WebVTT cue text as the W3C cue text parsing rules build it: an element, a text run or a timestamp.</summary>
internal sealed class VttNode
{
    public VttNode(string kind, VttNode? parent)
    {
        Kind = kind;
        Parent = parent;
    }

    /// <summary>Gets "c", "i", "b", "u", "ruby", "rt", "v", "lang", "#text", "#timestamp" or "#root".</summary>
    public string Kind { get; }

    public VttNode? Parent { get; }

    public List<VttNode> Children { get; } = [];

    public List<string> Classes { get; } = [];

    public string? Annotation { get; set; }

    public string? Text { get; set; }

    public MediaTime Time { get; set; }
}

/// <summary>
/// WebVTT cue text: the W3C tokenizer and tree builder (<c>&lt;c.class&gt;</c>, <c>&lt;i&gt;</c>, <c>&lt;b&gt;</c>,
/// <c>&lt;u&gt;</c>, <c>&lt;ruby&gt;</c>, <c>&lt;rt&gt;</c>, <c>&lt;v name&gt;</c>, <c>&lt;lang tag&gt;</c>, timestamps and
/// character references), flattening the tree into spans, and writing spans back.
/// </summary>
internal static class WebVttCueText
{
    private enum TokenKind
    {
        Text,
        StartTag,
        EndTag,
        Timestamp,
    }

    public static VttNode ParseTree(string text)
    {
        VttNode root = new("#root", null);
        VttNode current = root;
        int at = 0;
        while (at < text.Length)
        {
            (TokenKind kind, string value, List<string> classes, string annotation) = NextToken(text, ref at);
            switch (kind)
            {
                case TokenKind.Text:
                    current.Children.Add(new VttNode("#text", current) { Text = value });
                    break;
                case TokenKind.StartTag:
                    if (value is "c" or "i" or "b" or "u" or "ruby" or "v" or "lang" || (value == "rt" && current.Kind == "ruby"))
                    {
                        VttNode element = new(value, current);
                        element.Classes.AddRange(classes.Where(c => c.Length > 0));
                        if (value is "v" or "lang")
                        {
                            element.Annotation = annotation;
                        }

                        current.Children.Add(element);
                        current = element;
                    }

                    break;
                case TokenKind.EndTag:
                    if (value == current.Kind && current.Parent is not null)
                    {
                        current = current.Parent;
                    }
                    else if (value == "ruby" && current.Kind == "rt" && current.Parent?.Parent is { } above)
                    {
                        current = above;
                    }

                    break;
                case TokenKind.Timestamp:
                    if (WebVttFormat.TryTimestamp(value, out MediaTime time))
                    {
                        current.Children.Add(new VttNode("#timestamp", current) { Time = time });
                    }

                    break;
            }
        }

        return root;
    }

    private static (TokenKind Kind, string Value, List<string> Classes, string Annotation) NextToken(string text, ref int at)
    {
        StringBuilder result = new();
        StringBuilder buffer = new();
        List<string> classes = [];
        int state = 0; // 0 data, 1 tag, 2 start tag, 3 class, 4 annotation, 5 end tag, 6 timestamp
        while (true)
        {
            char c = at < text.Length ? text[at] : '\0';
            bool eof = at >= text.Length;
            switch (state)
            {
                case 0:
                    if (eof)
                    {
                        return (TokenKind.Text, result.ToString(), classes, "");
                    }

                    if (c == '&')
                    {
                        at = HtmlEntities.Consume(text, at, result);
                        continue;
                    }

                    if (c == '<')
                    {
                        if (result.Length > 0)
                        {
                            return (TokenKind.Text, result.ToString(), classes, "");
                        }

                        state = 1;
                        break;
                    }

                    result.Append(c);
                    break;
                case 1:
                    if (eof)
                    {
                        return (TokenKind.StartTag, "", classes, "");
                    }

                    if (c is '\t' or '\n' or '\f' or ' ')
                    {
                        state = 4;
                    }
                    else if (c == '.')
                    {
                        state = 3;
                    }
                    else if (c == '/')
                    {
                        state = 5;
                    }
                    else if (char.IsAsciiDigit(c))
                    {
                        buffer.Append(c);
                        state = 6;
                    }
                    else if (c == '>')
                    {
                        at++;
                        return (TokenKind.StartTag, "", classes, "");
                    }
                    else
                    {
                        result.Append(c);
                        state = 2;
                    }

                    break;
                case 2:
                    if (eof)
                    {
                        return (TokenKind.StartTag, result.ToString(), classes, "");
                    }

                    if (c is '\t' or '\f' or ' ')
                    {
                        state = 4;
                    }
                    else if (c == '\n')
                    {
                        buffer.Append(c);
                        state = 4;
                    }
                    else if (c == '.')
                    {
                        state = 3;
                    }
                    else if (c == '>')
                    {
                        at++;
                        return (TokenKind.StartTag, result.ToString(), classes, "");
                    }
                    else
                    {
                        result.Append(c);
                    }

                    break;
                case 3:
                    if (eof)
                    {
                        classes.Add(buffer.ToString());
                        return (TokenKind.StartTag, result.ToString(), classes, "");
                    }

                    if (c is '\t' or '\f' or ' ' or '\n')
                    {
                        classes.Add(buffer.ToString());
                        buffer.Clear();
                        if (c == '\n')
                        {
                            buffer.Append(c);
                        }

                        state = 4;
                    }
                    else if (c == '.')
                    {
                        classes.Add(buffer.ToString());
                        buffer.Clear();
                    }
                    else if (c == '>')
                    {
                        at++;
                        classes.Add(buffer.ToString());
                        return (TokenKind.StartTag, result.ToString(), classes, "");
                    }
                    else
                    {
                        buffer.Append(c);
                    }

                    break;
                case 4:
                    if (eof || c == '>')
                    {
                        if (!eof)
                        {
                            at++;
                        }

                        return (TokenKind.StartTag, result.ToString(), classes, CollapseWhitespace(buffer.ToString()));
                    }

                    if (c == '&')
                    {
                        at = HtmlEntities.Consume(text, at, buffer);
                        continue;
                    }

                    buffer.Append(c);
                    break;
                case 5:
                    if (eof || c == '>')
                    {
                        if (!eof)
                        {
                            at++;
                        }

                        return (TokenKind.EndTag, result.ToString(), classes, "");
                    }

                    result.Append(c);
                    break;
                default:
                    if (eof || c == '>')
                    {
                        if (!eof)
                        {
                            at++;
                        }

                        return (TokenKind.Timestamp, buffer.ToString(), classes, "");
                    }

                    buffer.Append(c);
                    break;
            }

            at++;
        }
    }

    private static string CollapseWhitespace(string value)
    {
        StringBuilder output = new();
        bool space = false;
        foreach (char c in value)
        {
            if (c is '\t' or '\n' or '\f' or '\r' or ' ')
            {
                space = output.Length > 0;
                continue;
            }

            if (space)
            {
                output.Append(' ');
                space = false;
            }

            output.Append(c);
        }

        return output.ToString();
    }

    /// <summary>Flattens a cue text tree into spans: elements become fields, ruby text goes on the text it annotates.</summary>
    public static List<SubtitleSpan> ToSpans(VttNode root)
    {
        List<SubtitleSpan> spans = [];
        MediaTime? pending = null;
        Walk(root, new SubtitleSpan(""), spans, ref pending);
        return spans;
    }

    private static void Walk(VttNode node, SubtitleSpan look, List<SubtitleSpan> spans, ref MediaTime? pending)
    {
        foreach (VttNode child in node.Children)
        {
            switch (child.Kind)
            {
                case "#text":
                    SubtitleWriting.Append(spans, look with { Text = child.Text!, Timestamp = pending });
                    pending = null;
                    break;
                case "#timestamp":
                    pending = child.Time;
                    break;
                case "ruby":
                    List<SubtitleSpan> inner = [];
                    MediaTime? innerPending = pending;
                    foreach (VttNode part in child.Children)
                    {
                        if (part.Kind == "rt")
                        {
                            StringBuilder rt = new();
                            Collect(part, rt);
                            if (inner.Count > 0)
                            {
                                SubtitleSpan baseSpan = inner[0] with { Text = string.Concat(inner.Select(s => s.Text)), Ruby = rt.ToString() };
                                spans.Add(baseSpan);
                                inner.Clear();
                            }

                            continue;
                        }

                        VttNode holder = new("#root", null);
                        holder.Children.Add(part);
                        Walk(holder, Apply(child, look), inner, ref innerPending);
                    }

                    foreach (SubtitleSpan rest in inner)
                    {
                        SubtitleWriting.Append(spans, rest);
                    }

                    pending = innerPending;
                    break;
                default:
                    Walk(child, Apply(child, look), spans, ref pending);
                    break;
            }
        }
    }

    private static SubtitleSpan Apply(VttNode element, SubtitleSpan look)
    {
        SubtitleSpan result = element.Kind switch
        {
            "b" => look with { Bold = true },
            "i" => look with { Italic = true },
            "u" => look with { Underline = true },
            "v" => look with { Voice = element.Annotation ?? "" },
            "lang" => look with { Language = element.Annotation ?? "" },
            _ => look,
        };

        return element.Classes.Count > 0 ? result with { Classes = [.. result.Classes, .. element.Classes] } : result;
    }

    private static void Collect(VttNode node, StringBuilder output)
    {
        foreach (VttNode child in node.Children)
        {
            if (child.Kind == "#text")
            {
                output.Append(child.Text);
            }
            else
            {
                Collect(child, output);
            }
        }
    }

    /// <summary>Writes spans as WebVTT cue text: tags nested as v, lang, c, b, i, u; text escaped; ruby and timestamps kept.</summary>
    public static string Write(IReadOnlyList<SubtitleSpan> spans)
    {
        StringBuilder output = new();
        List<string> open = [];
        foreach (SubtitleSpan span in spans)
        {
            if (span.IsDrawing)
            {
                continue;
            }

            List<string> wanted = [];
            if (span.Voice is { } voice)
            {
                wanted.Add(voice.Length > 0 ? "v " + Escape(voice.ReplaceLineEndings(" ")) : "v");
            }

            if (span.Language is { } language)
            {
                wanted.Add("lang " + Escape(language));
            }

            if (span.Classes.Count > 0)
            {
                wanted.Add("c." + string.Join('.', span.Classes.Select(c => c.Replace(' ', '_').Replace('.', '_').Replace('>', '_'))));
            }

            if (span.Bold)
            {
                wanted.Add("b");
            }

            if (span.Italic)
            {
                wanted.Add("i");
            }

            if (span.Underline)
            {
                wanted.Add("u");
            }

            int keep = 0;
            while (keep < open.Count && keep < wanted.Count && open[keep] == wanted[keep])
            {
                keep++;
            }

            for (int k = open.Count - 1; k >= keep; k--)
            {
                output.Append("</").Append(TagName(open[k])).Append('>');
            }

            open.RemoveRange(keep, open.Count - keep);
            if (span.Timestamp is { } time)
            {
                output.Append('<').Append(WebVttFormat.FormatTime(time)).Append('>');
            }

            for (int k = keep; k < wanted.Count; k++)
            {
                output.Append('<').Append(wanted[k]).Append('>');
                open.Add(wanted[k]);
            }

            string text = Escape(span.Text);
            if (span.Ruby is { } ruby)
            {
                output.Append("<ruby>").Append(text).Append("<rt>").Append(Escape(ruby)).Append("</rt></ruby>");
            }
            else
            {
                output.Append(text);
            }
        }

        for (int k = open.Count - 1; k >= 0; k--)
        {
            output.Append("</").Append(TagName(open[k])).Append('>');
        }

        return output.ToString();
    }

    private static string TagName(string open)
    {
        int end = open.AsSpan().IndexOfAny(' ', '.');
        return end < 0 ? open : open[..end];
    }

    private static string Escape(string text)
    {
        return text.AsSpan().IndexOfAny('&', '<', '>') < 0 ? text : text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
