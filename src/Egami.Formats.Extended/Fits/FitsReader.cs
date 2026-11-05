using Egami.Pixels;

namespace Egami.Formats.Extended.Fits;

/// <summary>
/// Reads FITS images, written from the FITS Standard 4.0 and the tiled image compression convention: the primary HDU and every
/// <c>IMAGE</c> extension and tile-compressed image, each plane of a cube an image of its own. The values are read whole and
/// stretched, so the image is buffered; the first stored row is at the bottom, as astronomy viewers show it.
/// </summary>
internal sealed class FitsReader : ExtendedReader, IMultiImageReader
{
    public override RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        List<Page> pages = Pages(input, options, null);
        int index = (options as FitsReaderOptions)?.Image ?? 0;
        if (pages.Count == 0)
        {
            throw new UnsupportedFormatException("The FITS file holds no image Egami reads.");
        }

        Page page = index < pages.Count ? pages[index] : throw new ArgumentOutOfRangeException(nameof(options), Out(index, pages.Count));
        PixelLayoutInfo layout = page.Rgb ? RgbF32.Layout : GrayF32.Layout;
        return Describe(FitsFormat.Info, page.Hdu.Width, page.Hdu.Height, layout, RasterAccess.Buffered, WorkingBytes(page)) with
        {
            ImageCount = pages.Count,
            HasMetadata = true,
        };
    }

    public override RowReader Open(EgamiInput input, ReaderOptions options)
    {
        int index = (options as FitsReaderOptions)?.Image ?? 0;
        List<Page> pages = Pages(input, options, index);
        if (pages.Count <= index)
        {
            throw pages.Count == 0 ? new UnsupportedFormatException("The FITS file holds no image Egami reads.") : new ArgumentOutOfRangeException(nameof(options), Out(index, pages.Count));
        }

        Page page = pages[index];
        FitsHdu hdu = page.Hdu;
        input.CheckFrameSize(hdu.Width, hdu.Height);
        if (hdu.Kind == FitsHduKind.CompressedImage && !FitsTiles.Supports(hdu.Compression))
        {
            throw new UnsupportedFormatException($"FITS tile compression {hdu.Compression} is not one Egami reads; it reads RICE_1, GZIP_1, GZIP_2, PLIO_1 and NOCOMPRESS.");
        }

        long pixels = (long)hdu.Width * hdu.Height;
        int planes = page.Rgb ? 3 : 1;
        input.CheckMemory(WorkingBytes(page));
        float[] values = new float[pixels * planes];
        long rowsRead;
        int damagedTiles = 0;
        if (hdu.Kind == FitsHduKind.CompressedImage)
        {
            input.CheckMemory(hdu.DataLength + (pixels * hdu.Planes * 4));
            byte[] data = InputData.ReadToEnd(input, hdu.DataLength, out int length);
            float[] all = new float[pixels * hdu.Planes];
            damagedTiles = FitsTiles.Decode<float>(data.AsSpan(0, length), hdu, all);
            all.AsSpan((int)(pixels * page.Plane), values.Length).CopyTo(values);
            rowsRead = hdu.Height;
        }
        else
        {
            int width = Math.Abs(hdu.Bitpix) / 8;
            long planeBytes = pixels * width;
            Skip(input, planeBytes * page.Plane);
            byte[] raw = InputData.ReadToEnd(input, planeBytes * planes, out int length);
            long decoded = FitsData.Decode<float>(raw.AsSpan(0, length), hdu.Bitpix, hdu.Scale, hdu.Zero, hdu.Blank, values);
            rowsRead = page.Rgb ? Math.Max(0, decoded - (pixels * 2)) / hdu.Width : decoded / hdu.Width;
        }

        FitsStretch stretch = (options as FitsReaderOptions)?.Stretch ?? FitsStretch.MinMax;
        (double low, double high) = FitsData.Limits(stretch, values, hdu.Header);
        FitsDetails details = new()
        {
            Header = hdu.Header,
            Hdu = hdu.Index,
            Plane = page.Plane,
            Bitpix = hdu.Bitpix,
            Compression = hdu.Compression,
            Stretch = stretch,
            Low = low,
            High = high,
        };
        Source source = new(values, hdu.Width, hdu.Height, (int)Math.Min(rowsRead, hdu.Height), damagedTiles, stretch, low, high, details);
        return page.Rgb ? new Rows<RgbF32>(input, options, source) : new Rows<GrayF32>(input, options, source);
    }

    public IReadOnlyList<ReaderOptions> Images(EgamiInput input, ReaderOptions options)
    {
        List<Page> pages = Pages(input, options, null);
        FitsReaderOptions fits = options as FitsReaderOptions ?? new FitsReaderOptions
        {
            Recovery = options.Recovery,
            ReadMetadata = options.ReadMetadata,
            ConvertColorTo = options.ConvertColorTo,
            Frames = options.Frames,
            MinimumSize = options.MinimumSize,
            Region = options.Region,
        };
        return [.. Enumerable.Range(0, pages.Count).Select(i => fits with { Image = i })];
    }

    private static long WorkingBytes(Page page) => (long)page.Hdu.Width * page.Hdu.Height * 4 * (page.Rgb ? 3 : 1) * 2;

    private static string Out(int index, int count) => $"Image {index} was asked for; the FITS file has {count}.";

    /// <summary>
    /// Lists the images of the file in order, reading headers and skipping data. With <paramref name="stopAt"/>, it stops once
    /// that image is found, leaving the input at the start of its HDU's data.
    /// </summary>
    private static List<Page> Pages(EgamiInput input, ReaderOptions options, int? stopAt)
    {
        bool rgb = options is FitsReaderOptions { Rgb: true };
        List<Page> pages = [];
        for (int index = 0; ; index++)
        {
            FitsHdu? hdu = FitsHeaderReader.Read(input, index);
            if (hdu is null)
            {
                return pages;
            }

            if (hdu.IsImage)
            {
                if (rgb && hdu.Axes.Count == 3 && hdu.Axes[2] == 3)
                {
                    pages.Add(new Page(hdu, 0, true));
                }
                else
                {
                    for (long p = 0; p < hdu.Planes && pages.Count < 100000; p++)
                    {
                        pages.Add(new Page(hdu, (int)p, false));
                    }
                }

                if (stopAt is int stop && pages.Count > stop)
                {
                    return pages;
                }
            }

            if (!Skip(input, hdu.PaddedLength))
            {
                return pages;
            }
        }
    }

    /// <summary>Skips data; returns false when the input ends first.</summary>
    private static bool Skip(EgamiInput input, long count)
    {
        try
        {
            input.Skip(count);
            return true;
        }
        catch (CorruptDataException)
        {
            return false;
        }
    }

    private sealed record Page(FitsHdu Hdu, int Plane, bool Rgb);

    /// <summary>The decoded values of one image and how they become the picture.</summary>
    private sealed record Source(float[] Values, int Width, int Height, int RowsRead, int DamagedTiles, FitsStretch Stretch, double Low, double High, FitsDetails Details);

    private sealed class Rows<TPixel> : ExtendedRows<TPixel, TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly Source _source;
        private readonly float[] _row;
        private bool _missingNoted;

        public Rows(EgamiInput input, ReaderOptions options, Source source)
            : base(input, options, FitsFormat.Info, "Fits", source.Width, source.Height)
        {
            _source = source;
            _row = new float[source.Width];
            Metadata.FormatDetails = source.Details;
            if (source.DamagedTiles > 0)
            {
                Damaged("TilesDamaged", $"{source.DamagedTiles} compressed tiles did not decode; their pixels are missing.");
            }
        }

        protected override bool DecodeRow(int y, Span<TPixel> row)
        {
            // The first stored row is the bottom of the picture.
            int fileRow = Height - 1 - y;
            Span<float> samples = System.Runtime.InteropServices.MemoryMarshal.Cast<TPixel, float>(row);
            if (fileRow >= _source.RowsRead)
            {
                if (Strict)
                {
                    throw Input.Corrupt(Input.Position, "The data ends unexpectedly.");
                }

                if (!_missingNoted)
                {
                    _missingNoted = true;
                    Warn("RowsMissing", "The file ends before the data of some rows; they are left black.");
                }

                samples.Clear();
                return true;
            }

            long pixels = (long)Width * Height;
            int planes = samples.Length / Width;
            for (int c = 0; c < planes; c++)
            {
                ReadOnlySpan<float> values = _source.Values.AsSpan((int)((c * pixels) + ((long)fileRow * Width)), Width);
                FitsData.Stretch(_source.Stretch, _source.Low, _source.High, values, _row);
                for (int x = 0; x < Width; x++)
                {
                    samples[(x * planes) + c] = _row[x];
                }
            }

            return true;
        }
    }
}
