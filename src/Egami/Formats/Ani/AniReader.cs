using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Formats.Bmp;
using Egami.Formats.Ico;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Ani;

/// <summary>
/// Reads animated cursors: <see cref="AniFile"/> parses the chunks and each stored frame's directory, and Egami's ICO reader
/// decodes the entries. The animation is played out step by step, so a frame the <c>seq </c> list shows twice is in the raster
/// twice; each stored frame is decoded once. All frames have one size, chosen as an icon's entry is: the largest, or with
/// <see cref="ReaderOptions.MinimumSize"/> the smallest big enough. A stored frame without that size is resized from its nearest
/// larger entry.
/// </summary>
internal sealed class AniReader : IFormatReader, IRowSourceReader, IMultiImageReader
{
    internal static readonly AniReader Instance = new();

    /// <summary>A jiffy, the unit of ANI display rates: 1/60 s.</summary>
    private const double TicksPerJiffy = TimeSpan.TicksPerSecond / 60.0;

    private AniReader()
    {
    }

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        return Run(input, options, file =>
        {
            Size size = Choose(file, options);
            (_, int length) = AnimationCanvasRange.Clamp(options.Frames, file.Sequence.Count);
            return new RasterInfo
            {
                Format = AniFormat.Info,
                Width = size.Width,
                Height = size.Height,
                Layout = Rgba8.Layout,
                FrameCount = length,
                ImageCount = file.Sizes().Count,
                LoopCount = 0,
                HasMetadata = file.Title is not null || file.Artist is not null,
                EstimatedBytes = (long)size.Width * size.Height * 4 * Math.Max(1, length),
            };
        });
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return Run(input, options, file => Build<TPixel>(input, file, options));
    }

    public Raster Read(EgamiInput input, ReaderOptions options) => Read<Rgba8>(input, options);

    /// <summary>Decodes the animation whole, so <see cref="ReaderOptions.MinimumSize"/> picks the size before the row stage reduces it.</summary>
    public RowSource Open(EgamiInput input, ReaderOptions options) => BufferedRowSource.From(Read(input, options));

    public IReadOnlyList<ReaderOptions> Images(EgamiInput input, ReaderOptions options)
    {
        IReadOnlyList<Size> sizes = Run(input, options, file => file.Sizes());
        return [.. sizes.Select(size => new AniSizeOptions(options) { Size = size })];
    }

    /// <summary>Reads the whole file, which cursors keep small, parses it and runs <paramref name="action"/>.</summary>
    private static T Run<T>(EgamiInput input, ReaderOptions options, Func<AniFile, T> action)
    {
        (byte[] data, int length, bool pooled) = InputBytes.ReadAll(input);
        try
        {
            return action(AniFile.Parse(input, data.AsMemory(0, length), options.Recovery == RecoveryMode.Strict));
        }
        finally
        {
            if (pooled)
            {
                ArrayPool<byte>.Shared.Return(data);
            }
        }
    }

    /// <summary>
    /// Picks the size every frame is loaded at: the one LoadAll named, else with a minimum size the smallest size at least that
    /// big, else the largest.
    /// </summary>
    private static Size Choose(AniFile file, ReaderOptions options)
    {
        IReadOnlyList<Size> sizes = file.Sizes();
        if (sizes.Count == 0)
        {
            throw new CorruptDataException(AniFormat.Info.Name, -1, "No frame of the animation has a readable size.");
        }

        if (options is AniSizeOptions named)
        {
            return named.Size;
        }

        if (options.MinimumSize is Size minimum)
        {
            Size? enough = sizes.Where(s => Fits(s, minimum, options.Region)).Cast<Size?>().LastOrDefault();
            if (enough is Size found)
            {
                return found;
            }
        }

        return sizes[0];
    }

    /// <summary>Returns whether a size is at least <paramref name="minimum"/>, counting only <paramref name="region"/> when one is asked for.</summary>
    private static bool Fits(Size size, Size minimum, Rect? region)
    {
        int width = size.Width;
        int height = size.Height;
        if (region is Rect r)
        {
            width = Math.Max(0, Math.Min(size.Width, r.X + r.Width) - Math.Max(0, r.X));
            height = Math.Max(0, Math.Min(size.Height, r.Y + r.Height) - Math.Max(0, r.Y));
        }

        return width >= minimum.Width && height >= minimum.Height;
    }

    /// <summary>Decodes the stored frames the chosen steps show, once each, then lays the steps out as frames.</summary>
    private static Raster<TPixel> Build<TPixel>(EgamiInput input, AniFile file, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Size target = Choose(file, options);
        input.Limits.CheckFrameSize(target.Width, target.Height);
        (int offset, int length) = AnimationCanvasRange.Clamp(options.Frames, file.Sequence.Count);
        if (length == 0)
        {
            throw input.Corrupt(-1, "No frame is in the requested range.");
        }

        input.CheckFrameCount(length);
        WarningList warnings = new();
        Dictionary<int, Decoded?> decoded = [];
        Raster<TPixel>? raster = null;
        try
        {
            for (int step = offset; step < offset + length; step++)
            {
                int index = file.Sequence[step];
                if (!decoded.ContainsKey(index))
                {
                    decoded[index] = Decode(input, file, file.Pictures[index], target, options, warnings);
                }
            }

            for (int step = offset; step < offset + length; step++)
            {
                if (decoded[file.Sequence[step]] is not Decoded picture)
                {
                    continue;
                }

                Frame<TPixel> frame = raster is null ? (raster = input.CreateRaster<TPixel>(target.Width, target.Height)).Frames[0] : input.AddFrame(raster);
                for (int y = 0; y < target.Height; y++)
                {
                    PixelConvert.Convert<Rgba8, TPixel>(picture.Pixels.Row(y), frame.Row(y));
                }

                frame.Metadata.Delay = Jiffies(file.Rates[step]);
                frame.Metadata.Disposal = FrameDisposal.ClearToBackground;
                frame.Metadata.Blend = FrameBlend.Source;
                frame.Metadata.FormatDetails = picture.Details.Copy();
            }

            if (raster is null)
            {
                throw input.Corrupt(-1, "No frame of the animation could be decoded.");
            }

            raster.Metadata.FormatName = AniFormat.Info.Name;
            raster.Metadata.LoopCount = 0;
            raster.Metadata.FormatDetails = new AniDetails
            {
                Title = file.Title,
                Artist = file.Artist,
                DefaultDelay = Jiffies(file.DefaultRate),
                StoredFrames = file.Pictures.Count,
                Steps = [.. file.Sequence],
                Sizes = file.Sizes(),
                RawBitmaps = file.RawBitmaps,
            };

            // The keys PNG and TGA use, so the title and artist carry over when the cursor is saved in another format.
            if (file.Title is not null)
            {
                raster.Metadata.Text.Add(new TextEntry("Title", file.Title));
            }

            if (file.Artist is not null)
            {
                raster.Metadata.Text.Add(new TextEntry("Author", file.Artist));
            }

            raster.Metadata.WarningList.Prepend(file.Warnings);
            raster.Metadata.WarningList.AddRange(warnings);
            return raster;
        }
        catch
        {
            raster?.Dispose();
            throw;
        }
        finally
        {
            foreach (Decoded? picture in decoded.Values)
            {
                picture?.Pixels.Dispose();
            }
        }
    }

    /// <summary>
    /// Decodes one stored frame at <paramref name="target"/>: the entry of exactly that size (the deepest, then the first), else the
    /// smallest larger entry, else the largest, resized. A frame that cannot be decoded is left out with a warning in lenient mode,
    /// and its steps with it.
    /// </summary>
    private static Decoded? Decode(EgamiInput input, AniFile file, AniFile.Picture picture, Size target, ReaderOptions options, WarningList warnings)
    {
        bool strict = options.Recovery == RecoveryMode.Strict;
        ReadOnlyMemory<byte> bytes = file.Data.Slice(picture.Offset, picture.Length);
        if (picture.Directory is IcoDirectory directory)
        {
            List<IcoDirectory.Entry> order = Order(directory.Entries, target);
            for (int i = 0; i < order.Count; i++)
            {
                IcoDirectory.Entry entry = order[i];
                Raster<Rgba8> pixels;
                try
                {
                    pixels = IcoReader.Decode<Rgba8>(input, bytes.Slice(entry.Offset, entry.Size), entry, options);
                }
                catch (CorruptDataException e) when (!strict)
                {
                    if (i + 1 < order.Count)
                    {
                        warnings.Add("Ico.EntryDamaged", $"Frame {picture.Index}'s entry {entry.Index} could not be decoded ({e.Message}); entry {order[i + 1].Index} was used instead.", e);
                        continue;
                    }

                    warnings.Add("Ani.FrameDamaged", $"Frame {picture.Index} could not be decoded ({e.Message}); the steps that show it were left out.", e);
                    return null;
                }

                Point? hotspot = entry.Hotspot;
                pixels = Fit(pixels, target, picture.Index, warnings, ref hotspot);
                IcoDetails details = new()
                {
                    Index = entry.Index,
                    Encoding = entry.Encoding,
                    BitsPerPixel = entry.Bits,
                    Hotspot = hotspot,
                    Entries = [.. directory.Entries.Select(e => e.Public)],
                };
                warnings.AddRange(directory.Warnings);
                return new Decoded(pixels, details);
            }

            return null;
        }

        RawFrame raw = picture.Raw!;
        Raster<Rgba8> rawPixels;
        try
        {
            rawPixels = DecodeRaw(input, bytes, raw, options);
        }
        catch (CorruptDataException e) when (!strict)
        {
            warnings.Add("Ani.FrameDamaged", $"Frame {picture.Index} could not be decoded ({e.Message}); the steps that show it were left out.", e);
            return null;
        }

        Point? none = null;
        rawPixels = Fit(rawPixels, target, picture.Index, warnings, ref none);
        return new Decoded(rawPixels, new IcoDetails { Index = 0, Encoding = IcoEntryEncoding.Bmp, BitsPerPixel = raw.Bits, Hotspot = null, Entries = [] });
    }

    /// <summary>Orders a frame's entries for a size: exact ones (deepest, then first), then larger ones (smallest first), then the rest (largest first).</summary>
    private static List<IcoDirectory.Entry> Order(IReadOnlyList<IcoDirectory.Entry> entries, Size target)
    {
        IEnumerable<IcoDirectory.Entry> exact = entries.Where(e => e.Width == target.Width && e.Height == target.Height)
            .OrderByDescending(e => e.RankBits).ThenBy(e => e.Index);
        IEnumerable<IcoDirectory.Entry> larger = entries.Where(e => e.Width >= target.Width && e.Height >= target.Height && (e.Width, e.Height) != (target.Width, target.Height))
            .OrderBy(e => e.Area).ThenByDescending(e => e.RankBits).ThenBy(e => e.Index);
        IEnumerable<IcoDirectory.Entry> rest = entries.Where(e => e.Width < target.Width || e.Height < target.Height)
            .OrderByDescending(e => e.Area).ThenByDescending(e => e.RankBits).ThenBy(e => e.Index);
        return [.. exact, .. larger, .. rest];
    }

    /// <summary>Resizes a decoded frame that is not at the animation's size, scaling its hotspot with it, and says so.</summary>
    private static Raster<Rgba8> Fit(Raster<Rgba8> pixels, Size target, int index, WarningList warnings, ref Point? hotspot)
    {
        if (pixels.Width == target.Width && pixels.Height == target.Height)
        {
            return pixels;
        }

        try
        {
            warnings.Add("Ani.SizeMissing", $"Frame {index} has no {target.Width}×{target.Height} image; its {pixels.Width}×{pixels.Height} image was resized.");
            if (hotspot is Point h)
            {
                hotspot = new Point(
                    Math.Clamp((int)((long)h.X * target.Width / pixels.Width), 0, target.Width - 1),
                    Math.Clamp((int)((long)h.Y * target.Height / pixels.Height), 0, target.Height - 1));
            }

            return pixels.Derive(ops => ops.Resize(target.Width, target.Height));
        }
        finally
        {
            pixels.Dispose();
        }
    }

    /// <summary>Decodes a raw-bitmap frame.</summary>
    private static Raster<Rgba8> DecodeRaw(EgamiInput input, ReadOnlyMemory<byte> bytes, RawFrame raw, ReaderOptions options)
    {
        input.Limits.CheckFrameSize(raw.Width, raw.Height);
        switch (raw.Kind)
        {
            case RawFrameKind.BmpFile:
                return Bmp(input, bytes, options);
            case RawFrameKind.IconDib:
                return IcoReader.DecodeBmp(input, bytes.Span, raw.Dib!, options);
            case RawFrameKind.Dib:
                byte[] file = new byte[14 + bytes.Length];
                file[0] = (byte)'B';
                file[1] = (byte)'M';
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2), (uint)file.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10), (uint)(14 + raw.Dib!.PixelOffset));
                bytes.Span.CopyTo(file.AsSpan(14));
                return Bmp(input, file, options);
            default:
                return Rows(input, bytes.Span, raw);
        }
    }

    private static Raster<Rgba8> Bmp(EgamiInput input, ReadOnlyMemory<byte> bytes, ReaderOptions options)
    {
        Raster<Rgba8> raster = IcoReader.WithInput(input, bytes, sub => BmpReader.Instance.Read<Rgba8>(sub, IcoReader.Inner(options)));
        raster.Metadata.FormatDetails = null;
        return raster;
    }

    /// <summary>Decodes bare bottom-up rows at 24 or 32 bits; 32-bit rows whose alpha is 0 everywhere are opaque, as in icons.</summary>
    private static Raster<Rgba8> Rows(EgamiInput input, ReadOnlySpan<byte> bytes, RawFrame raw)
    {
        int stride = (int)((((long)raw.Width * raw.Bits) + 31) / 32 * 4);
        Raster<Rgba8> raster = input.CreateRaster<Rgba8>(raw.Width, raw.Height);
        bool alpha = false;
        for (int fileRow = 0; fileRow < raw.Height; fileRow++)
        {
            ReadOnlySpan<byte> row = bytes.Slice(fileRow * stride, stride);
            Span<Rgba8> target = raster.Row(raw.Height - 1 - fileRow);
            if (raw.Bits == 32)
            {
                PixelConvert.Convert<Bgra8, Rgba8>(MemoryMarshal.Cast<byte, Bgra8>(row[..(raw.Width * 4)]), target);
                foreach (Rgba8 p in target)
                {
                    alpha |= p.A != 0;
                }
            }
            else
            {
                PixelConvert.Convert<Bgr8, Rgba8>(MemoryMarshal.Cast<byte, Bgr8>(row[..(raw.Width * 3)]), target);
            }
        }

        if (raw.Bits == 32 && !alpha)
        {
            for (int y = 0; y < raw.Height; y++)
            {
                foreach (ref Rgba8 p in raster.Row(y))
                {
                    p.A = 255;
                }
            }
        }

        return raster;
    }

    private static TimeSpan Jiffies(int rate) => TimeSpan.FromTicks((long)Math.Round(rate * TicksPerJiffy));

    /// <summary>A stored frame decoded at the animation's size, with what its frames' details say.</summary>
    private sealed record Decoded(Raster<Rgba8> Pixels, IcoDetails Details);
}

/// <summary>The options LoadAll loads one size of an animated cursor with.</summary>
internal sealed record AniSizeOptions : ReaderOptions
{
    internal AniSizeOptions(ReaderOptions options)
        : base(options)
    {
    }

    /// <summary>Gets the size every frame is loaded at.</summary>
    public required Size Size { get; init; }
}
