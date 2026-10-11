using System.Buffers;

using Egami.Pixels;

namespace Egami.Formats;

/// <summary>What a writer keeps of a raster, as it is about to write it (Diagnostics Design, question 8).</summary>
[Flags]
internal enum SaveKeeps
{
    /// <summary>Nothing beyond 8-bit RGB or gray pixels.</summary>
    None = 0,

    /// <summary>Alpha, or binary transparency.</summary>
    Alpha = 1 << 0,

    /// <summary>Samples deeper than 8 bits.</summary>
    Wide = 1 << 1,

    /// <summary>Floating-point samples, values outside 0 to 1 included.</summary>
    Float = 1 << 2,

    /// <summary>CMYK inks.</summary>
    Cmyk = 1 << 3,

    /// <summary>Every frame or page.</summary>
    Frames = 1 << 4,

    /// <summary>The EXIF block.</summary>
    Exif = 1 << 5,

    /// <summary>The XMP packet.</summary>
    Xmp = 1 << 6,

    /// <summary>The IPTC datasets.</summary>
    Iptc = 1 << 7,

    /// <summary>The ICC profile.</summary>
    Icc = 1 << 8,

    /// <summary>Every text entry.</summary>
    Text = 1 << 9,

    /// <summary>Text entries keyed "Comment" only.</summary>
    Comment = 1 << 10,

    /// <summary>The resolution.</summary>
    Resolution = 1 << 11,

    /// <summary>Every kind of metadata.</summary>
    Metadata = Exif | Xmp | Iptc | Icc | Text | Resolution,
}

/// <summary>
/// Reports, as Warnings, what a save loses without being asked: alpha, depth, CMYK inks, frames or metadata the format or its
/// default options cannot hold. A choice the caller made in the options is not reported. Nothing is examined unless a handler
/// takes Warnings.
/// </summary>
internal static class SaveLosses
{
    private static readonly string[] s_comment = ["Comment"];

    /// <summary>Compares the raster or rows about to be written with what the writer keeps, and reports each loss once.</summary>
    /// <param name="start">The encode, for its pixels, metadata, options and context.</param>
    /// <param name="format">The format's name, which starts each message.</param>
    /// <param name="keeps">What the writer keeps, as it will write this image.</param>
    /// <param name="textKeys">With <see cref="SaveKeeps.Comment"/>, the text keys the writer keeps; "Comment" when not given.</param>
    internal static void Report<TPixel>(EncodeStart<TPixel> start, string format, SaveKeeps keeps, string[]? textKeys = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        EgamiContext context = start.Context;
        if (!context.Reports(DiagnosticLevel.Warning))
        {
            return;
        }

        DiagnosticReporter? diagnostics = null;
        PixelLayoutInfo layout = TPixel.Layout;
        Raster<TPixel>? whole = start.Whole;
        if (whole is { FrameCount: > 1 } && (keeps & SaveKeeps.Frames) == 0)
        {
            context.Emit(ref diagnostics, DiagnosticLevel.Warning, "Save.FramesDropped", $"{format} holds one image here; only the first of the raster's {whole.FrameCount} frames was written.");
        }

        if (layout.HasAlpha && (keeps & SaveKeeps.Alpha) == 0 && (whole is null || !IsOpaque(whole)))
        {
            context.Emit(ref diagnostics, DiagnosticLevel.Warning, "Save.AlphaDropped", $"{format} as written here has no alpha; the pixels were written opaque.");
        }

        bool isFloat = layout.ChannelType is PixelChannelType.Float16 or PixelChannelType.Float32;
        if (isFloat && (keeps & SaveKeeps.Float) == 0)
        {
            string to = (keeps & SaveKeeps.Wide) != 0 ? "16-bit" : "8-bit";
            context.Emit(ref diagnostics, DiagnosticLevel.Warning, "Save.DepthReduced", $"{format} holds no floating-point samples here; they were written as {to} integers, values outside 0 to 1 clipped.");
        }
        else if (!isFloat && layout.BitsPerChannel > 8 && (keeps & SaveKeeps.Wide) == 0)
        {
            context.Emit(ref diagnostics, DiagnosticLevel.Warning, "Save.DepthReduced", $"{format} holds 8-bit samples here; the {layout.BitsPerChannel}-bit samples were rounded to 8 bits.");
        }

        if (layout.ChannelOrder == PixelChannelOrder.Cmyk && (keeps & SaveKeeps.Cmyk) == 0)
        {
            context.Emit(ref diagnostics, DiagnosticLevel.Warning, "Save.ConvertedToRgb", $"{format} holds no CMYK here; the inks were converted to RGB.");
        }

        if (!start.Options.StripMetadata && Dropped(start.Metadata, keeps, textKeys ?? s_comment) is string dropped)
        {
            context.Emit(ref diagnostics, DiagnosticLevel.Warning, "Save.MetadataDropped", $"{format} cannot hold {dropped}; it was not written.");
        }

        diagnostics?.Complete();
    }

    /// <summary>Reports one loss a writer finds itself, such as metadata too large for the format.</summary>
    internal static void Report(EgamiContext context, string id, string message)
    {
        DiagnosticReporter? diagnostics = null;
        context.Emit(ref diagnostics, DiagnosticLevel.Warning, id, $"{message}");
        diagnostics?.Complete();
    }

    // Lists the metadata the raster has that the writer does not keep, or returns null.
    private static string? Dropped(Metadata.RasterMetadata metadata, SaveKeeps keeps, string[] textKeys)
    {
        List<string>? kinds = null;
        void Add(string kind) => (kinds ??= []).Add(kind);
        if (metadata.Exif is not null && (keeps & SaveKeeps.Exif) == 0)
        {
            Add("EXIF");
        }

        if (!metadata.Xmp.IsEmpty && (keeps & SaveKeeps.Xmp) == 0)
        {
            Add("XMP");
        }

        if (metadata.HasIptc && (keeps & SaveKeeps.Iptc) == 0)
        {
            Add("IPTC");
        }

        if (!metadata.IccProfile.IsEmpty && (keeps & SaveKeeps.Icc) == 0)
        {
            Add("the ICC profile");
        }

        if ((keeps & SaveKeeps.Text) == 0)
        {
            bool comments = (keeps & SaveKeeps.Comment) != 0;
            for (int i = 0; i < metadata.Text.Count; i++)
            {
                if (!comments || !Kept(textKeys, metadata.Text[i].Key))
                {
                    Add(comments ? $"text other than {string.Join(" and ", textKeys)}" : "text entries");
                    break;
                }
            }
        }

        if (metadata.Resolution is not null && (keeps & SaveKeeps.Resolution) == 0)
        {
            Add("the resolution");
        }

        return kinds is null ? null : kinds.Count == 1 ? kinds[0] : $"{string.Join(", ", kinds.GetRange(0, kinds.Count - 1))} or {kinds[^1]}";
    }

    private static bool Kept(string[] keys, string key)
    {
        foreach (string kept in keys)
        {
            if (string.Equals(kept, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOpaque<TPixel>(Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Rgba8[] row = ArrayPool<Rgba8>.Shared.Rent(raster.Width);
        try
        {
            for (int f = 0; f < raster.FrameCount; f++)
            {
                for (int y = 0; y < raster.Height; y++)
                {
                    Span<Rgba8> pixels = row.AsSpan(0, raster.Width);
                    PixelConvert.Convert<TPixel, Rgba8>(raster.Frames[f].Row(y), pixels);
                    foreach (Rgba8 p in pixels)
                    {
                        if (p.A != 255)
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }
        finally
        {
            ArrayPool<Rgba8>.Shared.Return(row);
        }
    }
}
