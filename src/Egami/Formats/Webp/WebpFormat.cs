using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Webp;

/// <summary>The WebP format, registered in <see cref="EgamiContext.Default"/>: lossless (VP8L), lossy (VP8) with or without alpha, and animated.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Webp.cs" region="webp"/>
/// </example>
public static class WebpFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extension, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "WebP",
        MimeTypes = ["image/webp"],
        Extensions = ["webp"],
        SignatureLength = 12,
        Matches = WebpReader.Matches,
        Reader = WebpReader.Instance,
        Writer = WebpWriter.Instance,
    };
}

/// <summary>Where a kept chunk sat in a WebP file, so it can be written back in the same place.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Webp.cs" region="webp-details"/>
/// </example>
public enum WebpChunkPosition
{
    /// <summary>Before the image data (the VP8, VP8L or ALPH chunk, or the first ANMF chunk).</summary>
    BeforeImageData,

    /// <summary>After the image data.</summary>
    AfterImageData,
}

/// <summary>A chunk Egami does not read itself, kept unchanged for round trips.</summary>
/// <param name="Type">The four-character chunk type, such as "TEST".</param>
/// <param name="Data">The chunk data, without its padding byte.</param>
/// <param name="Position">Where the chunk sat.</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Webp.cs" region="webp-details"/>
/// </example>
public sealed record WebpChunk(string Type, ReadOnlyMemory<byte> Data, WebpChunkPosition Position);

/// <summary>Details only WebP has, kept in <see cref="RasterMetadata.FormatDetails"/> so a WebP can be written back as it was.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Webp.cs" region="webp-details"/>
/// </example>
public sealed class WebpDetails : FormatDetails
{
    /// <summary>
    /// Gets or sets whether the still image, or an animation's first frame, was lossless (VP8L) rather than lossy (VP8). Saving to
    /// WebP with <see cref="WebpWriterOptions.Lossless"/> left at <see langword="null"/> keeps this kind.
    /// </summary>
    public bool Lossless { get; set; }

    /// <summary>Gets or sets whether the file used the extended format (a VP8X chunk), which metadata, alpha in lossy files and animation need.</summary>
    public bool Extended { get; set; }

    /// <summary>Gets or sets whether the file said it has alpha (the VP8X flag, the VP8L alpha hint or an ALPH chunk).</summary>
    public bool HasAlpha { get; set; }

    /// <summary>
    /// Gets or sets an animation's background color from its ANIM chunk. RFC 9649 makes it a hint: Egami starts the canvas
    /// transparent, as libwebp and browsers do, unless <see cref="WebpReaderOptions.UseBackgroundColor"/> is set. Defaults to opaque
    /// white, which libwebp's animation encoder also writes.
    /// </summary>
    public Rgba8 BackgroundColor { get; set; } = new(255, 255, 255, 255);

    /// <summary>Gets the chunks Egami does not read itself, in file order, written back in the same place unless metadata is stripped.</summary>
    public IList<WebpChunk> OtherChunks { get; } = [];

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        WebpDetails copy = new() { Lossless = Lossless, Extended = Extended, HasAlpha = HasAlpha, BackgroundColor = BackgroundColor };
        foreach (WebpChunk chunk in OtherChunks)
        {
            copy.OtherChunks.Add(chunk with { Data = chunk.Data.ToArray() });
        }

        return copy;
    }
}

/// <summary>
/// Details of one frame of an animated WebP, kept in <see cref="FrameMetadata.FormatDetails"/>. Delay, blending, disposal and the
/// frame's rectangle are in <see cref="FrameMetadata"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Webp.cs" region="webp-details"/>
/// </example>
public sealed class WebpFrameDetails : FormatDetails
{
    /// <summary>Gets or sets whether the frame was stored lossless (VP8L) rather than lossy (VP8).</summary>
    public bool Lossless { get; set; }

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new WebpFrameDetails { Lossless = Lossless };
    }
}

/// <summary>Options for reading WebP, on top of the general <see cref="ReaderOptions"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Webp.cs" region="webp-read"/>
/// </example>
public sealed record WebpReaderOptions : ReaderOptions
{
    /// <summary>Gets the default options.</summary>
    public static new WebpReaderOptions Default { get; } = new();

    /// <summary>
    /// Gets whether an animation's canvas starts from, and disposed frames return to, the ANIM chunk's background color instead of
    /// transparent. Off by default, as in libwebp, Skia and browsers; RFC 9649 calls the color a hint.
    /// </summary>
    public bool UseBackgroundColor { get; init; }

    /// <summary>
    /// Gets whether lossy files repeat each chroma sample instead of interpolating between samples. Off by default, which matches
    /// libwebp's default ("fancy") upsampling; on is a little faster and gives blockier color edges, as for JPEG.
    /// </summary>
    public bool FastUpsampling { get; init; }
}

/// <summary>
/// Options for writing WebP. The defaults follow libwebp's: quality 75 and effort (libwebp's method) 4. A raster read from a WebP
/// file keeps its kind, lossless or lossy; anything else is written lossy.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Webp.cs" region="webp"/>
/// </example>
public sealed class WebpWriterOptions : WriterOptions
{
    private readonly int _quality = 75;
    private readonly int _effort = 4;
    private readonly int _alphaQuality = 100;
    private readonly int _nearLossless = 100;

    /// <summary>Gets the default options.</summary>
    public static new WebpWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets whether to write lossless (VP8L, every pixel exact) or lossy (VP8) data, or <see langword="null"/> (the default) to keep
    /// the kind of the WebP file the raster was read from (<see cref="WebpDetails.Lossless"/>), and write lossy otherwise.
    /// </summary>
    public bool? Lossless { get; init; }

    /// <summary>
    /// Gets the lossy quality, 0 (smallest) to 100 (best), on libwebp's scale, so the same number gives about the same file as
    /// cwebp -q. Ignored for lossless data. Defaults to 75.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 100.</exception>
    public int Quality
    {
        get => _quality;
        init => _quality = InRange(value, 100);
    }

    /// <summary>
    /// Gets how hard the encoder works, 0 (fastest) to 6 (smallest files), on libwebp's method scale, for both kinds. Defaults to 4.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 6.</exception>
    public int Effort
    {
        get => _effort;
        init => _effort = InRange(value, 6);
    }

    /// <summary>
    /// Gets the alpha quality of lossy files with transparency, 0 to 100: 100 stores alpha losslessly; lower values reduce the
    /// number of alpha levels first. Defaults to 100.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 100.</exception>
    public int AlphaQuality
    {
        get => _alphaQuality;
        init => _alphaQuality = InRange(value, 100);
    }

    /// <summary>
    /// Gets whether the colors of fully transparent pixels are kept: exactly in lossless data, so every pixel round-trips, and as
    /// closely as the quality allows in lossy data. Defaults to <see langword="true"/>; <see langword="false"/> lets either encoder
    /// change those colors for smaller files (lossy flattens them), as libwebp does unless told to stay exact.
    /// </summary>
    public bool KeepTransparentColors { get; init; } = true;

    /// <summary>
    /// Gets whether lossy data uses a slower RGB to YUV conversion that keeps sharp colored edges, libwebp's sharp YUV. Defaults
    /// to <see langword="false"/>.
    /// </summary>
    public bool SharpYuv { get; init; }

    /// <summary>
    /// Gets the near-lossless level for lossless data, 0 to 100 on libwebp's scale: below 100 the encoder may move pixel values a
    /// little before coding them, for smaller files that are no longer exact. Defaults to 100 (exact).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 100.</exception>
    public int NearLossless
    {
        get => _nearLossless;
        init => _nearLossless = InRange(value, 100);
    }

    /// <summary>
    /// Gets whether each frame of an animation is encoded both lossless and lossy and the smaller kept, as libwebp's img2webp -mixed
    /// does. About twice the encode time. Defaults to <see langword="false"/>.
    /// </summary>
    public bool MixedFrames { get; init; }

    /// <summary>Gets whether to write only the first frame, as a still image. Defaults to <see langword="false"/>.</summary>
    public bool FirstFrameOnly { get; init; }

    private static int InRange(int value, int max)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, max);
        return value;
    }
}
