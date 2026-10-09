using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Gif;

/// <summary>The GIF format, registered in <see cref="EgamiContext.Default"/>.</summary>
public static class GifFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extension, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "GIF",
        MimeTypes = ["image/gif"],
        Extensions = ["gif"],
        SignatureLength = 6,
        Matches = GifReader.Matches,
        Reader = GifReader.Instance,
        Writer = GifWriter.Instance,
    };
}

/// <summary>An application extension block kept unchanged for round trips.</summary>
/// <param name="Identifier">The 8-character application identifier and 3-character authentication code, such as "NETSCAPE2.0".</param>
/// <param name="Data">The data of its sub-blocks, joined.</param>
public sealed record GifApplicationExtension(string Identifier, ReadOnlyMemory<byte> Data);

/// <summary>Details only GIF has, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
public sealed class GifDetails : FormatDetails
{
    /// <summary>Gets or sets the version from the header: "87a" or "89a".</summary>
    public string Version { get; set; } = "89a";

    /// <summary>Gets or sets the global color table. Empty if the file had none.</summary>
    public ReadOnlyMemory<Rgb8> GlobalPalette { get; set; }

    /// <summary>Gets or sets the background color index from the logical screen descriptor.</summary>
    public byte BackgroundIndex { get; set; }

    /// <summary>Gets or sets the pixel aspect ratio byte from the logical screen descriptor (0 when not given).</summary>
    public byte PixelAspectRatio { get; set; }

    /// <summary>
    /// Gets or sets the loop value as the NETSCAPE2.0 block stored it, counting repeats after the first play (0 for forever), or
    /// <see langword="null"/> if the file had no such block. <see cref="RasterMetadata.LoopCount"/> holds the total plays.
    /// </summary>
    public int? RepeatCount { get; set; }

    /// <summary>Gets the application extensions Egami does not read itself, in file order.</summary>
    public IList<GifApplicationExtension> OtherExtensions { get; } = [];

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        GifDetails copy = new()
        {
            Version = Version,
            GlobalPalette = GlobalPalette.ToArray(),
            BackgroundIndex = BackgroundIndex,
            PixelAspectRatio = PixelAspectRatio,
            RepeatCount = RepeatCount,
        };
        foreach (GifApplicationExtension extension in OtherExtensions)
        {
            copy.OtherExtensions.Add(extension with { Data = extension.Data.ToArray() });
        }

        return copy;
    }
}

/// <summary>Details of one GIF frame, kept in <see cref="FrameMetadata.FormatDetails"/>.</summary>
public sealed class GifFrameDetails : FormatDetails
{
    /// <summary>Gets or sets the frame's own color table. Empty if it used the global one.</summary>
    public ReadOnlyMemory<Rgb8> LocalPalette { get; set; }

    /// <summary>Gets or sets the transparent color index, or <see langword="null"/> if the frame had none.</summary>
    public int? TransparentIndex { get; set; }

    /// <summary>Gets or sets whether the frame was stored interlaced.</summary>
    public bool IsInterlaced { get; set; }

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new GifFrameDetails { LocalPalette = LocalPalette.ToArray(), TransparentIndex = TransparentIndex, IsInterlaced = IsInterlaced };
    }
}
