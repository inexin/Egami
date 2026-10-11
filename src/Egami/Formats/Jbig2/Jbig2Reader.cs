using System.Buffers;
using System.Runtime.Intrinsics;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jbig2;

/// <summary>
/// Reads JBIG2, written from ITU-T T.88 (2018) and the PDF specification's JBIG2Decode filter. Load reads one page (the first
/// unless <see cref="Jbig2ReaderOptions.Page"/> says otherwise) as <see cref="Gray8"/>, black 0 and white 255.
/// </summary>
internal sealed partial class Jbig2Reader : IFormatReader, IMultiImageReader
{
    internal static readonly Jbig2Reader Instance = new();

    /// <summary>The most segments a stream may hold; real files have a few per page.</summary>
    internal const int MaxSegments = 1 << 20;

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        return Run(input, options, (decoder, page) =>
        {
            (int width, int height) = decoder.PageSize(page);
            input.Limits.CheckFrameSize(Math.Max(width, 1), Math.Max(height, 1));
            Jbig2PageInfo info = decoder.PageInfo(page);
            return new RasterInfo
            {
                Format = Jbig2Format.Info,
                Width = width,
                Height = height,
                Layout = Gray8.Layout,
                FrameCount = 1,
                ImageCount = decoder.PageSegments().Count,
                HasMetadata = false,
                EstimatedBytes = (long)width * height,
                Access = RasterAccess.Streamed,
                WorkingBytes = WorkingBytesOf(width, info.Striped && info.MaxStripeSize > 0 ? Math.Min(info.MaxStripeSize, height) : height),
            };
        });
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return Run(input, options, (decoder, page) => ReadPage<TPixel>(input, decoder, page, options));
    }

    public Raster Read(EgamiInput input, ReaderOptions options) => Read<Gray8>(input, options);

    public IReadOnlyList<ReaderOptions> Images(EgamiInput input, ReaderOptions options)
    {
        int count = Run(input, options, (decoder, _) => decoder.PageSegments().Count, anyPage: true);
        Jbig2ReaderOptions jbig2 = options as Jbig2ReaderOptions ?? new Jbig2ReaderOptions
        {
            Recovery = options.Recovery,
            ReadMetadata = options.ReadMetadata,
            ConvertColorTo = options.ConvertColorTo,
            Frames = options.Frames,
            MinimumSize = options.MinimumSize,
            Region = options.Region,
            Format = options.Format,
        };
        return [.. Enumerable.Range(0, count).Select(i => jbig2 with { Page = i })];
    }

    private static Raster<TPixel> ReadPage<TPixel>(EgamiInput input, Jbig2Decoder decoder, int page, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        (int width, int height) = decoder.PageSize(page);
        input.Limits.CheckFrameSize(Math.Max(width, 1), Math.Max(height, 1));
        Jbig2Page decoded = decoder.DecodePage(page, input.CancellationToken);
        Jbig2Bitmap bitmap = decoded.Bitmap;
        Raster<TPixel> raster = input.CreateRaster<TPixel>(bitmap.Width, bitmap.Height);
        try
        {
            byte[] gray = ArrayPool<byte>.Shared.Rent(bitmap.Width);
            try
            {
                for (int y = 0; y < bitmap.Height; y++)
                {
                    if (typeof(TPixel) == typeof(Gray8))
                    {
                        Expand(bitmap.Row(y), System.Runtime.InteropServices.MemoryMarshal.AsBytes(raster.Row(y)));
                        continue;
                    }

                    Span<byte> row = gray.AsSpan(0, bitmap.Width);
                    Expand(bitmap.Row(y), row);
                    PixelConvert.Convert<Gray8, TPixel>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Gray8>(row), raster.Row(y));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(gray);
            }

            Fill(raster.Metadata, decoder, page, decoded, options);
            foreach ((string id, string warning) in decoded.Warnings)
            {
                input.Warn(raster, id, warning);
            }

            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>Each byte of packed bits as the eight gray bytes it expands to, little-endian: a 1 bit (black) to 0, a 0 bit to 255.</summary>
    private static readonly ulong[] GrayBytes = BuildGrayBytes();

    /// <summary>Expands a row of packed bits to gray bytes, eight at a time: a 1 bit (black) to 0, a 0 bit to 255.</summary>
    internal static void Expand(ReadOnlySpan<ulong> words, Span<byte> row)
    {
        int width = row.Length;
        int start = 0;
        if (Vector256.IsHardwareAccelerated && width >= 32)
        {
            start = ExpandVector(words, row);
        }

        int whole = width >> 3;
        ref byte target = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(row);
        ref ulong table = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(GrayBytes);
        for (int i = start >> 3; i < whole; i++)
        {
            int bits = (int)(words[i >> 3] >> (56 - ((i & 7) << 3))) & 0xFF;
            System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref System.Runtime.CompilerServices.Unsafe.Add(ref target, i << 3), System.Runtime.CompilerServices.Unsafe.Add(ref table, bits));
        }

        for (int x = whole << 3; x < width; x++)
        {
            row[x] = (byte)(0 - (int)(((~words[x >> 6]) >> (63 - (x & 63))) & 1));
        }
    }

    /// <summary>
    /// Expands 32 pixels at a time with 256-bit vectors: each byte of output takes the byte of bits holding its pixel, keeps
    /// that pixel's bit, and becomes 0 where it is set (black) and 255 where it is clear. Returns the pixels done.
    /// </summary>
    private static int ExpandVector(ReadOnlySpan<ulong> words, Span<byte> row)
    {
        int width = row.Length;
        Vector256<byte> spread = Vector256.Create(
            (byte)3, 3, 3, 3, 3, 3, 3, 3, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0);
        Vector256<byte> bit = Vector256.Create(
            (byte)0x80, 0x40, 0x20, 0x10, 8, 4, 2, 1, 0x80, 0x40, 0x20, 0x10, 8, 4, 2, 1, 0x80, 0x40, 0x20, 0x10, 8, 4, 2, 1, 0x80, 0x40, 0x20, 0x10, 8, 4, 2, 1);
        ref byte target = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(row);
        int x = 0;
        for (; x + 32 <= width; x += 32)
        {
            // The 32 pixels from x, most significant first, as a big-endian uint: bytes 3 to 0 hold pixels 0-7 to 24-31.
            uint bits = (uint)(words[x >> 6] >> (32 - (x & 32)));
            Vector256<byte> broadcast = Vector256.Create(bits).AsByte();
            Vector256<byte> lanes = Vector256.Shuffle(broadcast, spread);
            Vector256<byte> set = Vector256.Equals(lanes & bit, bit);
            Vector256.StoreUnsafe(~set, ref target, (nuint)x);
        }

        return x;
    }

    private static ulong[] BuildGrayBytes()
    {
        ulong[] table = new ulong[256];
        for (int bits = 0; bits < 256; bits++)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++)
            {
                ulong gray = ((bits >> (7 - i)) & 1) != 0 ? 0UL : 255UL;
                value |= gray << (8 * i);
            }

            table[bits] = value;
        }

        return table;
    }

    private static void Fill(RasterMetadata metadata, Jbig2Decoder decoder, int page, Jbig2Page decoded, ReaderOptions options)
    {
        Jbig2PageInfo info = decoded.Info;
        Jbig2Details details = new()
        {
            PageIndex = decoder.PageSegments().IndexOf(page),
            Organisation = decoder.Stream.Organisation,
            UsesGlobals = decoder.UsesGlobals(page),
            Striped = info.Striped,
            MaxStripeSize = info.Striped ? info.MaxStripeSize : 0,
            DefaultPixel = info.DefaultPixel,
            CombinationOperator = (Jbig2CombinationOperator)info.DefaultCombine,
            Regions = decoded.Regions,
            Lossless = info.EventuallyLossless,
        };
        foreach (KeyValuePair<string, string> comment in decoded.Comments)
        {
            details.Comments.Add(comment);
        }

        metadata.FormatDetails = details;
        if (options.ReadMetadata && info.XResolution > 0 && info.YResolution > 0)
        {
            metadata.Resolution = new Resolution(info.XResolution, info.YResolution, ResolutionUnit.PerMeter);
        }
    }

    /// <summary>
    /// Reads the whole input, splits it into segments and runs <paramref name="action"/> on the page to load, mapping damage to
    /// <see cref="CorruptDataException"/>.
    /// </summary>
    private static T Run<T>(EgamiInput input, ReaderOptions options, Func<Jbig2Decoder, int, T> action, bool anyPage = false)
    {
        (byte[] data, int length, bool pooled) = InputBytes.ReadAll(input);
        try
        {
            (Jbig2Decoder decoder, int page) = Prepare(input, data, length, options, anyPage);
            return action(decoder, page);
        }
        catch (Exception e) when (e is InvalidDataException or Jbig2MissingSegmentException or EndOfStreamException)
        {
            throw Map(input, e);
        }
        finally
        {
            if (pooled)
            {
                ArrayPool<byte>.Shared.Return(data);
            }
        }
    }

    /// <summary>Splits the data into segments and finds the page to load: its page information segment, or -1 with <paramref name="anyPage"/>.</summary>
    private static (Jbig2Decoder Decoder, int Page) Prepare(EgamiInput input, byte[] data, int length, ReaderOptions options, bool anyPage = false)
    {
        bool named = options.Format?.Reader is Jbig2Reader;
        Jbig2Stream stream = Jbig2Stream.Parse(data, length, embedded: named, MaxSegments);
        Jbig2ReaderOptions? jbig2 = options as Jbig2ReaderOptions;
        Jbig2Decoder decoder = new(stream, jbig2?.Globals, new Jbig2Budget(input.Limits), options.Recovery == RecoveryMode.Strict);
        if (anyPage)
        {
            return (decoder, -1);
        }

        List<int> pages = decoder.PageSegments();
        if (pages.Count == 0)
        {
            throw new InvalidDataException("The JBIG2 data has no page information segment.");
        }

        int index = jbig2?.Page ?? 0;
        if (index < 0 || index >= pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(options), index, $"The JBIG2 data has {pages.Count} pages; page {index} does not exist.");
        }

        return (decoder, pages[index]);
    }

    /// <summary>Turns a decoder's damage exception into <see cref="CorruptDataException"/>.</summary>
    private static CorruptDataException Map(EgamiInput input, Exception e)
    {
        return e is EndOfStreamException
            ? new CorruptDataException(input.FormatName, -1, e.Message) { IsTruncation = true }
            : input.Corrupt(-1, e.Message);
    }
}
