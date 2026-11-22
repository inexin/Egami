namespace Egami.Drawing.Tracing;

/// <summary>How <see cref="RasterTraceExtensions.Trace(Raster, TraceOptions?, EgamiContext?)"/> turns pixels into shapes.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tracing.cs" region="options"/>
/// </example>
public enum TraceMode
{
    /// <summary>
    /// Splits the image into ink and paper at <see cref="TraceOptions.Threshold"/> and traces the ink as black shapes, with smooth
    /// curves and sharp corners fitted to the pixel edges.
    /// </summary>
    BlackAndWhite,

    /// <summary>
    /// Reduces the image to <see cref="TraceOptions.Colors"/> gray levels of its own (or to <see cref="TraceOptions.Palette"/>)
    /// and traces each level as a layer of gray shapes.
    /// </summary>
    Grayscale,

    /// <summary>
    /// Reduces the image to <see cref="TraceOptions.Colors"/> of its own flat colors (or to <see cref="TraceOptions.Palette"/>),
    /// leaving out the blends along edges, and traces each color as a layer of shapes.
    /// </summary>
    Color,

    /// <summary>
    /// For logos, icons and other flat-color art with antialiased edges: reduces the image to its flat colors as
    /// <see cref="Color"/> does, then places each edge to a fraction of a pixel from smooth fields of how much of each pixel each
    /// color covers, for smoother curves than <see cref="Color"/> on small art. Transparency around the art is left out;
    /// transparency the art encloses is traced as white. An image that is not flat color (a photo, a gradient) is refused.
    /// </summary>
    Logo,

    /// <summary>
    /// For line art, sketches and handwriting: splits ink from paper at <see cref="TraceOptions.Threshold"/> as
    /// <see cref="BlackAndWhite"/> does, then traces the middle of each stroke as an open path, stroked with the width its ink
    /// covers (or <see cref="TraceOptions.StrokeWidth"/>), joined straight through crossings and junctions. A dot becomes a small
    /// filled disc.
    /// </summary>
    Centerline,

    /// <summary>
    /// For pixel art, sprites and icons drawn pixel by pixel, after Kopf and Lischinski's "Depixelizing Pixel Art": each pixel
    /// joins its neighbours of similar color, crossing diagonals are settled by which reads as a line, and the edges between
    /// colors become smooth curves that keep corners of two pixels or more sharp, for art that scales up without blocks. Each of
    /// the image's own colors becomes one shape; <see cref="TraceOptions.Exact"/> keeps the pixel squares instead. Pixels under
    /// half alpha are left out. An image of more than 256 colors (a photo, a gradient) is refused.
    /// </summary>
    PixelArt,
}
