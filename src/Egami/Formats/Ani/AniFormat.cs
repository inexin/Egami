using Egami.Formats.Ico;
using Egami.Metadata;

namespace Egami.Formats.Ani;

/// <summary>
/// The Windows animated cursor format (.ani), registered in <see cref="EgamiContext.Default"/>: a RIFF file whose frames are
/// cursors or icons, with a display time and an order for each step. Load returns one animated raster with a frame per step, all
/// at one size; LoadAll returns the animation once for every size its frames hold.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Ani.cs" region="ani-load"/>
/// </example>
public static class AniFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extension, signature and reader.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "ANI",
        MimeTypes = ["application/x-navi-animation"],
        Extensions = ["ani"],
        SignatureLength = AniFile.SignatureLength,
        Matches = AniFile.Matches,
        Reader = AniReader.Instance,
    };
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
