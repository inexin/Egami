namespace Egami.SystemDrawing;

/// <summary>How a Graphics draws on a 32-bit ARGB bitmap, the format <c>new Bitmap(width, height)</c> makes.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="graphics"/>
/// </example>
public enum ArgbDrawing
{
    /// <summary>
    /// Draws on the bitmap's own pixels, blending straight alpha with Egami's rounding: many times faster, and within a level of
    /// GDI+ where colors blend. <see cref="Drawing2D.CompositingMode.SourceCopy"/> still rounds as GDI+ does.
    /// </summary>
    InPlace,

    /// <summary>Draws on a premultiplied copy of the area each call touches and writes it back, rounding exactly as GDI+ does.</summary>
    Exact,
}

/// <summary>Egami's own settings for the drop-in, beyond System.Drawing's API.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="graphics"/>
/// </example>
public static class SystemDrawingOptions
{
    private static ArgbDrawing s_argbDrawing = ArgbDrawing.InPlace;

    /// <summary>
    /// Gets or sets how Graphics objects created from now on draw on 32-bit ARGB bitmaps; <see cref="ArgbDrawing.InPlace"/> by
    /// default. Each Graphics can change its own with <see cref="Graphics.ArgbDrawing"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not an <see cref="ArgbDrawing"/>.</exception>
    public static ArgbDrawing ArgbDrawing
    {
        get => s_argbDrawing;
        set => s_argbDrawing = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }
}
