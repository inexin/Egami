using System.Buffers.Binary;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Tiff;

/// <summary>
/// Reads TIFF and BigTIFF, written from the TIFF 6.0 specification, Adobe's technical notes and supplements and the BigTIFF design
/// note, starting from Lars's ClassicMac reader. Load reads one page (the first full-resolution page unless
/// <see cref="TiffReaderOptions.Page"/> says otherwise) into its natural pixel type, a strip or row of tiles at a time.
/// </summary>
internal sealed class TiffReader : IFormatReader, IRowSourceReader, IMultiImageReader
{
    internal static readonly TiffReader Instance = new();

    /// <summary>Gets the bytes the signature needs: a BigTIFF header is 16.</summary>
    internal const int SignatureLength = 16;

    /// <summary>
    /// Returns whether the data starts like TIFF: II or MM, then 42 and a first IFD offset of at least 8, or 43 (BigTIFF) with
    /// 8-byte offsets, a reserved 0 and a first IFD offset of at least 16. Whether the IFD lies inside the file is checked when
    /// reading, since a signature cannot know the length.
    /// </summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        if (header.Length < 8)
        {
            return false;
        }

        bool little = header[0] == 'I' && header[1] == 'I';
        bool big = header[0] == 'M' && header[1] == 'M';
        if (!little && !big)
        {
            return false;
        }

        int version = little ? BinaryPrimitives.ReadUInt16LittleEndian(header[2..]) : BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        if (version == 42)
        {
            uint first = little ? BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) : BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            return first >= 8;
        }

        if (version != 43 || header.Length < 16)
        {
            return false;
        }

        int offsetSize = little ? BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) : BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        int reserved = little ? BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) : BinaryPrimitives.ReadUInt16BigEndian(header[6..]);
        ulong offset = little ? BinaryPrimitives.ReadUInt64LittleEndian(header[8..]) : BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
        return offsetSize == 8 && reserved == 0 && offset >= 16;
    }

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        using TiffImage image = TiffImage.Open(input, options);
        (int index, int sub) = image.PageToLoad(options);
        TiffPage page = image.Page(index, sub);
        input.Limits.CheckFrameSize(page.Width, page.Height);
        RasterMetadata metadata = image.Metadata(page, options);
        bool hasMetadata = metadata.Exif is not null || !metadata.IccProfile.IsEmpty || !metadata.Xmp.IsEmpty || metadata.HasIptc;
        return new RasterInfo
        {
            Format = TiffFormat.Info,
            Width = page.Width,
            Height = page.Height,
            Layout = page.Layout,
            FrameCount = 1,
            ImageCount = image.Images(options).Count,
            HasMetadata = hasMetadata,
            EstimatedBytes = (long)page.Width * page.Height * page.Layout.BytesPerPixel,
            Orientation = metadata.Exif?.Orientation ?? ImageOrientation.Normal,
            Access = image.Source.Buffered ? RasterAccess.Buffered : RasterAccess.Streamed,
            WorkingBytes = TiffRows<Rgba8>.WorkingBytesOf(image, page),
        };
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using TiffImage image = TiffImage.Open(input, options);
        (int index, int sub) = image.PageToLoad(options);
        TiffPage page = image.Page(index, sub);
        return ReadPage<TPixel>(input, image, page, options);
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        using TiffImage image = TiffImage.Open(input, options);
        (int index, int sub) = image.PageToLoad(options);
        TiffPage page = image.Page(index, sub);
        return page.WithNaturalType(new NaturalRead(input, image, page, options));
    }

    /// <summary>Decodes a page into a new raster of <typeparamref name="TPixel"/>.</summary>
    internal static Raster<TPixel> ReadPage<TPixel>(EgamiInput input, TiffImage image, TiffPage page, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        input.Limits.CheckFrameSize(page.Width, page.Height);
        using RowSource rows = page.WithNaturalType(new OpenRows(input, image, page, options, ownsInput: false));
        Raster<TPixel> raster = input.CreateRaster<TPixel>(page.Width, page.Height);
        try
        {
            rows.NextFrame();
            for (int y = 0; y < page.Height; y++)
            {
                rows.ReadRow(y, 0, raster.Row(y));
            }

            rows.EndFrame();
            raster.Metadata.ReplaceWith(rows.Metadata);
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    public RowSource Open(EgamiInput input, ReaderOptions options)
    {
        TiffImage image = TiffImage.Open(input, options);
        try
        {
            (int index, int sub) = image.PageToLoad(options);
            TiffPage page = image.Page(index, sub);
            input.Limits.CheckFrameSize(page.Width, page.Height);
            if (sub < 0 && options.MinimumSize is not null && image.PyramidFor(page, options) is (TiffPage level, Pyramid pyramid))
            {
                return level.WithNaturalType(new OpenRows(input, image, level, options, ownsInput: true, pyramid));
            }

            return page.WithNaturalType(new OpenRows(input, image, page, options, ownsInput: true));
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    public IReadOnlyList<ReaderOptions> Images(EgamiInput input, ReaderOptions options)
    {
        using TiffImage image = TiffImage.Open(input, options);
        TiffReaderOptions tiff = options as TiffReaderOptions ?? new TiffReaderOptions
        {
            Recovery = options.Recovery,
            ReadMetadata = options.ReadMetadata,
            ConvertColorTo = options.ConvertColorTo,
            Frames = options.Frames,
            MinimumSize = options.MinimumSize,
            Region = options.Region,
        };
        return [.. image.Images(options).Select(d => tiff with { Directory = d })];
    }

    private sealed class OpenRows(EgamiInput input, TiffImage image, TiffPage page, ReaderOptions options, bool ownsInput, Pyramid? pyramid = null)
        : INaturalTypeVisitor<RowSource>
    {
        public RowSource Visit<TPixel>()
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            return new TiffRows<TPixel>(input, image, page, options, ownsInput, pyramid);
        }
    }

    private sealed class NaturalRead(EgamiInput input, TiffImage image, TiffPage page, ReaderOptions options) : INaturalTypeVisitor<Raster>
    {
        public Raster Visit<TPixel>()
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            return ReadPage<TPixel>(input, image, page, options);
        }
    }
}
