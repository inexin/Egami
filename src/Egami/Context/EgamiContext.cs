using Egami.Formats;

namespace Egami;

/// <summary>
/// Every setting that is not specific to one call: where memory comes from, how many threads an operation may use, and the safety limits.
/// Contexts are immutable and safe to share. Change one with a <c>with</c> expression.
/// </summary>
/// <example>
/// <code>
/// var context = EgamiContext.Default with
/// {
///     Limits = SafetyLimits.Default with { MaxPixels = 100_000_000 },
///     MaxParallelism = 4,
/// };
/// </code>
/// </example>
public sealed record EgamiContext
{
    private readonly IPixelMemorySource _memory = PooledPixelMemorySource.Shared;
    private readonly int _maxParallelism = Environment.ProcessorCount;
    private readonly SafetyLimits _limits = SafetyLimits.Default;
    private readonly DiagnosticLevel _diagnosticLevel = DiagnosticLevel.Warning;
    private readonly IReadOnlyList<FormatInfo> _formats = [Egami.Formats.Png.PngFormat.Info, Egami.Formats.Jpeg.JpegFormat.Info, Egami.Formats.Gif.GifFormat.Info, Egami.Formats.Webp.WebpFormat.Info, Egami.Formats.Bmp.BmpFormat.Info, Egami.Formats.Qoi.QoiFormat.Info, Egami.Formats.Pnm.PnmFormat.Info, Egami.Formats.Tiff.TiffFormat.Info, Egami.Formats.Jbig2.Jbig2Format.Info, Egami.Formats.Jpeg2000.Jpeg2000Format.Info, Egami.Formats.Ico.IcoFormat.Info, Egami.Formats.Ico.CurFormat.Info, Egami.Formats.Psd.PsdFormat.Info, Egami.Formats.Radiance.RadianceFormat.Info, Egami.Formats.Exr.ExrFormat.Info, Egami.Formats.Jxl.JxlFormat.Info, Egami.Formats.Tga.TgaFormat.Info, Egami.Formats.Ccitt.CcittFormat.Info];

    private static EgamiContext? s_default;

    /// <summary>
    /// Gets the context used when a call passes none. The first read fixes it: <see cref="SetDefault"/> works only before that.
    /// </summary>
    public static EgamiContext Default => Volatile.Read(ref s_default) ?? Interlocked.CompareExchange(ref s_default, new EgamiContext(), null) ?? s_default!;

    /// <summary>
    /// Replaces <see cref="Default"/>, once, at startup, so that format plugins, memory sources and limits apply to every call that
    /// passes no context.
    /// </summary>
    /// <param name="context">The context to use as the default.</param>
    /// <exception cref="InvalidOperationException"><see cref="Default"/> was already read or set.</exception>
    public static void SetDefault(EgamiContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Interlocked.CompareExchange(ref s_default, context, null) is not null)
        {
            throw new InvalidOperationException("EgamiContext.Default was already read or set. Call SetDefault once, at startup, before any other Egami call.");
        }
    }

    /// <summary>Gets the source of all pixel memory. Defaults to <see cref="PooledPixelMemorySource.Shared"/>.</summary>
    /// <exception cref="ArgumentNullException">The value set is <see langword="null"/>.</exception>
    public IPixelMemorySource Memory { get => _memory; init => _memory = value ?? throw new ArgumentNullException(nameof(value)); }

    /// <summary>Gets the most threads one operation may use. 1 means fully sequential. Defaults to the processor count.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value set is less than 1.</exception>
    public int MaxParallelism
    {
        get => _maxParallelism;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxParallelism = value;
        }
    }

    /// <summary>Gets the safety limits applied when reading. Defaults to <see cref="SafetyLimits.Default"/>.</summary>
    /// <exception cref="ArgumentNullException">The value set is <see langword="null"/>.</exception>
    public SafetyLimits Limits { get => _limits; init => _limits = value ?? throw new ArgumentNullException(nameof(value)); }

    /// <summary>
    /// Gets the handler that receives Egami's warnings and notable decisions, or <see langword="null"/> (the default) for none, which
    /// costs nothing. It runs inline, possibly on several decode threads at once, so it must be quick and thread-safe. An exception
    /// it throws fails the call, which turns warnings into errors. Warnings a load raises arrive in
    /// <see cref="Metadata.RasterMetadata.Warnings"/> order as the call ends; Info and Debug events arrive as they happen. One id is
    /// delivered at most 10 times per call, then once more as a count of the rest.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Diagnostics.cs" region="logger"/>
    /// </example>
    public Action<EgamiDiagnostic>? Diagnostics { get; init; }

    /// <summary>
    /// Gets the lowest level <see cref="Diagnostics"/> receives. Defaults to <see cref="Egami.DiagnosticLevel.Warning"/>; events below
    /// it are never built.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value set is not a defined level.</exception>
    public DiagnosticLevel DiagnosticLevel
    {
        get => _diagnosticLevel;
        init
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Not a defined diagnostic level.");
            }

            _diagnosticLevel = value;
        }
    }

    /// <summary>
    /// Starts reporting for one call outside Egami's own loads and saves, such as a video decode or a plug-in's own entry point,
    /// with the same <see cref="DiagnosticLevel"/> filter and per-id cap. Dispose it when the call ends.
    /// </summary>
    /// <returns>A reporter for the call; a shared one that does nothing when <see cref="Diagnostics"/> is <see langword="null"/>.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Diagnostics.cs" region="plugin"/>
    /// </example>
    public DiagnosticReporter StartDiagnostics() => DiagnosticReporter.Start(this);

    /// <summary>Gets the registered formats, in detection order. Defaults to PNG, JPEG, GIF, WebP, BMP, QOI, PNM, TIFF, JBIG2, JPEG 2000 and TGA.</summary>
    public IReadOnlyList<FormatInfo> Formats
    {
        get => _formats;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _formats = [.. value];
        }
    }

    /// <summary>
    /// Returns a copy of this context with <paramref name="format"/> registered. A format with the same name is replaced in place;
    /// otherwise the new format is detected last.
    /// </summary>
    /// <param name="format">The format to add.</param>
    /// <returns>The new context.</returns>
    public EgamiContext WithFormat(FormatInfo format)
    {
        ArgumentNullException.ThrowIfNull(format);
        List<FormatInfo> formats = [.. _formats];
        int existing = formats.FindIndex(f => string.Equals(f.Name, format.Name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            formats[existing] = format;
        }
        else
        {
            formats.Add(format);
        }

        return this with { Formats = formats };
    }

    /// <summary>Gets whether events at <paramref name="level"/> reach a handler, so a site builds them only then.</summary>
    internal bool Reports(DiagnosticLevel level) => Diagnostics is not null && level >= _diagnosticLevel;

    /// <summary>Finds a registered format by name or file extension, with or without a leading dot, ignoring case.</summary>
    internal FormatInfo? FindFormat(string nameOrExtension)
    {
        string key = nameOrExtension.TrimStart('.');
        foreach (FormatInfo format in _formats)
        {
            if (string.Equals(format.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                return format;
            }
        }

        foreach (FormatInfo format in _formats)
        {
            foreach (string extension in format.Extensions)
            {
                if (string.Equals(extension.TrimStart('.'), key, StringComparison.OrdinalIgnoreCase))
                {
                    return format;
                }
            }
        }

        return null;
    }
}
