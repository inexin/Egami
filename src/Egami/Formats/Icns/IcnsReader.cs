using System.Buffers;
using System.Buffers.Binary;

using Egami.Formats.Ico;
using Egami.Formats.Jpeg2000;
using Egami.Formats.Png;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Icns;

/// <summary>
/// Reads ICNS, written from Apple's description of the format: an element parser over Egami's PNG and JPEG 2000 readers, which
/// decode those entries whole, and decoders for the rest: the run-length encoded ARGB and RGB planes (a control byte below 128
/// copies that many bytes plus one, else repeats the next byte that many less 125 times), the 8-bit masks, and the old 1, 4
/// and 8-bit icons on the Mac OS system palettes, masked by the second half of the 1-bit icon of their size.
/// </summary>
internal sealed class IcnsReader : IFormatReader, IRowSourceReader, IMultiImageReader
{
    internal static readonly IcnsReader Instance = new();

    /// <summary>The Mac OS 16-color palette, as 8-bit RGB.</summary>
    private static readonly Rgba8[] Palette4 =
    [
        new(0xFF, 0xFF, 0xFF, 255), new(0xFC, 0xF3, 0x05, 255), new(0xFF, 0x64, 0x02, 255), new(0xDD, 0x08, 0x06, 255),
        new(0xF2, 0x08, 0x84, 255), new(0x46, 0x00, 0xA5, 255), new(0x00, 0x00, 0xD4, 255), new(0x02, 0xAB, 0xEA, 255),
        new(0x1F, 0xB7, 0x14, 255), new(0x00, 0x64, 0x11, 255), new(0x56, 0x2C, 0x05, 255), new(0x90, 0x71, 0x3A, 255),
        new(0xC0, 0xC0, 0xC0, 255), new(0x80, 0x80, 0x80, 255), new(0x40, 0x40, 0x40, 255), new(0x00, 0x00, 0x00, 255),
    ];

    /// <summary>The Mac OS 256-color palette: a 6×6×6 cube from white without black, then ramps of red, green, blue and gray, then black.</summary>
    private static readonly Rgba8[] Palette8 = SystemPalette();

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        return Run(input, options, (directory, _) =>
        {
            IcnsDirectory.Entry entry = directory.Candidates(options)[0];
            return new RasterInfo
            {
                Format = IcnsFormat.Info,
                Width = entry.Width,
                Height = entry.Height,
                Layout = Rgba8.Layout,
                FrameCount = 1,
                ImageCount = directory.Entries.Count,
                HasMetadata = false,
                EstimatedBytes = (long)entry.Width * entry.Height * 4,
            };
        });
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = Run(input, options, (directory, data) => ReadEntry<TPixel>(input, directory, data, options));
        raster.Metadata.FormatName = IcnsFormat.Info.Name;
        return raster;
    }

    public Raster Read(EgamiInput input, ReaderOptions options) => Read<Rgba8>(input, options);

    /// <summary>Decodes the chosen entry whole, so <see cref="ReaderOptions.MinimumSize"/> picks the entry before the row stage reduces it.</summary>
    public RowSource Open(EgamiInput input, ReaderOptions options) => BufferedRowSource.From(Read(input, options));

    public IReadOnlyList<ReaderOptions> Images(EgamiInput input, ReaderOptions options)
    {
        int count = Run(input, options, (directory, _) => directory.Entries.Count);
        IcnsReaderOptions icns = options as IcnsReaderOptions ?? new IcnsReaderOptions
        {
            Recovery = options.Recovery,
            ReadMetadata = options.ReadMetadata,
            ConvertColorTo = options.ConvertColorTo,
            Frames = options.Frames,
            MinimumSize = options.MinimumSize,
            Region = options.Region,
            Format = options.Format,
        };
        return [.. Enumerable.Range(0, count).Select(i => icns with { Entry = i })];
    }

    /// <summary>Reads the whole file, which icons keep small, parses the elements and runs <paramref name="action"/>.</summary>
    private static T Run<T>(EgamiInput input, ReaderOptions options, Func<IcnsDirectory, ReadOnlyMemory<byte>, T> action)
    {
        (byte[] data, int length, bool pooled) = InputBytes.ReadAll(input);
        try
        {
            IcnsDirectory directory = IcnsDirectory.Parse(data.AsSpan(0, length), options.Recovery == RecoveryMode.Strict);
            return action(directory, data.AsMemory(0, length));
        }
        finally
        {
            if (pooled)
            {
                ArrayPool<byte>.Shared.Return(data);
            }
        }
    }

    private static Raster<TPixel> ReadEntry<TPixel>(EgamiInput input, IcnsDirectory directory, ReadOnlyMemory<byte> data, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        bool strict = options.Recovery == RecoveryMode.Strict;
        IReadOnlyList<IcnsDirectory.Entry> candidates = directory.Candidates(options);
        WarningList skipped = new();
        for (int i = 0; ; i++)
        {
            IcnsDirectory.Entry entry = candidates[i];
            Raster<TPixel> raster;
            try
            {
                raster = Decode<TPixel>(input, data, entry, options);
            }
            catch (CorruptDataException e) when (!strict && i + 1 < candidates.Count && options is not IcnsReaderOptions { Entry: not null })
            {
                skipped.Add("Icns.EntryDamaged", $"Entry {entry.Index} ('{entry.Type}') could not be decoded ({e.Message}); entry {candidates[i + 1].Index} was loaded instead.", e);
                continue;
            }

            try
            {
                raster.Metadata.FormatDetails = new IcnsDetails
                {
                    Index = entry.Index,
                    Type = entry.Type,
                    Encoding = entry.Encoding,
                    BitsPerPixel = entry.Bits,
                    Entries = [.. directory.Entries.Select(e => e.Public)],
                };
                raster.Metadata.WarningList.Prepend(directory.Warnings);
                raster.Metadata.WarningList.AddRange(skipped);
                return raster;
            }
            catch
            {
                raster.Dispose();
                throw;
            }
        }
    }

    private static Raster<TPixel> Decode<TPixel>(EgamiInput input, ReadOnlyMemory<byte> data, IcnsDirectory.Entry entry, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ReadOnlyMemory<byte> body = data.Slice(entry.Offset, entry.Length);
        switch (entry.Kind)
        {
            case IcnsDirectory.Kind.Png:
                return IcoReader.WithInput(input, body, sub => PngReader.Instance.Read<TPixel>(sub, IcoReader.Inner(options)));
            case IcnsDirectory.Kind.Jpeg2000:
                return IcoReader.WithInput(input, body, sub => Jpeg2000Reader.Instance.Read<TPixel>(sub, IcoReader.Inner(options)));
        }

        input.Limits.CheckFrameSize(entry.Width, entry.Height);
        Raster<Rgba8> rgba = input.CreateRaster<Rgba8>(entry.Width, entry.Height);
        try
        {
            ReadOnlySpan<byte> span = data.Span;
            int pixels = entry.Width * entry.Height;
            switch (entry.Kind)
            {
                case IcnsDirectory.Kind.Argb:
                    if (!span.Slice(entry.Offset, entry.Length).StartsWith("ARGB"u8))
                    {
                        throw new CorruptDataException(IcnsFormat.Info.Name, entry.Offset, $"The '{entry.Type}' element is neither PNG, JPEG 2000 nor ARGB.");
                    }

                    Planes(span.Slice(entry.Offset + 4, entry.Length - 4), rgba, [3, 0, 1, 2], entry.Offset + 4);
                    break;
                case IcnsDirectory.Kind.Rgb:
                    ReadOnlySpan<byte> rgb = span.Slice(entry.Offset, entry.Length);
                    if (rgb.Length == pixels * 4)
                    {
                        // Uncompressed: a pad byte, then red, green and blue.
                        for (int i = 0; i < pixels; i++)
                        {
                            rgba.Row(i / entry.Width)[i % entry.Width] = new Rgba8(rgb[(4 * i) + 1], rgb[(4 * i) + 2], rgb[(4 * i) + 3], 255);
                        }
                    }
                    else
                    {
                        // it32 data starts with four zero bytes.
                        int skip = entry.Type == "it32" && rgb.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(rgb) == 0 ? 4 : 0;
                        Planes(rgb[skip..], rgba, [0, 1, 2], entry.Offset + skip);
                        SetAlpha(rgba, 255);
                    }

                    if (entry.AlphaMask is (int maskOffset, int maskLength))
                    {
                        AlphaMask(span.Slice(maskOffset, maskLength), rgba, maskOffset);
                    }
                    else if (entry.BitMask is (int bitOffset, int bitLength))
                    {
                        BitMask(span.Slice(bitOffset, bitLength), rgba);
                    }

                    break;
                default:
                    Indexed(span.Slice(entry.Offset, entry.Length), entry, rgba);
                    if (entry.BitMask is (int monoOffset, int monoLength))
                    {
                        BitMask(span.Slice(monoOffset, monoLength), rgba);
                    }

                    break;
            }

            if (typeof(TPixel) == typeof(Rgba8))
            {
                return (Raster<TPixel>)(object)rgba;
            }

            Raster<TPixel> raster = input.CreateRaster<TPixel>(rgba.Width, rgba.Height);
            for (int y = 0; y < rgba.Height; y++)
            {
                PixelConvert.Convert<Rgba8, TPixel>(rgba.Row(y), raster.Row(y));
            }

            rgba.Dispose();
            return raster;
        }
        catch
        {
            rgba.Dispose();
            throw;
        }
    }

    /// <summary>Unpacks run-length encoded planes, one after another, into the channels named (0 red, 1 green, 2 blue, 3 alpha).</summary>
    private static void Planes(ReadOnlySpan<byte> packed, Raster<Rgba8> raster, ReadOnlySpan<int> channels, int offset)
    {
        int width = raster.Width;
        int pixels = width * raster.Height;
        int at = 0;
        foreach (int channel in channels)
        {
            int i = 0;
            while (i < pixels)
            {
                if (at >= packed.Length)
                {
                    throw new CorruptDataException(IcnsFormat.Info.Name, offset + at, "The run-length encoded planes end early.") { IsTruncation = true };
                }

                int control = packed[at++];
                bool repeat = control >= 0x80;
                int count = repeat ? control - 125 : control + 1;
                if (at + (repeat ? 1 : count) > packed.Length)
                {
                    throw new CorruptDataException(IcnsFormat.Info.Name, offset + at, "The run-length encoded planes end early.") { IsTruncation = true };
                }

                count = Math.Min(count, pixels - i);
                for (int k = 0; k < count; k++, i++)
                {
                    byte value = repeat ? packed[at] : packed[at + k];
                    Span<byte> pixel = System.Runtime.InteropServices.MemoryMarshal.AsBytes(raster.Row(i / width).Slice(i % width, 1));
                    pixel[channel] = value;
                }

                at += repeat ? 1 : control + 1;
            }
        }
    }

    private static void AlphaMask(ReadOnlySpan<byte> mask, Raster<Rgba8> raster, int offset)
    {
        int width = raster.Width;
        if (mask.Length < width * raster.Height)
        {
            throw new CorruptDataException(IcnsFormat.Info.Name, offset, $"The 8-bit mask has {mask.Length} bytes; a {width}×{raster.Height} icon needs {width * raster.Height}.") { IsTruncation = true };
        }

        for (int y = 0; y < raster.Height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x].A = mask[(y * width) + x];
            }
        }
    }

    /// <summary>Sets alpha from the second half of a 1-bit icon and mask element; an element without the mask leaves the pixels opaque.</summary>
    private static void BitMask(ReadOnlySpan<byte> element, Raster<Rgba8> raster)
    {
        int stride = (raster.Width + 7) / 8;
        int half = stride * raster.Height;
        if (element.Length < 2 * half)
        {
            return;
        }

        ReadOnlySpan<byte> mask = element.Slice(half, half);
        for (int y = 0; y < raster.Height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < raster.Width; x++)
            {
                row[x].A = (mask[(y * stride) + (x >> 3)] & (0x80 >> (x & 7))) != 0 ? (byte)255 : (byte)0;
            }
        }
    }

    /// <summary>Decodes a 1, 4 or 8-bit icon: rows packed most significant bit first, on the system palettes, 1 being black.</summary>
    private static void Indexed(ReadOnlySpan<byte> body, IcnsDirectory.Entry entry, Raster<Rgba8> raster)
    {
        int bits = entry.Bits;
        int stride = ((raster.Width * bits) + 7) / 8;
        if (body.Length < stride * raster.Height)
        {
            throw new CorruptDataException(IcnsFormat.Info.Name, entry.Offset, $"The '{entry.Type}' element has {body.Length} bytes; it needs {stride * raster.Height}.") { IsTruncation = true };
        }

        int perByte = 8 / bits;
        int mask = (1 << bits) - 1;
        for (int y = 0; y < raster.Height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            ReadOnlySpan<byte> packed = body.Slice(y * stride, stride);
            for (int x = 0; x < raster.Width; x++)
            {
                int index = (packed[x / perByte] >> (8 - (bits * ((x % perByte) + 1)))) & mask;
                row[x] = bits switch
                {
                    1 => index == 0 ? new Rgba8(255, 255, 255, 255) : new Rgba8(0, 0, 0, 255),
                    4 => Palette4[index],
                    _ => Palette8[index],
                };
            }
        }
    }

    private static void SetAlpha(Raster<Rgba8> raster, byte alpha)
    {
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (ref Rgba8 p in raster.Row(y))
            {
                p.A = alpha;
            }
        }
    }

    private static Rgba8[] SystemPalette()
    {
        Rgba8[] palette = new Rgba8[256];
        int i = 0;
        for (int r = 5; r >= 0; r--)
        {
            for (int g = 5; g >= 0; g--)
            {
                for (int b = 5; b >= 0; b--)
                {
                    if (r + g + b > 0)
                    {
                        palette[i++] = new Rgba8((byte)(r * 0x33), (byte)(g * 0x33), (byte)(b * 0x33), 255);
                    }
                }
            }
        }

        ReadOnlySpan<byte> ramp = [0xEE, 0xDD, 0xBB, 0xAA, 0x88, 0x77, 0x55, 0x44, 0x22, 0x11];
        foreach (int channel in (int[])[0, 1, 2, 3])
        {
            foreach (byte v in ramp)
            {
                palette[i++] = channel switch
                {
                    0 => new Rgba8(v, 0, 0, 255),
                    1 => new Rgba8(0, v, 0, 255),
                    2 => new Rgba8(0, 0, v, 255),
                    _ => new Rgba8(v, v, v, 255),
                };
            }
        }

        palette[255] = new Rgba8(0, 0, 0, 255);
        return palette;
    }
}
