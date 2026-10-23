using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Formats.Ico;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Ani;

/// <summary>
/// Writes animated cursors: each distinct frame (pixels and hotspot) is stored once as a cursor written by Egami's CUR writer, a
/// <c>seq </c> chunk says which stored frame each step shows when one repeats, and a <c>rate</c> chunk gives each step's time when
/// the delays differ. Delays are rounded to jiffies (1/60 s). The same raster and options give the same bytes.
/// </summary>
internal sealed class AniWriter : IFormatWriter
{
    internal static readonly AniWriter Instance = new();

    /// <summary>A delay shorter than this plays as <see cref="ShortDelayJiffies"/>, as browsers play GIFs.</summary>
    private static readonly TimeSpan s_shortDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>100 ms in jiffies.</summary>
    private const int ShortDelayJiffies = 6;

    private AniWriter()
    {
    }

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        AniWriterOptions ani = options as AniWriterOptions ?? new AniWriterOptions { StripMetadata = options.StripMetadata, OmitSrgbProfile = options.OmitSrgbProfile };
        EgamiContext context = raster.Context;
        AniDetails? details = ani.StripMetadata ? null : raster.Metadata.FormatDetails as AniDetails;
        IcoDetails? rasterIcon = raster.Metadata.FormatDetails as IcoDetails;

        // Without Sizes the frames are written at their own size, fitted to an entry's 256 × 256 once for the whole animation.
        int width = raster.Width;
        int height = raster.Height;
        if (ani.Sizes is null && (width > 256 || height > 256))
        {
            double scale = 256.0 / Math.Max(width, height);
            (width, height) = (Math.Clamp((int)Math.Round(width * scale), 1, 256), Math.Clamp((int)Math.Round(height * scale), 1, 256));
            SaveLosses.Report(context, "Save.IcoFitted", $"ANI: the {raster.Width}×{raster.Height} raster is larger than a cursor entry's 256 × 256; it was fitted to {width}×{height}. Set AniWriterOptions.Sizes to choose the sizes.");
        }

        SaveLosses.Report(new EncodeStart<TPixel>(raster.Width, raster.Height, raster.Metadata, ani, context, raster, seekable: false), "ANI", SaveKeeps.Alpha | SaveKeeps.Frames | (AnyPng(raster, ani) ? SaveKeeps.Icc | SaveKeeps.Resolution : SaveKeeps.None));
        if (raster.Metadata.LoopCount != 0 && raster.FrameCount > 1)
        {
            SaveLosses.Report(context, "Save.AniLoops", $"ANI animations always loop; the raster's LoopCount of {raster.Metadata.LoopCount} was not written.");
        }

        // Steps: the stored frame each one shows, and its time.
        List<int> stored = [];
        List<long> hashes = [];
        List<Point> hotspots = [];
        int[] sequence = new int[raster.FrameCount];
        int[] rates = new int[raster.FrameCount];
        bool raised = false;
        for (int i = 0; i < raster.FrameCount; i++)
        {
            Frame<TPixel> frame = raster.Frames[i];
            Point hotspot = ani.Hotspot ?? (frame.Metadata.FormatDetails as IcoDetails)?.Hotspot ?? rasterIcon?.Hotspot ?? default;
            long hash = Hash(frame, raster.Width, raster.Height);
            int match = -1;
            for (int s = 0; s < stored.Count && match < 0; s++)
            {
                if (hashes[s] == hash && hotspots[s] == hotspot && Same(raster.Frames[stored[s]], frame, raster.Width, raster.Height))
                {
                    match = s;
                }
            }

            if (match < 0)
            {
                match = stored.Count;
                stored.Add(i);
                hashes.Add(hash);
                hotspots.Add(hotspot);
            }

            sequence[i] = match;
            TimeSpan delay = frame.Metadata.Delay;
            if (delay < s_shortDelay)
            {
                raised = true;
                rates[i] = ShortDelayJiffies;
            }
            else
            {
                rates[i] = (int)Math.Clamp(Math.Round(delay.TotalSeconds * 60, MidpointRounding.AwayFromZero), 1, int.MaxValue);
            }
        }

        if (raised && raster.FrameCount > 1)
        {
            SaveLosses.Report(context, "Save.AniDelayRaised", "ANI: frames shown for less than 20 ms were written as 100 ms, as browsers play such GIF frames.");
        }

        // The default rate is the most common one, the first of equals; a rate chunk only when the steps differ.
        int defaultRate = rates.GroupBy(r => r).OrderByDescending(g => g.Count()).First().Key;
        bool needRates = rates.Any(r => r != defaultRate);
        bool needSequence = stored.Count != sequence.Length;

        // Each stored frame becomes a whole CUR file.
        IcoWriterOptions cursor = new()
        {
            Sizes = ani.Sizes,
            Encoding = ani.Encoding,
            Filter = ani.Filter,
            StripMetadata = ani.StripMetadata,
            OmitSrgbProfile = ani.OmitSrgbProfile,
        };
        List<byte[]> icons = new(stored.Count);
        for (int s = 0; s < stored.Count; s++)
        {
            icons.Add(Cursor(raster, stored[s], hotspots[s], width, height, cursor));
        }

        string? title = ani.Title ?? details?.Title;
        string? artist = ani.Artist ?? details?.Artist;

        using MemoryStream body = new();
        body.Write("ACON"u8);
        if (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(artist))
        {
            using MemoryStream info = new();
            info.Write("INFO"u8);
            if (!string.IsNullOrEmpty(title))
            {
                WriteChunk(info, "INAM"u8, Text(title));
            }

            if (!string.IsNullOrEmpty(artist))
            {
                WriteChunk(info, "IART"u8, Text(artist));
            }

            WriteChunk(body, "LIST"u8, info.ToArray());
        }

        Span<byte> header = stackalloc byte[AniFile.HeaderLength];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, AniFile.HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)stored.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)sequence.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)defaultRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[32..], AniFile.IconFlag | (needSequence ? AniFile.SequenceFlag : 0));
        WriteChunk(body, "anih"u8, header);
        if (needRates)
        {
            WriteChunk(body, "rate"u8, Words(rates));
        }

        if (needSequence)
        {
            WriteChunk(body, "seq "u8, Words(sequence));
        }

        using MemoryStream frames = new();
        frames.Write("fram"u8);
        foreach (byte[] icon in icons)
        {
            WriteChunk(frames, "icon"u8, icon);
        }

        WriteChunk(body, "LIST"u8, frames.ToArray());
        Span<byte> riff = stackalloc byte[8];
        "RIFF"u8.CopyTo(riff);
        BinaryPrimitives.WriteUInt32LittleEndian(riff[4..], checked((uint)body.Length));
        output.Write(riff);
        body.Position = 0;
        body.CopyTo(output);
    }

    /// <summary>
    /// Writes frame <paramref name="index"/> as a CUR file: the frame alone, as 8-bit RGBA at <paramref name="width"/> ×
    /// <paramref name="height"/> when no sizes are named, its hotspot in its <see cref="IcoDetails"/> and metadata a cursor cannot
    /// hold already gone, so the CUR writer reports nothing the ANI save has not.
    /// </summary>
    private static byte[] Cursor<TPixel>(Raster<TPixel> raster, int index, Point hotspot, int width, int height, IcoWriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<TPixel> one = raster.CopyFrame(index);
        Raster<Rgba8> rgba = one as Raster<Rgba8> ?? one.As<Rgba8>();
        try
        {
            IcoDetails? own = raster.Frames[index].Metadata.FormatDetails as IcoDetails ?? raster.Metadata.FormatDetails as IcoDetails;
            if (width != raster.Width || height != raster.Height)
            {
                Raster<Rgba8> fitted = rgba.Derive(ops => ops.Resize(width, height, options.Filter));
                if (!ReferenceEquals(rgba, one))
                {
                    rgba.Dispose();
                }

                rgba = fitted;
                hotspot = new Point((int)Math.Floor(hotspot.X * (double)width / raster.Width), (int)Math.Floor(hotspot.Y * (double)height / raster.Height));
                own = null;
            }

            RasterMetadata metadata = rgba.Metadata;
            metadata.Exif = null;
            metadata.Xmp = default;
            metadata.Iptc = null;
            metadata.Text.Clear();
            metadata.LoopCount = 0;
            // The CUR writer keeps a frame's own entry encoding at its own size, and takes the hotspot from these details.
            metadata.FormatDetails = new IcoDetails
            {
                Encoding = own?.Encoding ?? (width == 256 && height == 256 ? IcoEntryEncoding.Png : IcoEntryEncoding.Bmp),
                Hotspot = hotspot,
            };
            using MemoryStream file = new();
            IcoWriter.Cursor.Write(rgba, file, options);
            return file.ToArray();
        }
        finally
        {
            if (!ReferenceEquals(rgba, one))
            {
                rgba.Dispose();
            }
        }
    }

    /// <summary>Returns whether any entry may be written as PNG, which keeps the ICC profile and resolution.</summary>
    private static bool AnyPng(Raster raster, AniWriterOptions options)
    {
        return options.Encoding switch
        {
            IcoEntryEncoding.Png => true,
            IcoEntryEncoding.Bmp => false,
            _ when options.Sizes is IReadOnlyList<int> sizes => sizes.Contains(256),
            _ => (raster.Width == 256 && raster.Height == 256) || raster.Metadata.FormatDetails is IcoDetails { Encoding: IcoEntryEncoding.Png }
                || Enumerable.Range(0, raster.FrameCount).Any(i => raster.FrameMetadataAt(i).FormatDetails is IcoDetails { Encoding: IcoEntryEncoding.Png }),
        };
    }

    private static long Hash<TPixel>(Frame<TPixel> frame, int width, int height)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        HashCode hash = default;
        for (int y = 0; y < height; y++)
        {
            hash.AddBytes(MemoryMarshal.AsBytes(frame.Plane.Row(y)[..width]));
        }

        return hash.ToHashCode();
    }

    private static bool Same<TPixel>(Frame<TPixel> a, Frame<TPixel> b, int width, int height)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        for (int y = 0; y < height; y++)
        {
            if (!MemoryMarshal.AsBytes(a.Plane.Row(y)[..width]).SequenceEqual(MemoryMarshal.AsBytes(b.Plane.Row(y)[..width])))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A zero-terminated INFO string: UTF-8, which is ASCII for the usual titles.</summary>
    private static byte[] Text(string value) => [.. Encoding.UTF8.GetBytes(value), 0];

    private static byte[] Words(int[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), (uint)values[i]);
        }

        return bytes;
    }

    /// <summary>Writes a chunk: its id, its length and its data, padded to an even length.</summary>
    private static void WriteChunk(Stream output, ReadOnlySpan<byte> id, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        id.CopyTo(head);
        BinaryPrimitives.WriteUInt32LittleEndian(head[4..], checked((uint)data.Length));
        output.Write(head);
        output.Write(data);
        if ((data.Length & 1) != 0)
        {
            output.WriteByte(0);
        }
    }
}
