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
}
