using System.Buffers;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jbig2;

/// <summary>
/// Reads JBIG2, written from ITU-T T.88 (2018) and the PDF specification's JBIG2Decode filter. Load reads one page (the first
/// unless <see cref="Jbig2ReaderOptions.Page"/> says otherwise) as <see cref="Gray8"/>, black 0 and white 255.
/// </summary>
internal sealed class Jbig2Reader : IFormatReader, IMultiImageReader
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
                Access = RasterAccess.Buffered,
                WorkingBytes = (((width + 63L) >> 6) * 8 * height) + ((long)width * height),
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
            foreach (string warning in decoded.Warnings)
            {
                input.Warn(raster, warning);
            }

            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>Expands a row of packed bits to gray bytes: a 1 bit (black) to 0, a 0 bit to 255.</summary>
    internal static void Expand(ReadOnlySpan<ulong> words, Span<byte> row)
    {
        int width = row.Length;
        for (int w = 0; w < words.Length; w++)
        {
            ulong word = ~words[w];
            int start = w << 6;
            int count = Math.Min(64, width - start);
            for (int i = 0; i < count; i++)
            {
                row[start + i] = (byte)(0 - (int)((word >> (63 - i)) & 1));
            }
        }
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
        (byte[] data, int length, bool pooled) = ReadAll(input);
        try
        {
            bool named = options.Format?.Reader is Jbig2Reader;
            Jbig2Stream stream = Jbig2Stream.Parse(data, length, embedded: named, MaxSegments);
            Jbig2ReaderOptions? jbig2 = options as Jbig2ReaderOptions;
            Jbig2Decoder decoder = new(stream, jbig2?.Globals, new Jbig2Budget(input.Limits), options.Recovery == RecoveryMode.Strict);
            List<int> pages = decoder.PageSegments();
            if (anyPage)
            {
                return action(decoder, -1);
            }

            if (pages.Count == 0)
            {
                throw new InvalidDataException("The JBIG2 data has no page information segment.");
            }

            int index = jbig2?.Page ?? 0;
            if (index < 0 || index >= pages.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(options), index, $"The JBIG2 data has {pages.Count} pages; page {index} does not exist.");
            }

            return action(decoder, pages[index]);
        }
        catch (Exception e) when (e is InvalidDataException or Jbig2MissingSegmentException)
        {
            throw input.Corrupt(-1, e.Message);
        }
        catch (EndOfStreamException e)
        {
            throw new CorruptDataException(input.FormatName, -1, e.Message) { IsTruncation = true };
        }
        finally
        {
            if (pooled)
            {
                ArrayPool<byte>.Shared.Return(data);
            }
        }
    }

    /// <summary>Reads the input to its end, counting the bytes against <see cref="SafetyLimits.MaxTotalBytes"/>.</summary>
    private static (byte[] Data, int Length, bool Pooled) ReadAll(EgamiInput input)
    {
        long max = Math.Min(input.Limits.MaxTotalBytes, Array.MaxLength);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        int length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (length >= max)
                    {
                        throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), input.Limits.MaxTotalBytes, length + 1L);
                    }

                    byte[] larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(max, (long)buffer.Length * 2));
                    buffer.AsSpan(0, length).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                int n = input.Read(buffer.AsSpan(length, (int)Math.Min(buffer.Length, max) - length));
                if (n == 0)
                {
                    return (buffer, length, true);
                }

                length += n;
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }
}
