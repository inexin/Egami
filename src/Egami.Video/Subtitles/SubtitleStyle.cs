using Egami.Pixels;

namespace Egami.Video.Subtitles;

/// <summary>
/// A named ASS or SSA style (a <c>Style:</c> line of <c>[V4+ Styles]</c> or <c>[V4 Styles]</c>): the font, colors, outline and
/// place cues get unless their override tags say otherwise. Colors are straight RGBA, with ASS's inverted alpha already
/// turned around (255 is opaque).
/// </summary>
/// <param name="Name">The style's name, which cues refer to in <see cref="SubtitleCue.Style"/>.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-ass"/>
/// </example>
public sealed record SubtitleStyle(string Name)
{
    /// <summary>Gets the font family. Arial by default.</summary>
    public string FontName { get; init; } = "Arial";

    /// <summary>Gets the font size in script pixels. 20 by default.</summary>
    public double FontSize { get; init; } = 20;

    /// <summary>Gets the fill color of the text.</summary>
    public Rgba8 PrimaryColor { get; init; } = new(255, 255, 255, 255);

    /// <summary>Gets the color karaoke text has before it is sung.</summary>
    public Rgba8 SecondaryColor { get; init; } = new(255, 0, 0, 255);

    /// <summary>Gets the outline color (SSA's tertiary color).</summary>
    public Rgba8 OutlineColor { get; init; } = new(0, 0, 0, 255);

    /// <summary>Gets the shadow color, or the box color when <see cref="BorderStyle"/> is 3.</summary>
    public Rgba8 BackColor { get; init; } = new(0, 0, 0, 255);

    /// <summary>Gets whether the text is bold.</summary>
    public bool Bold { get; init; }

    /// <summary>Gets whether the text is italic.</summary>
    public bool Italic { get; init; }

    /// <summary>Gets whether the text is underlined. ASS only.</summary>
    public bool Underline { get; init; }

    /// <summary>Gets whether the text is struck out. ASS only.</summary>
    public bool Strikeout { get; init; }

    /// <summary>Gets the horizontal scale in percent. 100 by default. ASS only.</summary>
    public double ScaleX { get; init; } = 100;

    /// <summary>Gets the vertical scale in percent. 100 by default. ASS only.</summary>
    public double ScaleY { get; init; } = 100;

    /// <summary>Gets the extra space between letters in script pixels. ASS only.</summary>
    public double Spacing { get; init; }

    /// <summary>Gets the rotation in degrees, counterclockwise. ASS only.</summary>
    public double Angle { get; init; }

    /// <summary>Gets the border style: 1 for an outline and shadow, 3 for an opaque box.</summary>
    public int BorderStyle { get; init; } = 1;

    /// <summary>Gets the outline width in script pixels.</summary>
    public double Outline { get; init; } = 2;

    /// <summary>Gets the shadow depth in script pixels.</summary>
    public double Shadow { get; init; } = 2;

    /// <summary>Gets where cues sit. Bottom center by default.</summary>
    public SubtitleAlignment Alignment { get; init; } = SubtitleAlignment.BottomCenter;

    /// <summary>Gets the left margin in script pixels.</summary>
    public int MarginLeft { get; init; } = 10;

    /// <summary>Gets the right margin in script pixels.</summary>
    public int MarginRight { get; init; } = 10;

    /// <summary>Gets the vertical margin in script pixels.</summary>
    public int MarginVertical { get; init; } = 10;

    /// <summary>Gets the font's Windows character set (1 is the default, 0 ANSI).</summary>
    public int Encoding { get; init; } = 1;
}
