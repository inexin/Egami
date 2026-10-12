namespace Egami.Fonts;

/// <summary>A position on one axis of a variable font, in the axis's own units, such as weight 650.</summary>
/// <param name="Axis">The axis tag, such as <c>wght</c>.</param>
/// <param name="Value">The value in the axis's user units (wght 1 to 1000, wdth in percent, slnt in degrees, opsz in points).</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="variable"/>
/// </example>
public readonly record struct FontVariation(FontTag Axis, float Value)
{
    /// <summary>Initializes a position from the axis tag's text, such as <c>new FontVariation("wght", 650)</c>.</summary>
    /// <param name="axis">One to four characters.</param>
    /// <param name="value">The value in the axis's user units.</param>
    public FontVariation(string axis, float value)
        : this(new FontTag(axis), value)
    {
    }

    /// <summary>Gets the tag of the registered weight axis, <c>wght</c> (1 to 1000).</summary>
    public static FontTag WeightAxis { get; } = new("wght");

    /// <summary>Gets the tag of the registered width axis, <c>wdth</c> (percent of normal).</summary>
    public static FontTag WidthAxis { get; } = new("wdth");

    /// <summary>Gets the tag of the registered slant axis, <c>slnt</c> (degrees counter-clockwise, negative for text leaning right).</summary>
    public static FontTag SlantAxis { get; } = new("slnt");

    /// <summary>Gets the tag of the registered italic axis, <c>ital</c> (0 upright, 1 italic).</summary>
    public static FontTag ItalicAxis { get; } = new("ital");

    /// <summary>Gets the tag of the registered optical size axis, <c>opsz</c> (the text size in points the design is for).</summary>
    public static FontTag OpticalSizeAxis { get; } = new("opsz");

    /// <summary>Returns a weight position.</summary>
    /// <param name="value">The weight, 1 to 1000; 400 is normal and 700 bold.</param>
    /// <returns>The position.</returns>
    public static FontVariation Weight(float value) => new(WeightAxis, value);

    /// <summary>Returns a width position.</summary>
    /// <param name="percent">The width in percent of normal: 100 normal, 75 condensed, 125 expanded.</param>
    /// <returns>The position.</returns>
    public static FontVariation Width(float percent) => new(WidthAxis, percent);

    /// <summary>Returns a slant position.</summary>
    /// <param name="degrees">The slant in degrees counter-clockwise from upright; negative values lean right.</param>
    /// <returns>The position.</returns>
    public static FontVariation Slant(float degrees) => new(SlantAxis, degrees);

    /// <summary>Returns an italic position.</summary>
    /// <param name="value">0 for upright, 1 for italic.</param>
    /// <returns>The position.</returns>
    public static FontVariation Italic(float value) => new(ItalicAxis, value);

    /// <summary>Returns an optical size position.</summary>
    /// <param name="points">The text size the design is for.</param>
    /// <returns>The position.</returns>
    public static FontVariation OpticalSize(float points) => new(OpticalSizeAxis, points);
}

/// <summary>One axis of a variable font, from its fvar table.</summary>
/// <param name="Tag">The axis tag, such as <c>wght</c>.</param>
/// <param name="Name">The axis's English name from the name table, such as "Weight", or the tag when the font names none.</param>
/// <param name="Min">The smallest value.</param>
/// <param name="Default">The default value, where the font's own outlines are.</param>
/// <param name="Max">The largest value.</param>
/// <param name="IsHidden">Whether the font asks applications not to show the axis (fvar's HIDDEN_AXIS flag).</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="variable"/>
/// </example>
public sealed record class VariationAxis(FontTag Tag, string Name, float Min, float Default, float Max, bool IsHidden);

/// <summary>A named instance of a variable font, such as "Bold Condensed", from its fvar table.</summary>
/// <param name="Name">The instance's English subfamily name, such as "Bold".</param>
/// <param name="PostScriptName">The instance's PostScript name, or <see langword="null"/> when the font gives none.</param>
/// <param name="Coordinates">The position on every axis, in fvar order.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="variable"/>
/// </example>
public sealed record class NamedInstance(string Name, string? PostScriptName, IReadOnlyList<FontVariation> Coordinates);

/// <summary>Whether a <see cref="Font"/> sets a variable font's optical size axis from its size.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="variable"/>
/// </example>
public enum OpticalSizing
{
    /// <summary>The opsz axis follows the font size in canvas units, clamped to the axis, unless the typeface sets opsz itself.</summary>
    Auto,

    /// <summary>The typeface's opsz position is used as it is.</summary>
    None,
}
