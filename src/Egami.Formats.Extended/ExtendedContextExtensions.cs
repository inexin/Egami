using Egami.Formats.Extended.Dds;
using Egami.Formats.Extended.Dpx;
using Egami.Formats.Extended.Farbfeld;
using Egami.Formats.Extended.JpegLs;
using Egami.Formats.Extended.Ktx;
using Egami.Formats.Extended.Pcx;
using Egami.Formats.Extended.Raw;
using Egami.Formats.Extended.Sgi;
using Egami.Formats.Extended.SunRaster;
using Egami.Formats.Extended.Wbmp;
using Egami.Formats.Extended.Xbm;
using Egami.Formats.Extended.Xpm;

namespace Egami.Formats.Extended;

/// <summary>
/// Registers Egami.Formats.Extended's formats with an <see cref="EgamiContext"/>. Nothing registers them on their own: an app
/// calls <see cref="WithExtendedFormats"/> for every stable format, or one method per format.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/FormatsExtended.cs" region="register"/>
/// </example>
public static class ExtendedContextExtensions
{
    /// <summary>
    /// Returns a copy of <paramref name="context"/> with every stable format of this package registered: PCX, SGI, Sun raster,
    /// XBM, XPM, WBMP, Farbfeld, DDS, KTX, KTX 2, JPEG-LS, camera RAW, DPX and Cineon. Formats in preview are never included; each has its own method. Call it once, for example
    /// <c>EgamiContext.SetDefault(new EgamiContext().WithExtendedFormats())</c> at startup.
    /// </summary>
    /// <param name="context">The context to add the formats to.</param>
    /// <returns>The new context, its own formats first, so this package's are detected after them.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/FormatsExtended.cs" region="register"/>
    /// </example>
    public static EgamiContext WithExtendedFormats(this EgamiContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.WithPcx().WithSgi().WithSunRaster().WithXbm().WithXpm().WithWbmp().WithFarbfeld().WithDds().WithKtx().WithKtx2().WithJpegLs().WithRaw().WithDpx().WithCineon();
    }

    /// <summary>Returns a copy of <paramref name="context"/> that reads and writes PCX (<see cref="PcxFormat"/>).</summary>
    /// <param name="context">The context to add the format to.</param>
    /// <returns>The new context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static EgamiContext WithPcx(this EgamiContext context) => Add(context, PcxFormat.Info);

    /// <summary>Returns a copy of <paramref name="context"/> that reads and writes SGI (<see cref="SgiFormat"/>).</summary>
    /// <param name="context">The context to add the format to.</param>
    /// <returns>The new context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static EgamiContext WithSgi(this EgamiContext context) => Add(context, SgiFormat.Info);

    /// <summary>Returns a copy of <paramref name="context"/> that reads and writes Sun raster (<see cref="SunRasterFormat"/>).</summary>
    /// <param name="context">The context to add the format to.</param>
    /// <returns>The new context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static EgamiContext WithSunRaster(this EgamiContext context) => Add(context, SunRasterFormat.Info);

    /// <summary>Returns a copy of <paramref name="context"/> that reads and writes XBM (<see cref="XbmFormat"/>).</summary>
    /// <param name="context">The context to add the format to.</param>
    /// <returns>The new context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static EgamiContext WithXbm(this EgamiContext context) => Add(context, XbmFormat.Info);

    /// <summary>Returns a copy of <paramref name="context"/> that reads and writes XPM (<see cref="XpmFormat"/>).</summary>
    /// <param name="context">The context to add the format to.</param>
    /// <returns>The new context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static EgamiContext WithXpm(this EgamiContext context) => Add(context, XpmFormat.Info);

    /// <summary>Returns a copy of <paramref name="context"/> that reads and writes WBMP (<see cref="WbmpFormat"/>).</summary>
    /// <param name="context">The context to add the format to.</param>
    /// <returns>The new context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static EgamiContext WithWbmp(this EgamiContext context) => Add(context, WbmpFormat.Info);

    /// <summary>Returns a copy of <paramref name="context"/> that reads and writes Farbfeld (<see cref="FarbfeldFormat"/>).</summary>
    /// <param name="context">The context to add the format to.</param>
    /// <returns>The new context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static EgamiContext WithFarbfeld(this EgamiContext context) => Add(context, FarbfeldFormat.Info);

    /// <summary>Returns a copy of <paramref name="context"/> that reads DDS textures (<see cref="DdsFormat"/>).</summary>
    /// <param name="context">The context to add the format to.</param>
    /// <returns>The new context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static EgamiContext WithDds(this EgamiContext context) => Add(context, DdsFormat.Info);

    /// <summary>Returns a copy of <paramref name="context"/> that reads KTX 1.1 textures (<see cref="KtxFormat"/>).</summary>
    /// <param name="context">The context to add the format to.</param>
    /// <returns>The new context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static EgamiContext WithKtx(this EgamiContext context) => Add(context, KtxFormat.Info);

    /// <summary>Returns a copy of <paramref name="context"/> that reads KTX 2.0 textures (<see cref="Ktx2Format"/>).</summary>
    /// <param name="context">The context to add the format to.</param>
    /// <returns>The new context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static EgamiContext WithKtx2(this EgamiContext context) => Add(context, Ktx2Format.Info);

    /// <summary>Returns a copy of <paramref name="context"/> that reads and writes DPX (<see cref="DpxFormat"/>).</summary>
    /// <param name="context">The context to add the format to.</param>
    /// <returns>The new context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static EgamiContext WithDpx(this EgamiContext context) => Add(context, DpxFormat.Info);

    /// <summary>Returns a copy of <paramref name="context"/> that reads and writes Cineon (<see cref="CineonFormat"/>).</summary>
    /// <param name="context">The context to add the format to.</param>
    /// <returns>The new context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static EgamiContext WithCineon(this EgamiContext context) => Add(context, CineonFormat.Info);

    /// <summary>
    /// Returns a copy of <paramref name="context"/> that reads and writes JPEG-LS (<see cref="JpegLsFormat"/>). JPEG-LS is
    /// detected ahead of the other formats, since a JPEG-LS file starts as a JPEG file does; without it, a .jls file fails to
    /// load as JPEG.
    /// </summary>
    /// <param name="context">The context to add the format to.</param>
    /// <returns>The new context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/FormatsExtended.cs" region="jpegls"/>
    /// </example>
    public static EgamiContext WithJpegLs(this EgamiContext context) => First(context, JpegLsFormat.Info);

    /// <summary>
    /// Returns a copy of <paramref name="context"/> that reads camera RAW files (<see cref="RawFormat"/>). RAW is detected ahead
    /// of every other format, since a DNG file is also a TIFF file; without it, a DNG opens as TIFF and only its preview pages load.
    /// </summary>
    /// <param name="context">The context to add the format to.</param>
    /// <returns>The new context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/FormatsExtended.cs" region="raw-load"/>
    /// </example>
    public static EgamiContext WithRaw(this EgamiContext context)
    {
        return First(context, RawFormat.Info);
    }

    /// <summary>Returns <paramref name="context"/> with <paramref name="format"/> first, so it is detected before the others.</summary>
    private static EgamiContext First(EgamiContext context, FormatInfo format)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context with { Formats = [format, .. context.Formats.Where(f => !string.Equals(f.Name, format.Name, StringComparison.OrdinalIgnoreCase))] };
    }

    private static EgamiContext Add(EgamiContext context, FormatInfo format)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.WithFormat(format);
    }
}
