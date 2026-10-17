using Egami.Colors;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>Which plates a paint prints on an ink canvas, as PDF's overprint; see <see cref="Paint.Overprint"/>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="ink-paints"/>
/// </example>
public enum Overprint
{
    /// <summary>Every plate is painted: inks the colour does not name get 0, knocking out what is below.</summary>
    Off,

    /// <summary>Only the plates of the inks the colour names are painted; the rest keep what is below.</summary>
    On,

    /// <summary>As <see cref="On"/>, but a CMYK colour's zero components leave their plates alone (PDF's overprint mode 1).</summary>
    NonZero,
}

/// <summary>Ink colours on canvases that draw RGB.</summary>
internal static class Inks
{
    /// <summary>
    /// The colour an RGB canvas draws for an ink colour: its appearance in sRGB (a CMYK appearance through FOGRA39), opaque, or
    /// transparent when it has none.
    /// </summary>
    public static RgbaF32 Appearance(InkColor ink)
    {
        if (ink.Appearance is not InkAppearance appearance)
        {
            return new RgbaF32(0, 0, 0, 0);
        }

        RgbaF32 c = appearance.ToLab().ToRgbaF32();
        return new RgbaF32(Math.Clamp(c.R, 0, 1), Math.Clamp(c.G, 0, 1), Math.Clamp(c.B, 0, 1), 1);
    }
}
