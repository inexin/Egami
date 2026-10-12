using System.Globalization;
using System.Numerics;
using System.Text;

namespace Egami.Svg.Output;

/// <summary>Formats numbers and transforms for SVG: invariant culture, a fixed number of decimals, no trailing zeros, no "-0".</summary>
internal sealed class SvgNumbers
{
    private readonly string _format;
    private readonly string _fine;
    private readonly int _precision;
    private readonly int _finePrecision;

    public SvgNumbers(int precision)
    {
        _precision = precision;
        _finePrecision = Math.Min(9, precision + 4);
        _format = precision == 0 ? "0" : "0." + new string('#', precision);
        _fine = "0." + new string('#', _finePrecision);
    }

    /// <summary>A number at the chosen precision.</summary>
    public string F(double value) => Format(value, _precision, _format);

    /// <summary>A number with four more decimals, for a transform's scale and rotation, which multiply every coordinate.</summary>
    public string Fine(double value) => Format(value, _finePrecision, _fine);

    /// <summary>A color channel or opacity, 0 to 1, with four decimals at most.</summary>
    public static string Unit(float value) => Format(Math.Clamp(value, 0, 1), 4, "0.####");

    /// <summary>A transform attribute's value, or <see langword="null"/> for the identity.</summary>
    public string? Transform(Matrix3x2 m)
    {
        if (m.IsIdentity)
        {
            return null;
        }

        if (m.M11 == 1 && m.M12 == 0 && m.M21 == 0 && m.M22 == 1)
        {
            return $"translate({F(m.M31)} {F(m.M32)})";
        }

        return $"matrix({Fine(m.M11)} {Fine(m.M12)} {Fine(m.M21)} {Fine(m.M22)} {F(m.M31)} {F(m.M32)})";
    }

    /// <summary>A color as #rrggbb, its channels clamped.</summary>
    public static string Color(float r, float g, float b)
        => string.Create(CultureInfo.InvariantCulture, $"#{Byte(r):x2}{Byte(g):x2}{Byte(b):x2}");

    private static int Byte(float v) => (int)MathF.Round(Math.Clamp(float.IsFinite(v) ? v : 0, 0, 1) * 255);

    private static string Format(double value, int precision, string format)
    {
        if (!double.IsFinite(value))
        {
            return "0";
        }

        string s = Math.Round(value, precision, MidpointRounding.AwayFromZero).ToString(format, CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}

/// <summary>An element's attributes in the order written; null values are left out.</summary>
internal sealed class SvgAttrs : List<KeyValuePair<string, string>>
{
    public SvgAttrs Add(string name, string? value)
    {
        if (value is not null)
        {
            Add(new KeyValuePair<string, string>(name, value));
        }

        return this;
    }

    public void WriteTo(StringBuilder sb)
    {
        foreach (KeyValuePair<string, string> a in this)
        {
            sb.Append(' ').Append(a.Key).Append("=\"");
            SvgMarkup.Escape(sb, a.Value, attribute: true);
            sb.Append('"');
        }
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        WriteTo(sb);
        return sb.ToString();
    }
}

/// <summary>Writes well-formed SVG markup: elements opened are closed in order, and text and attribute values are escaped.</summary>
internal sealed class SvgMarkup
{
    private readonly StringBuilder _sb = new();
    private readonly Stack<string> _open = new();
    private readonly bool _compact;
    private int _depth;

    public SvgMarkup(bool compact, int depth)
    {
        _compact = compact;
        _depth = depth;
    }

    /// <summary>Gets the indentation depth of the next element.</summary>
    public int Depth => _depth;

    /// <summary>Gets the number of elements opened and not closed.</summary>
    public int OpenCount => _open.Count;

    /// <summary>Gets a value indicating whether nothing has been written.</summary>
    public bool IsEmpty => _sb.Length == 0;

    public void Open(string name, SvgAttrs? attrs = null)
    {
        Indent();
        _sb.Append('<').Append(name);
        attrs?.WriteTo(_sb);
        _sb.Append('>');
        _open.Push(name);
        _depth++;
    }

    public void Empty(string name, SvgAttrs? attrs = null)
    {
        Indent();
        _sb.Append('<').Append(name);
        attrs?.WriteTo(_sb);
        _sb.Append("/>");
    }

    public void Close()
    {
        string name = _open.Pop();
        _depth--;
        Indent();
        _sb.Append("</").Append(name).Append('>');
    }

    /// <summary>Writes markup made elsewhere, already well-formed, on a line of its own.</summary>
    public void Raw(string markup)
    {
        if (markup.Length == 0)
        {
            return;
        }

        Indent();
        _sb.Append(markup);
    }

    public override string ToString() => _sb.ToString();

    /// <summary>One element with its children's markup, as a string at a depth.</summary>
    public static string Element(string name, SvgAttrs attrs, string? inner, bool compact, int depth)
    {
        StringBuilder sb = new();
        sb.Append('<').Append(name);
        attrs.WriteTo(sb);
        if (string.IsNullOrEmpty(inner))
        {
            sb.Append("/>");
            return sb.ToString();
        }

        sb.Append('>').Append(inner);
        if (!compact)
        {
            sb.Append('\n').Append(' ', depth * 2);
        }

        sb.Append("</").Append(name).Append('>');
        return sb.ToString();
    }

    /// <summary>Appends text escaped for XML, leaving out characters XML 1.0 cannot hold (controls, lone surrogates).</summary>
    public static void Escape(StringBuilder sb, string text, bool attribute)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                case '"' when attribute:
                    sb.Append("&quot;");
                    break;
                case '\t' or '\n' or '\r':
                    if (attribute)
                    {
                        sb.Append("&#").Append((int)c).Append(';');
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        sb.Append(c).Append(text[++i]);
                    }
                    else if (c >= 0x20 && !char.IsSurrogate(c) && c != 0xFFFE && c != 0xFFFF)
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }
    }

    public static string Escape(string text)
    {
        StringBuilder sb = new(text.Length);
        Escape(sb, text, attribute: false);
        return sb.ToString();
    }

    private void Indent()
    {
        if (!_compact && (_sb.Length > 0 || _depth > 0))
        {
            _sb.Append('\n').Append(' ', _depth * 2);
        }
    }
}
