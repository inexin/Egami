namespace Egami.Operations;

/// <summary>
/// How a source color is combined with the destination under it: the twelve Porter–Duff operators, Plus and Modulate, and the
/// separable and non-separable blend modes of the W3C Compositing and Blending specification, plus Add and Subtract. Blend modes
/// (<see cref="Multiply"/> to <see cref="Luminosity"/>) blend the colors where both are present, then composite source-over.
/// The values are fixed: they never change between releases.
/// </summary>
/// <remarks>
/// Every mode works on premultiplied values; with <c>s</c> the source and <c>d</c> the destination, each with alpha
/// <c>αs</c> and <c>αd</c>. The source's alpha is multiplied by the opacity first, so opacity 0 leaves the destination as it
/// was for every mode except <see cref="Clear"/>, <see cref="Source"/>, <see cref="DestinationAtop"/>, <see cref="Modulate"/>
/// and the In and Out modes, whose results depend on where the source is.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Blending.cs" region="blend-mode"/>
/// </example>
public enum BlendMode
{
    /// <summary>The source covers the destination: <c>s + d(1 − αs)</c>. The default, and what every 1.x DrawImage does.</summary>
    SourceOver = 0,

    /// <summary>Both are removed: the result is transparent.</summary>
    Clear = 1,

    /// <summary>The source replaces the destination: <c>s</c>.</summary>
    Source = 2,

    /// <summary>The destination is kept: <c>d</c>. Drawing with it changes nothing.</summary>
    Destination = 3,

    /// <summary>The destination covers the source: <c>s(1 − αd) + d</c>, drawing behind what is there.</summary>
    DestinationOver = 4,

    /// <summary>The source where the destination is: <c>s·αd</c>.</summary>
    SourceIn = 5,

    /// <summary>The destination where the source is: <c>d·αs</c>. Use it to cut an image to a shape.</summary>
    DestinationIn = 6,

    /// <summary>The source where the destination is not: <c>s(1 − αd)</c>.</summary>
    SourceOut = 7,

    /// <summary>The destination where the source is not: <c>d(1 − αs)</c>. Use it to punch a shape out of an image.</summary>
    DestinationOut = 8,

    /// <summary>The source on top of the destination, only where the destination is: <c>s·αd + d(1 − αs)</c>.</summary>
    SourceAtop = 9,

    /// <summary>The destination on top of the source, only where the source is: <c>s(1 − αd) + d·αs</c>.</summary>
    DestinationAtop = 10,

    /// <summary>Each where the other is not: <c>s(1 − αd) + d(1 − αs)</c>.</summary>
    Xor = 11,

    /// <summary>The sum, clamped to 1, of color and of alpha: <c>min(1, s + d)</c> (the W3C "lighter").</summary>
    Plus = 12,

    /// <summary>The product of color and of alpha: <c>s·d</c>.</summary>
    Modulate = 13,

    /// <summary>Multiplies the colors, which always darkens: <c>B = Cs·Cd</c>. White changes nothing; black gives black.</summary>
    Multiply = 14,

    /// <summary>Multiplies the inverted colors, which always lightens: <c>B = Cs + Cd − Cs·Cd</c>. Black changes nothing.</summary>
    Screen = 15,

    /// <summary>Multiplies or screens depending on the destination, raising contrast: <see cref="HardLight"/> with the layers swapped.</summary>
    Overlay = 16,

    /// <summary>The darker of the two colors, per channel.</summary>
    Darken = 17,

    /// <summary>The lighter of the two colors, per channel.</summary>
    Lighten = 18,

    /// <summary>Brightens the destination to reflect the source: <c>B = min(1, Cd / (1 − Cs))</c>.</summary>
    ColorDodge = 19,

    /// <summary>Darkens the destination to reflect the source: <c>B = 1 − min(1, (1 − Cd) / Cs)</c>.</summary>
    ColorBurn = 20,

    /// <summary>Multiplies where the source is dark and screens where it is light, as a harsh spotlight would.</summary>
    HardLight = 21,

    /// <summary>Darkens or lightens depending on the source, as a diffused spotlight would (the W3C formula).</summary>
    SoftLight = 22,

    /// <summary>The absolute difference of the colors: <c>B = |Cd − Cs|</c>.</summary>
    Difference = 23,

    /// <summary>Like <see cref="Difference"/> with lower contrast: <c>B = Cs + Cd − 2·Cs·Cd</c>.</summary>
    Exclusion = 24,

    /// <summary>The sum of the colors, clamped: <c>B = min(1, Cs + Cd)</c> (Photoshop's Linear Dodge).</summary>
    Add = 25,

    /// <summary>The source taken from the destination, clamped: <c>B = max(0, Cd − Cs)</c>.</summary>
    Subtract = 26,

    /// <summary>The source's hue with the destination's saturation and luminosity.</summary>
    Hue = 27,

    /// <summary>The source's saturation with the destination's hue and luminosity.</summary>
    Saturation = 28,

    /// <summary>The source's hue and saturation with the destination's luminosity: tints a gray image.</summary>
    Color = 29,

    /// <summary>The source's luminosity with the destination's hue and saturation.</summary>
    Luminosity = 30,
}
