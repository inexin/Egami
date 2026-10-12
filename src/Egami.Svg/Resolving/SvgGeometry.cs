using Egami.Svg.Reading;
using Egami.Svg.Values;

namespace Egami.Svg.Resolving;

/// <summary>
/// Reads an element's geometry properties (x, y, width, height, r, rx, ry, cx, cy, d...). SVG 2 makes them CSS properties, so a
/// style sheet or style attribute may set them; <see cref="StyleValue"/> gives that value when there is one, else the attribute
/// is read. A value that cannot be read is reported and counts as missing.
/// </summary>
internal sealed class SvgGeometry(SvgReport report)
{
    /// <summary>Gets or sets the lookup of a geometry property set by CSS for an element, or <see langword="null"/> for attributes only.</summary>
    public Func<SvgElement, string, string?>? StyleValue { get; set; }

    /// <summary>Reads a length; a missing or unreadable one is <paramref name="fallback"/>.</summary>
    public float Length(SvgElement element, string name, in SvgLengthContext context, SvgAxis axis, float fallback) =>
        Read(element, name, out SvgLength length) ? length.ToPixels(context, axis) : fallback;

    /// <summary>Reads a length that may be <c>auto</c>; missing, <c>auto</c> or unreadable is <see langword="null"/>.</summary>
    public float? Auto(SvgElement element, string name, in SvgLengthContext context, SvgAxis axis) =>
        Read(element, name, out SvgLength length) ? length.ToPixels(context, axis) : null;

    /// <summary>Reads a length without resolving it; false when missing, auto or unreadable.</summary>
    public bool Read(SvgElement element, string name, out SvgLength length)
    {
        string? text = StyleValue?.Invoke(element, name) ?? element.Attributes[name];
        if (text is null || text.AsSpan().Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            length = default;
            return false;
        }

        if (SvgLength.TryParse(text, out length))
        {
            return true;
        }

        report.Warn("Svg.BadAttribute", $"The {element.Name}'s {name} \"{Clip(text)}\" is not a length; it is ignored.", element);
        return false;
    }

    /// <summary>Gets the path data of a path element: the CSS d property's path() when set, else the d attribute.</summary>
    public string? PathData(SvgElement element)
    {
        string? style = StyleValue?.Invoke(element, "d");
        if (style is not null)
        {
            ReadOnlySpan<char> s = style.AsSpan().Trim();
            if (s.StartsWith("path(", StringComparison.OrdinalIgnoreCase) && s.EndsWith(")"))
            {
                s = s[5..^1].Trim();
                if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0])
                {
                    return s[1..^1].ToString();
                }
            }
        }

        return element.Attributes["d"];
    }

    /// <summary>Shortens a value for a message.</summary>
    public static string Clip(string text) => text.Length <= 40 ? text : string.Concat(text.AsSpan(0, 37), "...");
}
