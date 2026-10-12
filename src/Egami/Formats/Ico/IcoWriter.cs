using System.Buffers.Binary;

using Egami.Formats.Png;
using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Formats.Ico;

/// <summary>
/// Writes ICO and CUR: the directory, then each entry's data, as PNG files written by Egami's PNG writer or as 32-bit BMPs with
/// an AND mask built from alpha. Entries are 8-bit RGBA, sorted smallest first. The same rasters and options give the same bytes.
/// </summary>
internal sealed class IcoWriter : IFormatWriter, IMultiImageWriter
{
    internal static readonly IcoWriter Icon = new(IcoDirectory.IconType);
    internal static readonly IcoWriter Cursor = new(IcoDirectory.CursorType);

    private readonly int _type;

    private IcoWriter(int type)
    {
        _type = type;
    }

    private string Name => _type == IcoDirectory.IconType ? "ICO" : "CUR";

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        WriteAll([raster], output, options);
    }

    public void WriteAll(IReadOnlyList<Raster> rasters, Stream output, WriterOptions options)
    {
        IcoWriterOptions ico = options as IcoWriterOptions ?? new IcoWriterOptions { StripMetadata = options.StripMetadata, OmitSrgbProfile = options.OmitSrgbProfile };
        List<Planned> plan = Plan(rasters, ico);
        List<(Planned Entry, byte[] Data)> entries = new(plan.Count);
        try
        {
            foreach (Planned entry in plan)
            {
                entries.Add((entry, Encode(entry, ico)));
            }
        }
        finally
        {
            foreach (Planned entry in plan)
            {
                entry.Dispose();
            }
        }

        int count = entries.Count;
        byte[] directory = new byte[IcoDirectory.HeaderLength + (IcoDirectory.EntryLength * count)];
        BinaryPrimitives.WriteUInt16LittleEndian(directory.AsSpan(2), (ushort)_type);
        BinaryPrimitives.WriteUInt16LittleEndian(directory.AsSpan(4), (ushort)count);
        long offset = directory.Length;
        for (int i = 0; i < count; i++)
        {
            (Planned entry, byte[] data) = entries[i];
            Span<byte> d = directory.AsSpan(IcoDirectory.HeaderLength + (IcoDirectory.EntryLength * i), IcoDirectory.EntryLength);
            d[0] = (byte)(entry.Width == 256 ? 0 : entry.Width);
            d[1] = (byte)(entry.Height == 256 ? 0 : entry.Height);
            if (_type == IcoDirectory.CursorType)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(d[4..], (ushort)entry.Hotspot.X);
                BinaryPrimitives.WriteUInt16LittleEndian(d[6..], (ushort)entry.Hotspot.Y);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(d[4..], 1);
                BinaryPrimitives.WriteUInt16LittleEndian(d[6..], 32);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(d[8..], (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(d[12..], checked((uint)offset));
            offset += data.Length;
        }

        output.Write(directory);
        foreach ((Planned _, byte[] data) in entries)
        {
            output.Write(data);
        }
    }

    /// <summary>
    /// Decides every entry: its size, the raster it comes from and how it is resized, its encoding and hotspot, in the order they are
    /// written. Checks everything before any pixel work.
    /// </summary>
    private List<Planned> Plan(IReadOnlyList<Raster> rasters, IcoWriterOptions options)
    {
        List<Planned> plan = [];
        if (options.Sizes is IReadOnlyList<int> sizes)
        {
            Raster largest = rasters.MaxBy(r => (long)r.Width * r.Height)!;
            foreach (int size in sizes)
            {
                Raster source = rasters.FirstOrDefault(r => r.Width == size && r.Height == size) ?? largest;
                plan.Add(Entry(source, size, size, square: true, options));
            }
        }
        else
        {
            foreach (Raster raster in rasters)
            {
                int width = raster.Width;
                int height = raster.Height;
                if (width > 256 || height > 256)
                {
                    double scale = 256.0 / Math.Max(width, height);
                    (width, height) = (Math.Clamp((int)Math.Round(width * scale), 1, 256), Math.Clamp((int)Math.Round(height * scale), 1, 256));
                    SaveLosses.Report(raster.Context, "Save.IcoFitted", $"{Name}: the {raster.Width}×{raster.Height} raster is larger than an icon entry's 256 × 256; it was fitted to {width}×{height}. Set IcoWriterOptions.Sizes to choose the sizes.");
                }

                plan.Add(Entry(raster, width, height, square: false, options));
            }
        }

        plan.Sort((a, b) => a.Width != b.Width ? a.Width.CompareTo(b.Width) : a.Height.CompareTo(b.Height));
        for (int i = 1; i < plan.Count; i++)
        {
            if (plan[i].Width == plan[i - 1].Width && plan[i].Height == plan[i - 1].Height)
            {
                throw new ArgumentException($"Two entries would be {plan[i].Width}×{plan[i].Height}; an icon holds one entry of each size.", nameof(rasters));
            }
        }

        foreach (Raster raster in rasters.Distinct())
        {
            raster.Accept(new LossVisitor(Name, options, plan.Any(p => p.Encoding == IcoEntryEncoding.Png)));
        }

        return plan;
    }

    private Planned Entry(Raster source, int width, int height, bool square, IcoWriterOptions options)
    {
        // The image's own size inside the entry: the whole entry, or fitted inside a square entry and centred.
        int innerWidth = width;
        int innerHeight = height;
        if (square && source.Width != source.Height)
        {
            double scale = (double)width / Math.Max(source.Width, source.Height);
            innerWidth = Math.Clamp((int)Math.Round(source.Width * scale), 1, width);
            innerHeight = Math.Clamp((int)Math.Round(source.Height * scale), 1, height);
        }

        IcoDetails? details = source.Metadata.FormatDetails as IcoDetails;
        bool ownSize = width == source.Width && height == source.Height;
        IcoEntryEncoding encoding = options.Encoding switch
        {
            IcoEntryEncoding.Png => IcoEntryEncoding.Png,
            IcoEntryEncoding.Bmp => IcoEntryEncoding.Bmp,
            null when ownSize && details is not null => details.Encoding,
            _ => width == 256 && height == 256 ? IcoEntryEncoding.Png : IcoEntryEncoding.Bmp,
        };

        Point hotspot = default;
        if (_type == IcoDirectory.CursorType)
        {
            Point at = options.Hotspot ?? details?.Hotspot ?? default;
            int left = (width - innerWidth) / 2;
            int top = (height - innerHeight) / 2;
            int x = left + (int)Math.Floor(at.X * (double)innerWidth / source.Width);
            int y = top + (int)Math.Floor(at.Y * (double)innerHeight / source.Height);
            hotspot = new Point(Math.Clamp(x, 0, width - 1), Math.Clamp(y, 0, height - 1));
        }

        return new Planned(source, width, height, innerWidth, innerHeight, encoding, hotspot, options.Filter);
    }

    /// <summary>Encodes one entry's data: a PNG file, or a BITMAPINFOHEADER, bottom-up BGRA rows and the AND mask.</summary>
    private static byte[] Encode(Planned entry, IcoWriterOptions options)
    {
        Raster<Rgba8> pixels = entry.Pixels();
        if (entry.Encoding == IcoEntryEncoding.Png)
        {
            using MemoryStream png = new();
            // Always 8-bit RGBA: Windows' icon decoder skips PNG entries of other color types.
            PngWriter.Instance.Write(pixels, png, new PngWriterOptions { ColorType = PngColorType.Rgba, BitDepth = 8, StripMetadata = options.StripMetadata, OmitSrgbProfile = options.OmitSrgbProfile });
            return png.ToArray();
        }

        int width = pixels.Width;
        int height = pixels.Height;
        int colorStride = width * 4;
        int maskStride = (width + 31) / 32 * 4;
        byte[] data = new byte[40 + ((colorStride + maskStride) * height)];
        Span<byte> header = data.AsSpan(0, 40);
        BinaryPrimitives.WriteUInt32LittleEndian(header, 40);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)(data.Length - 40));
        int maskStart = 40 + (colorStride * height);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<Rgba8> row = pixels.Row(height - 1 - y);
            Span<byte> color = data.AsSpan(40 + (y * colorStride), colorStride);
            Span<byte> mask = data.AsSpan(maskStart + (y * maskStride), maskStride);
            for (int x = 0; x < width; x++)
            {
                Rgba8 p = row[x];
                color[4 * x] = p.B;
                color[(4 * x) + 1] = p.G;
                color[(4 * x) + 2] = p.R;
                color[(4 * x) + 3] = p.A;
                if (p.A == 0)
                {
                    mask[x >> 3] |= (byte)(0x80 >> (x & 7));
                }
            }
        }

        return data;
    }

    /// <summary>Reports what the save drops: depth beyond 8 bits, frames after the first, EXIF, XMP, IPTC and text.</summary>
    private sealed class LossVisitor(string format, IcoWriterOptions options, bool anyPng) : IRasterVisitor<bool>
    {
        public bool Visit<TPixel>(Raster<TPixel> raster)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            EncodeStart<TPixel> start = new(raster.Width, raster.Height, raster.Metadata, options, raster.Context, raster, seekable: false);
            SaveLosses.Report(start, format, SaveKeeps.Alpha | (anyPng ? SaveKeeps.Icc | SaveKeeps.Resolution : SaveKeeps.None));
            return true;
        }
    }

    /// <summary>One entry to write, its pixels made on first use.</summary>
    private sealed class Planned(Raster source, int width, int height, int innerWidth, int innerHeight, IcoEntryEncoding encoding, Point hotspot, ResampleFilter filter) : IDisposable
    {
        private Raster<Rgba8>? _pixels;

        public int Width => width;

        public int Height => height;

        public IcoEntryEncoding Encoding => encoding;

        public Point Hotspot => hotspot;

        /// <summary>Returns the entry's 8-bit RGBA pixels: the first frame, resized from the source and centred on transparency as planned.</summary>
        public Raster<Rgba8> Pixels()
        {
            if (_pixels is not null)
            {
                return _pixels;
            }

            Raster<Rgba8> rgba = source.As<Rgba8>();
            try
            {
                if (innerWidth != source.Width || innerHeight != source.Height)
                {
                    Raster<Rgba8> resized = rgba.Derive(ops => ops.Resize(innerWidth, innerHeight, filter));
                    rgba.Dispose();
                    rgba = resized;
                }

                if (innerWidth != width || innerHeight != height)
                {
                    Raster<Rgba8> padded = rgba.Derive(ops => ops.Pad(width, height, default, Anchor.Center));
                    rgba.Dispose();
                    rgba = padded;
                }

                RasterMetadata metadata = rgba.Metadata;
                metadata.Exif = null;
                metadata.Xmp = default;
                metadata.Iptc = null;
                metadata.Text.Clear();
                _pixels = rgba;
                return rgba;
            }
            catch
            {
                rgba.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _pixels?.Dispose();
            _pixels = null;
        }
    }
}
