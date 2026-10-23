using Egami.Formats.Ico;
using Egami.Metadata;
using Egami.Operations;

namespace Egami.Formats.Ani;

/// <summary>
/// The Windows animated cursor format (.ani), registered in <see cref="EgamiContext.Default"/>: a RIFF file whose frames are
/// cursors or icons, with a display time and an order for each step. Load returns one animated raster with a frame per step, all
/// at one size; LoadAll returns the animation once for every size its frames hold. Save writes an animated raster with
/// <see cref="AniWriterOptions"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Ani.cs" region="ani-load"/>
/// </example>
public static class AniFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extension, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "ANI",
        MimeTypes = ["application/x-navi-animation"],
        Extensions = ["ani"],
        SignatureLength = AniFile.SignatureLength,
        Matches = AniFile.Matches,
        Reader = AniReader.Instance,
        Writer = AniWriter.Instance,
    };
}

/// <summary>
/// Options for writing ANI. Each frame of the raster is a step of the animation; a frame that repeats an earlier one (same pixels,
/// same hotspot) is stored once and shown again. Every stored frame is a cursor with the entries <see cref="IcoWriterOptions"/>
/// would write. Delays are rounded to 1/60 s, and a delay under 20 ms is written as 100 ms, as browsers play GIFs. ANI always
/// loops. EXIF, XMP and IPTC are not written; an ICC profile goes into PNG entries.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Ani.cs" region="ani-save"/>
/// </example>
public sealed class AniWriterOptions : WriterOptions
{
    private readonly IReadOnlyList<int>? _sizes;

    /// <summary>Gets the default options.</summary>
    public static new AniWriterOptions Default { get; } = new();

    /// <summary>Gets the sizes Windows draws cursors at as the display scale grows: 32, 48, 64, 96 and 128.</summary>
    public static IReadOnlyList<int> CursorSizes { get; } = [32, 48, 64, 96, 128];

    /// <summary>
    /// Gets the square sizes every stored frame holds, 1 to 256, or <see langword="null"/> (the default) to write the frames at the
    /// raster's own size, fitted down to 256 with a warning when larger. A raster that is not square is fitted inside each square
    /// and centred on transparency, as <see cref="IcoWriterOptions.Sizes"/> does.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A size is outside 1 to 256.</exception>
    /// <exception cref="ArgumentException">The list is empty or names a size twice.</exception>
    public IReadOnlyList<int>? Sizes
    {
        get => _sizes;
        init => _sizes = new IcoWriterOptions { Sizes = value }.Sizes;
    }

    /// <summary>
    /// Gets how every entry is stored, or <see langword="null"/> (the default) to choose per entry as
    /// <see cref="IcoWriterOptions.Encoding"/> does: PNG at 256 × 256, 32-bit BMP below, a frame loaded from a cursor keeping its
    /// entry's encoding at its own size.
    /// </summary>
    public IcoEntryEncoding? Encoding { get; init; }

    /// <summary>Gets the filter that resizes the frames to <see cref="Sizes"/>, as for Resize. Defaults to <see cref="ResampleFilter.Bicubic"/>.</summary>
    public ResampleFilter Filter { get; init; }

    /// <summary>
    /// Gets the hotspot of every frame at the raster's own size, scaled to each entry, or <see langword="null"/> (the default) for
    /// each frame's own <see cref="IcoDetails.Hotspot"/>, else the raster's, else the top-left pixel.
    /// </summary>
    public Point? Hotspot { get; init; }

    /// <summary>Gets the title to write, or <see langword="null"/> (the default) for <see cref="AniDetails.Title"/> of a raster loaded from ANI.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the artist to write, or <see langword="null"/> (the default) for <see cref="AniDetails.Artist"/> of a raster loaded from ANI.</summary>
    public string? Artist { get; init; }
}

/// <summary>
/// Details only ANI has, kept in <see cref="RasterMetadata.FormatDetails"/>. Each frame's own hotspot, entry and depth are in its
/// <see cref="FrameMetadata.FormatDetails"/>, an <see cref="IcoDetails"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Ani.cs" region="ani-details"/>
/// </example>
public sealed class AniDetails : FormatDetails
{
    /// <summary>Gets or sets the title from the file's <c>INFO</c> list, or <see langword="null"/>.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the artist from the file's <c>INFO</c> list, or <see langword="null"/>.</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets the display time of a step that has none of its own: the header's default rate.</summary>
    public TimeSpan DefaultDelay { get; set; }

    /// <summary>Gets or sets how many frames the file stores; steps may show a stored frame more than once.</summary>
    public int StoredFrames { get; set; }

    /// <summary>
    /// Gets or sets the stored frame each step shows, in playing order: the file's <c>seq </c> list, or 0, 1, 2 and so on without
    /// one. Steps whose frame could not be read are left out.
    /// </summary>
    public IReadOnlyList<int> Steps { get; set; } = [];

    /// <summary>Gets or sets every size the stored frames hold, largest first, as <see cref="Raster.LoadAll(Stream, EgamiContext?, ReaderOptions?)"/> returns them.</summary>
    public IReadOnlyList<Size> Sizes { get; set; } = [];

    /// <summary>Gets or sets whether the frames are raw bitmaps rather than icons or cursors, which old files sometimes are.</summary>
    public bool RawBitmaps { get; set; }

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new AniDetails
        {
            Title = Title,
            Artist = Artist,
            DefaultDelay = DefaultDelay,
            StoredFrames = StoredFrames,
            Steps = [.. Steps],
            Sizes = [.. Sizes],
            RawBitmaps = RawBitmaps,
        };
    }
}
