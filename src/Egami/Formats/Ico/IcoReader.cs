using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Formats.Bmp;
using Egami.Formats.Png;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Ico;

/// <summary>
/// Reads ICO and CUR, written from Microsoft's icon documentation: a directory parser over Egami's PNG reader, which decodes PNG
/// entries whole, and its BMP reader, which decodes a BMP entry's color rows after a file header is put in front of them. The
/// only pixel code here is the AND mask, which marks transparent pixels: it applies to entries below 32 bits, and to 32-bit
/// entries whose alpha is 0 everywhere (old writers left it so), as Windows does.
/// </summary>
internal sealed class IcoReader : IFormatReader, IRowSourceReader, IMultiImageReader
{
    internal static readonly IcoReader Icon = new(IcoDirectory.IconType);
    internal static readonly IcoReader Cursor = new(IcoDirectory.CursorType);

    private readonly int _type;

    private IcoReader(int type)
    {
        _type = type;
    }

    private FormatInfo Format => _type == IcoDirectory.IconType ? IcoFormat.Info : CurFormat.Info;

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        return Run(input, options, (directory, _) =>
        {
            IcoDirectory.Entry entry = directory.Candidates(options)[0];
            return new RasterInfo
            {
                Format = Format,
                Width = entry.Width,
                Height = entry.Height,
                Layout = Rgba8.Layout,
                FrameCount = 1,
                ImageCount = directory.Entries.Count,
                HasMetadata = false,
                EstimatedBytes = entry.Area * 4,
            };
        });
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = Run(input, options, (directory, data) => ReadEntry<TPixel>(input, directory, data, options));
        raster.Metadata.FormatName = Format.Name;
        return raster;
    }

    public Raster Read(EgamiInput input, ReaderOptions options) => Read<Rgba8>(input, options);

    /// <summary>Decodes the chosen entry whole, so <see cref="ReaderOptions.MinimumSize"/> picks the entry before the row stage reduces it.</summary>
    public RowSource Open(EgamiInput input, ReaderOptions options) => BufferedRowSource.From(Read(input, options));

    public IReadOnlyList<ReaderOptions> Images(EgamiInput input, ReaderOptions options)
    {
        IReadOnlyList<int> indices = Run(input, options, (directory, _) => (IReadOnlyList<int>)[.. directory.Entries.Select(e => e.Index)]);
        IcoReaderOptions ico = options as IcoReaderOptions ?? new IcoReaderOptions
        {
            Recovery = options.Recovery,
            ReadMetadata = options.ReadMetadata,
            ConvertColorTo = options.ConvertColorTo,
            Frames = options.Frames,
            MinimumSize = options.MinimumSize,
            Region = options.Region,
            Format = options.Format,
        };
        return [.. indices.Select(i => ico with { Entry = i })];
    }

    /// <summary>Reads the whole file, which icons keep small, parses the directory and runs <paramref name="action"/>.</summary>
    private T Run<T>(EgamiInput input, ReaderOptions options, Func<IcoDirectory, ReadOnlyMemory<byte>, T> action)
    {
        (byte[] data, int length, bool pooled) = InputBytes.ReadAll(input);
        try
        {
            IcoDirectory directory = IcoDirectory.Parse(input, data.AsSpan(0, length), _type, options.Recovery == RecoveryMode.Strict);
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

    private static Raster<TPixel> ReadEntry<TPixel>(EgamiInput input, IcoDirectory directory, ReadOnlyMemory<byte> data, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        bool strict = options.Recovery == RecoveryMode.Strict;
        IReadOnlyList<IcoDirectory.Entry> candidates = directory.Candidates(options);
        WarningList skipped = new();
        for (int i = 0; ; i++)
        {
            IcoDirectory.Entry entry = candidates[i];
            Raster<TPixel> raster;
            try
            {
                raster = Decode<TPixel>(input, data.Slice(entry.Offset, entry.Size), entry, options);
            }
            catch (CorruptDataException e) when (!strict && i + 1 < candidates.Count && options is not IcoReaderOptions { Entry: not null })
            {
                skipped.Add("Ico.EntryDamaged", $"Entry {entry.Index} could not be decoded ({e.Message}); entry {candidates[i + 1].Index} was loaded instead.", e);
                continue;
            }

            try
            {
                raster.Metadata.FormatDetails = new IcoDetails
                {
                    Index = entry.Index,
                    Encoding = entry.Encoding,
                    BitsPerPixel = entry.Bits,
                    Hotspot = entry.Hotspot,
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

    /// <summary>The options an entry's own reader gets: the caller's recovery and metadata choices, nothing that reshapes.</summary>
    private static ReaderOptions Inner(ReaderOptions options)
    {
        return new ReaderOptions { Recovery = options.Recovery, ReadMetadata = options.ReadMetadata, Frames = ..1 };
    }

    private static Raster<TPixel> Decode<TPixel>(EgamiInput input, ReadOnlyMemory<byte> bytes, IcoDirectory.Entry entry, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        input.Limits.CheckFrameSize(entry.Width, entry.Height);
        if (entry.Encoding == IcoEntryEncoding.Png)
        {
            return WithInput(input, bytes, sub => PngReader.Instance.Read<TPixel>(sub, Inner(options)));
        }

        Raster<Rgba8> rgba = DecodeBmp(input, bytes.Span, entry.Dib!, options);
        if (typeof(TPixel) == typeof(Rgba8))
        {
            return (Raster<TPixel>)(object)rgba;
        }

        try
        {
            Raster<TPixel> raster = input.CreateRaster<TPixel>(rgba.Width, rgba.Height);
            for (int y = 0; y < rgba.Height; y++)
            {
                PixelConvert.Convert<Rgba8, TPixel>(rgba.Row(y), raster.Row(y));
            }

            raster.Metadata.ReplaceWith(rgba.Metadata);
            return raster;
        }
        finally
        {
            rgba.Dispose();
        }
    }

    /// <summary>Runs a reader over an entry's bytes as its own input.</summary>
    private static T WithInput<T>(EgamiInput input, ReadOnlyMemory<byte> bytes, Func<EgamiInput, T> read)
    {
        byte[] array = MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> segment) ? segment.Array! : bytes.ToArray();
        int offset = segment.Array is null ? 0 : segment.Offset;
        using MemoryStream stream = new(array, offset, bytes.Length, writable: false);
        EgamiInput sub = new(stream, input.Context, input.CancellationToken);
        try
        {
            return read(sub);
        }
        finally
        {
            sub.Dispose();
        }
    }

    /// <summary>
    /// Decodes a BMP entry: a 14-byte file header and the halved height make it a BMP file the BMP reader decodes, then the AND
    /// mask sets alpha where it applies.
    /// </summary>
    private static Raster<Rgba8> DecodeBmp(EgamiInput input, ReadOnlySpan<byte> entry, DibLayout dib, ReaderOptions options)
    {
        if (dib.HeaderSize == 40 && dib.Compression == 0 && dib.Bits is 24 or 32 && dib.PixelOffset + (dib.ColorStride * dib.Height) <= entry.Length)
        {
            return DecodeDirect(input, entry, dib, options);
        }

        byte[] file = new byte[14 + entry.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2), (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10), (uint)(14 + dib.PixelOffset));
        entry.CopyTo(file.AsSpan(14));
        if (dib.HeaderSize == 12)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(14 + 6), (ushort)dib.Height);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(14 + 8), dib.Height);
        }

        Raster<Rgba8> raster = WithInput(input, file, sub => BmpReader.Instance.Read<Rgba8>(sub, Inner(options)));
        try
        {
            raster.Metadata.FormatDetails = null;
            if (dib.Bits < 32 || !AlphaUsed(entry, dib, raster))
            {
                ApplyMask(entry, dib, raster, options.Recovery == RecoveryMode.Strict);
            }

            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Decodes the common entries without the BMP reader: complete 24 and 32-bit rows under a plain BITMAPINFOHEADER, which the
    /// BMP reader would decode to the same pixels, swizzled straight into place.
    /// </summary>
    private static Raster<Rgba8> DecodeDirect(EgamiInput input, ReadOnlySpan<byte> entry, DibLayout dib, ReaderOptions options)
    {
        int width = dib.Width;
        int height = dib.Height;
        int stride = (int)dib.ColorStride;
        Raster<Rgba8> raster = input.CreateRaster<Rgba8>(width, height);
        try
        {
            for (int fileRow = 0; fileRow < height; fileRow++)
            {
                ReadOnlySpan<byte> row = entry.Slice(dib.PixelOffset + (fileRow * stride), stride);
                Span<Rgba8> target = raster.Row(height - 1 - fileRow);
                if (dib.Bits == 32)
                {
                    PixelConvert.Convert<Bgra8, Rgba8>(MemoryMarshal.Cast<byte, Bgra8>(row[..(width * 4)]), target);
                }
                else
                {
                    PixelConvert.Convert<Bgr8, Rgba8>(MemoryMarshal.Cast<byte, Bgr8>(row[..(width * 3)]), target);
                }
            }

            if (dib.Bits < 32 || !AlphaUsed(entry, dib, raster))
            {
                ApplyMask(entry, dib, raster, options.Recovery == RecoveryMode.Strict);
            }

            int xppm = BinaryPrimitives.ReadInt32LittleEndian(entry[24..]);
            int yppm = BinaryPrimitives.ReadInt32LittleEndian(entry[28..]);
            if (xppm > 0 && yppm > 0)
            {
                raster.Metadata.Resolution = new Resolution(xppm, yppm, ResolutionUnit.PerMeter);
            }

            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns whether a 32-bit entry's alpha is used: some stored alpha byte is not 0 (BI_RGB), or some decoded pixel is not fully
    /// transparent (bit fields with an alpha mask).
    /// </summary>
    private static bool AlphaUsed(ReadOnlySpan<byte> entry, DibLayout dib, Raster<Rgba8> raster)
    {
        if (dib.Compression == 0)
        {
            long end = Math.Min(entry.Length, dib.PixelOffset + (dib.ColorStride * dib.Height));
            ReadOnlySpan<byte> pixels = entry[dib.PixelOffset..(int)end];
            ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(pixels[..(pixels.Length & ~3)]);
            return words.IndexOfAnyInRange(0x0100_0000u, uint.MaxValue) >= 0;
        }

        for (int y = 0; y < raster.Height; y++)
        {
            foreach (Rgba8 p in raster.Row(y))
            {
                if (p.A != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Makes transparent every pixel whose mask bit is 1, and opaque every other; mask rows missing from the file count as opaque.</summary>
    private static void ApplyMask(ReadOnlySpan<byte> entry, DibLayout dib, Raster<Rgba8> raster, bool strict)
    {
        int width = raster.Width;
        int height = raster.Height;
        long stride = dib.MaskStride;
        int missing = 0;
        for (int fileRow = 0; fileRow < height; fileRow++)
        {
            Span<Rgba8> row = raster.Row(height - 1 - fileRow);
            long at = dib.MaskOffset + (fileRow * stride);
            if (at + ((width + 7) / 8) > entry.Length)
            {
                missing++;
                foreach (ref Rgba8 p in row)
                {
                    p.A = 255;
                }

                continue;
            }

            ReadOnlySpan<byte> bits = entry.Slice((int)at, (width + 7) / 8);
            for (int x = 0; x < width; x++)
            {
                row[x].A = (bits[x >> 3] & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255;
            }
        }

        if (missing > 0)
        {
            if (strict)
            {
                throw new CorruptDataException(IcoFormat.Info.Name, dib.MaskOffset, $"The entry ends before {missing} rows of its AND mask.") { IsTruncation = true };
            }

            raster.Metadata.WarningList.Add("Ico.MaskMissing", $"The entry ends before {missing} rows of its AND mask; those rows are opaque.");
        }
    }
}
