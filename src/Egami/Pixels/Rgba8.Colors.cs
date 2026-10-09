using System.Globalization;

namespace Egami.Pixels;

/// <content>Hex strings and named colors.</content>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/GettingStarted.cs" region="pixel-types"/>
/// </example>
public partial struct Rgba8
{
    /// <summary>Gets fully transparent black.</summary>
    public static Rgba8 Transparent => new(0, 0, 0, 0);

    /// <summary>Gets black, #000000.</summary>
    public static Rgba8 Black => new(0, 0, 0, 255);

    /// <summary>Gets white, #FFFFFF.</summary>
    public static Rgba8 White => new(255, 255, 255, 255);

    /// <summary>Gets gray, #808080.</summary>
    public static Rgba8 Gray => new(128, 128, 128, 255);

    /// <summary>Gets silver, #C0C0C0.</summary>
    public static Rgba8 Silver => new(192, 192, 192, 255);

    /// <summary>Gets red, #FF0000.</summary>
    public static Rgba8 Red => new(255, 0, 0, 255);

    /// <summary>Gets maroon, #800000.</summary>
    public static Rgba8 Maroon => new(128, 0, 0, 255);

    /// <summary>Gets lime, #00FF00.</summary>
    public static Rgba8 Lime => new(0, 255, 0, 255);

    /// <summary>Gets green, #008000.</summary>
    public static Rgba8 Green => new(0, 128, 0, 255);

    /// <summary>Gets blue, #0000FF.</summary>
    public static Rgba8 Blue => new(0, 0, 255, 255);

    /// <summary>Gets navy, #000080.</summary>
    public static Rgba8 Navy => new(0, 0, 128, 255);

    /// <summary>Gets yellow, #FFFF00.</summary>
    public static Rgba8 Yellow => new(255, 255, 0, 255);

    /// <summary>Gets olive, #808000.</summary>
    public static Rgba8 Olive => new(128, 128, 0, 255);

    /// <summary>Gets cyan, #00FFFF.</summary>
    public static Rgba8 Cyan => new(0, 255, 255, 255);

    /// <summary>Gets teal, #008080.</summary>
    public static Rgba8 Teal => new(0, 128, 128, 255);

    /// <summary>Gets magenta, #FF00FF.</summary>
    public static Rgba8 Magenta => new(255, 0, 255, 255);

    /// <summary>Gets purple, #800080.</summary>
    public static Rgba8 Purple => new(128, 0, 128, 255);

    /// <summary>
    /// Parses a hex color: <c>RGB</c>, <c>RGBA</c>, <c>RRGGBB</c> or <c>RRGGBBAA</c>, with or without a leading <c>#</c>.
    /// Single digits are doubled, so <c>#F80</c> is <c>#FF8800</c>. Missing alpha means opaque.
    /// </summary>
    /// <param name="hex">The hex string.</param>
    /// <returns>The color.</returns>
    /// <exception cref="FormatException"><paramref name="hex"/> is not a valid hex color.</exception>
    public static Rgba8 FromHex(ReadOnlySpan<char> hex)
    {
        return TryFromHex(hex, out Rgba8 color)
            ? color
            : throw new FormatException("Expected a hex color in the form RGB, RGBA, RRGGBB or RRGGBBAA, optionally starting with '#'.");
    }

    /// <summary>Tries to parse a hex color in the forms that <see cref="FromHex"/> accepts.</summary>
    /// <param name="hex">The hex string.</param>
    /// <param name="color">The color, or <see cref="Transparent"/> if parsing failed.</param>
    /// <returns><see langword="true"/> if <paramref name="hex"/> was a valid hex color.</returns>
    public static bool TryFromHex(ReadOnlySpan<char> hex, out Rgba8 color)
    {
        color = default;
        if (hex.StartsWith('#'))
        {
            hex = hex[1..];
        }

        bool shortForm = hex.Length is 3 or 4;
        if (!shortForm && hex.Length is not (6 or 8))
        {
            return false;
        }

        if (!uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
        {
            return false;
        }

        if (shortForm)
        {
            if (hex.Length == 3)
            {
                value = (value << 4) | 0xF;
            }

            color = new Rgba8(
                (byte)(((value >> 12) & 0xF) * 17),
                (byte)(((value >> 8) & 0xF) * 17),
                (byte)(((value >> 4) & 0xF) * 17),
                (byte)((value & 0xF) * 17));
            return true;
        }

        if (hex.Length == 6)
        {
            value = (value << 8) | 0xFF;
        }

        color = new Rgba8((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    /// <summary>Returns the color as <c>#RRGGBBAA</c> in uppercase hex.</summary>
    /// <returns>The hex string.</returns>
    public readonly string ToHex()
    {
        return string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}{A:X2}");
    }
}
