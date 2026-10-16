using System.Numerics;

using Egami.Drawing;
using Egami.Metafiles.Reading;

namespace Egami.Metafiles;

/// <summary>
/// A Windows metafile, WMF, EMF or EMF+ (and their gzip forms WMZ and EMZ), read and checked; its records stay internal (design
/// question 3). The size facts come from the header: an EMF's frame, a placeable WMF's box and units per inch, or a plain
/// WMF's window extent at 96 DPI. A metafile is immutable and safe to read from many threads.
/// </summary>
public sealed class Metafile
{
    private Metafile(MetafileContent content, IReadOnlyList<EgamiDiagnostic> warnings, EgamiContext context)
    {
        Content = content;
        Warnings = warnings;
        Context = context;
        (Frame, Dpi) = Measure(content);
    }

    /// <summary>Gets what the file holds.</summary>
    public MetafileKind Kind => Content.Kind;

    /// <summary>Gets the picture's size in points (1/72 inch), the size Word and PowerPoint give it.</summary>
    public Vector2 Size => new(Frame.Width, Frame.Height);

    /// <summary>
    /// Gets the picture's frame in points: an EMF's frame rectangle (its origin may be away from zero), a placeable WMF's box, or
    /// a plain WMF's window extent from zero.
    /// </summary>
    public RectF Frame { get; }

    /// <summary>
    /// Gets the resolution of the units the picture was recorded in: an EMF's reference device in pixels per inch, a placeable
    /// WMF's units per inch, or 96 for a plain WMF.
    /// </summary>
    public Vector2 Dpi { get; }

    /// <summary>Gets an EMF's description (the application, then the picture's name, on separate lines), or <see langword="null"/>.</summary>
    public string? Description => Content.Description;

    /// <summary>Gets what the load skipped or assumed, such as a record that runs past the end of the file.</summary>
    public IReadOnlyList<EgamiDiagnostic> Warnings { get; }

    /// <summary>Gets the container read from the file.</summary>
    internal MetafileContent Content { get; }

    /// <summary>Gets the context the file was loaded with.</summary>
    internal EgamiContext Context { get; }

    /// <summary>Reads a .wmf, .emf, .wmz or .emz file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">How to read it, or <see langword="null"/> for the defaults.</param>
    /// <returns>The metafile.</returns>
    /// <exception cref="CorruptDataException">The file is no WMF or EMF file, or its header is broken.</exception>
    /// <exception cref="LimitExceededException">The file, or an EMZ or WMZ file once inflated, is larger than the context's <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    public static Metafile Load(string path, MetafileLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream stream = File.OpenRead(path);
        return Load(stream, options);
    }

    /// <summary>Reads a WMF, EMF, WMZ or EMZ stream from its current position to its end.</summary>
    /// <param name="stream">The stream; it is read to its end and left open.</param>
    /// <param name="options">How to read it, or <see langword="null"/> for the defaults.</param>
    /// <returns>The metafile.</returns>
    /// <exception cref="CorruptDataException">The data is no WMF or EMF file, or its header is broken.</exception>
    /// <exception cref="LimitExceededException">The data, or an EMZ or WMZ stream once inflated, is larger than the context's <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    public static Metafile Load(Stream stream, MetafileLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= MetafileLoadOptions.Default;
        EgamiContext context = options.EffectiveContext;
        byte[] data = MetafileReader.ReadAll(stream, context.Limits);
        MetafileReport report = new();
        MetafileContent content = MetafileReader.Read(data, report);
        if (content.Kind == MetafileKind.Wmf && content.PlaceableBox is null)
        {
            report.Warn(
                "Metafile.NoPlaceableHeader",
                content.WindowExtent is null
                    ? "The WMF has no placeable header and sets no window extent; it is sized 96 by 96 units at 96 DPI."
                    : "The WMF has no placeable header; it is sized by its window extent at 96 DPI.");
        }

        report.Deliver(context);
        return new Metafile(content, report.Warnings, context);
    }

    /// <summary>The frame in points and the DPI of the recorded units.</summary>
    private static (RectF Frame, Vector2 Dpi) Measure(MetafileContent c)
    {
        if (c.Kind != MetafileKind.Wmf)
        {
            const float PointsPerHundredthMm = 72f / 2540f;
            (int left, int top, int right, int bottom) = c.Frame;
            Vector2 dpi = c.DeviceMillimeters.Width > 0 && c.DeviceMillimeters.Height > 0
                ? new Vector2(c.DevicePixels.Width * 25.4f / c.DeviceMillimeters.Width, c.DevicePixels.Height * 25.4f / c.DeviceMillimeters.Height)
                : new Vector2(96);
            return (new RectF(left * PointsPerHundredthMm, top * PointsPerHundredthMm, (right - left) * PointsPerHundredthMm, (bottom - top) * PointsPerHundredthMm), dpi);
        }

        if (c.PlaceableBox is (short l, short t, short r, short b))
        {
            float scale = 72f / c.UnitsPerInch;
            return (new RectF(l * scale, t * scale, (r - l) * scale, (b - t) * scale), new Vector2(c.UnitsPerInch));
        }

        (short x, short y) = c.WindowExtent ?? (96, 96);
        return (new RectF(0, 0, Math.Abs((int)x) * 0.75f, Math.Abs((int)y) * 0.75f), new Vector2(96));
    }
}
