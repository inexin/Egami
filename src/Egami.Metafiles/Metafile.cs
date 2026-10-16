using System.Numerics;

using Egami.Drawing;
using Egami.Metafiles.Playback;
using Egami.Metafiles.Reading;
using Egami.Pixels;

namespace Egami.Metafiles;

/// <summary>
/// A Windows metafile, WMF, EMF or EMF+ (and their gzip forms WMZ and EMZ), read and checked; its records stay internal (design
/// question 3). The size facts come from the header: an EMF's frame, a placeable WMF's box and units per inch, or a plain
/// WMF's window extent at 96 DPI. A metafile is immutable and safe to read from many threads.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Metafiles.cs" region="metafile-to-png"/>
/// </example>
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
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Metafiles.cs" region="metafile-to-png"/>
    /// </example>
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
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Metafiles.cs" region="metafile-limits"/>
    /// </example>
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

    /// <summary>
    /// Draws the picture on a canvas: in points from the canvas's origin (the frame's corner there), or with its frame stretched
    /// to <paramref name="box"/> exactly, as Word stretches a picture to its frame (design question 10). Drawing calls only the
    /// public canvas API, so a raster canvas, a PDF page, SvgCanvas or a PictureRecorder receive the same calls. It never throws
    /// on content: what it cannot draw is skipped and reported to the context's diagnostics.
    /// </summary>
    /// <param name="canvas">The canvas.</param>
    /// <param name="box">The rectangle to stretch the frame to, in the canvas's coordinates, or <see langword="null"/>.</param>
    /// <param name="options">Fonts, antialiasing and playback, or <see langword="null"/> for the defaults.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Metafiles.cs" region="metafile-draw"/>
    /// </example>
    public void Draw(Canvas canvas, RectF? box = null, MetafileRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        options ??= MetafileRenderOptions.Default;
        MetafileReport report = new();
        DrawCore(canvas, box, options, report);
        report.Deliver(options.Context ?? Context);
    }

    /// <summary>
    /// Draws the picture into a new raster: at its size at <see cref="MetafileRenderOptions.Dpi"/> (96 by default), or at a
    /// target width and/or height (the other side keeps the aspect ratio), over <see cref="MetafileRenderOptions.Background"/>.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type of the raster.</typeparam>
    /// <param name="options">The size, background, fonts and playback, or <see langword="null"/> for the defaults.</param>
    /// <returns>The raster.</returns>
    /// <exception cref="LimitExceededException">The raster would break the context's size limits.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Metafiles.cs" region="metafile-to-png"/>
    /// </example>
    public Raster<TPixel> Rasterize<TPixel>(MetafileRenderOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        options ??= MetafileRenderOptions.Default;
        MetafileReport report = new();
        Raster<TPixel> raster = RasterizeCore<TPixel>(options, report);
        report.Deliver(options.Context ?? Context);
        return raster;
    }

    /// <summary>The raster size for the options: the size at the DPI, rounded up, or the width and height asked for.</summary>
    internal (int Width, int Height) PixelSize(MetafileRenderOptions options)
    {
        double w, h;
        Vector2 size = Size;
        if (options.Width is int width && options.Height is int height)
        {
            (w, h) = (width, height);
        }
        else if (options.Width is int onlyWidth)
        {
            (w, h) = (onlyWidth, size.X > 0 ? Math.Ceiling(onlyWidth * (double)size.Y / size.X) : onlyWidth);
        }
        else if (options.Height is int onlyHeight)
        {
            (w, h) = (size.Y > 0 ? Math.Ceiling(onlyHeight * (double)size.X / size.Y) : onlyHeight, onlyHeight);
        }
        else
        {
            // A hair under a whole pixel is that pixel: the frame's 0.01 mm units rarely land exactly on one.
            (w, h) = (Math.Ceiling((size.X * (double)options.Dpi / 72) - 1e-3), Math.Ceiling((size.Y * (double)options.Dpi / 72) - 1e-3));
        }

        return ((int)Math.Clamp(w, 1, int.MaxValue), (int)Math.Clamp(h, 1, int.MaxValue));
    }

    /// <summary>Rasterizes, collecting what playback skipped into <paramref name="report"/>.</summary>
    internal Raster<TPixel> RasterizeCore<TPixel>(MetafileRenderOptions options, MetafileReport report)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        (int w, int h) = PixelSize(options);
        EgamiContext context = options.Context ?? Context;
        SafetyLimits limits = context.Limits;
        if (w > limits.MaxWidth)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxWidth), limits.MaxWidth, w);
        }

        if (h > limits.MaxHeight)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxHeight), limits.MaxHeight, h);
        }

        if ((long)w * h > limits.MaxPixels)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxPixels), limits.MaxPixels, (long)w * h);
        }

        Raster<TPixel> raster = Raster.Create<TPixel>(w, h, context);
        using RasterCanvas<TPixel> canvas = raster.CreateCanvas(options.CanvasOptions ?? CanvasOptions.Default);
        if (options.Background.A > 0)
        {
            canvas.Clear(options.Background);
        }

        DrawCore(canvas, new RectF(0, 0, w, h), options, report);
        return raster;
    }

    /// <summary>Draws, collecting what playback skipped into <paramref name="report"/>.</summary>
    internal void DrawCore(Canvas canvas, RectF? box, MetafileRenderOptions options, MetafileReport report, int depth = 0)
    {
        RectF frame = Frame;
        Matrix3x2 place;
        if (box is RectF target)
        {
            if (!(frame.Width > 0 && frame.Height > 0 && target.Width > 0 && target.Height > 0))
            {
                return;
            }

            place = Matrix3x2.CreateTranslation(-frame.X, -frame.Y)
                * Matrix3x2.CreateScale(target.Width / frame.Width, target.Height / frame.Height)
                * Matrix3x2.CreateTranslation(target.X, target.Y);
        }
        else
        {
            place = Matrix3x2.CreateTranslation(-frame.X, -frame.Y);
        }

        if (!float.IsFinite(place.M11) || !float.IsFinite(place.M22) || !float.IsFinite(place.M31) || !float.IsFinite(place.M32))
        {
            return;
        }

        int count = canvas.Save();
        try
        {
            canvas.Concat(place);
            MetafilePlayer.Play(this, canvas, options, report, depth);
        }
        finally
        {
            canvas.RestoreToCount(count);
        }
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
