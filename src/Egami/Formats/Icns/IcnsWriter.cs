using System.Buffers.Binary;

using Egami.Formats.Png;
using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Formats.Icns;

/// <summary>
/// Writes ICNS: the <c>icns</c> header, a <c>TOC </c> element listing the entries, then one PNG entry per size, written by Egami's
/// PNG writer as 8-bit RGBA and typed by its pixel size (icp4, icp5, icp6, ic07, ic08, ic09, ic10), smallest first. The same
/// rasters and options give the same bytes.
/// </summary>
internal sealed class IcnsWriter : IFormatWriter, IMultiImageWriter
{
    internal static readonly IcnsWriter Instance = new();

    private static readonly Dictionary<int, string> TypeOfSize = new()
    {
        [16] = "icp4",
        [32] = "icp5",
        [64] = "icp6",
        [128] = "ic07",
        [256] = "ic08",
        [512] = "ic09",
        [1024] = "ic10",
    };

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        WriteAll([raster], output, options);
    }

    public void WriteAll(IReadOnlyList<Raster> rasters, Stream output, WriterOptions options)
    {
        IcnsWriterOptions icns = options as IcnsWriterOptions ?? new IcnsWriterOptions { StripMetadata = options.StripMetadata, OmitSrgbProfile = options.OmitSrgbProfile };
        List<(int Size, Raster Source)> plan = Plan(rasters, icns);
        List<(string Type, byte[] Data)> entries = new(plan.Count);
        foreach ((int size, Raster source) in plan)
        {
            entries.Add((TypeOfSize[size], Encode(source, size, icns)));
        }

        int tocLength = 8 + (8 * entries.Count);
        long total = 8 + tocLength + entries.Sum(e => 8L + e.Data.Length);
        if (total > uint.MaxValue)
        {
            throw new ArgumentException("The icon family would be larger than ICNS's 32-bit length allows.", nameof(rasters));
        }

        byte[] head = new byte[8 + tocLength];
        "icns"u8.CopyTo(head);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), (uint)total);
        "TOC "u8.CopyTo(head.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12), (uint)tocLength);
        for (int i = 0; i < entries.Count; i++)
        {
            Span<byte> item = head.AsSpan(16 + (8 * i), 8);
            System.Text.Encoding.Latin1.GetBytes(entries[i].Type, item);
            BinaryPrimitives.WriteUInt32BigEndian(item[4..], (uint)(8 + entries[i].Data.Length));
        }

        output.Write(head);
        Span<byte> header = stackalloc byte[8];
        foreach ((string type, byte[] data) in entries)
        {
            System.Text.Encoding.Latin1.GetBytes(type, header);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)(8 + data.Length));
            output.Write(header);
            output.Write(data);
        }
    }

    /// <summary>Decides every entry's size and the raster it comes from, smallest first. Checks everything before any pixel work.</summary>
    private static List<(int Size, Raster Source)> Plan(IReadOnlyList<Raster> rasters, IcnsWriterOptions options)
    {
        List<(int Size, Raster Source)> plan = [];
        if (options.Sizes is IReadOnlyList<int> sizes)
        {
            Raster largest = rasters.MaxBy(r => (long)r.Width * r.Height)!;
            foreach (int size in sizes)
            {
                plan.Add((size, rasters.FirstOrDefault(r => r.Width == size && r.Height == size) ?? largest));
            }
        }
        else
        {
            foreach (Raster raster in rasters)
            {
                int fill = Math.Max(raster.Width, raster.Height);
                int size = IcnsWriterOptions.MacSizes.LastOrDefault(s => s <= fill, 16);
                if (size != raster.Width || size != raster.Height)
                {
                    SaveLosses.Report(raster.Context, "Save.IcnsFitted", $"ICNS: the {raster.Width}×{raster.Height} raster is not one of the entry sizes (16, 32, 64, 128, 256, 512 or 1024 square); it was fitted to {size}×{size}. Set IcnsWriterOptions.Sizes to choose the sizes.");
                }

                plan.Add((size, raster));
            }
        }

        plan.Sort((a, b) => a.Size.CompareTo(b.Size));
        for (int i = 1; i < plan.Count; i++)
        {
            if (plan[i].Size == plan[i - 1].Size)
            {
                throw new ArgumentException($"Two entries would be {plan[i].Size}×{plan[i].Size}; an icon family holds one entry of each size.", nameof(rasters));
            }
        }

        foreach (Raster raster in rasters.Distinct())
        {
            raster.Accept(new LossVisitor(options));
        }

        return plan;
    }

    /// <summary>Encodes one entry as an 8-bit RGBA PNG: the first frame, resized and centred on transparency when it is not that size.</summary>
    private static byte[] Encode(Raster source, int size, IcnsWriterOptions options)
    {
        Raster<Rgba8> rgba = source.As<Rgba8>();
        try
        {
            if (source.Width != size || source.Height != size)
            {
                double scale = (double)size / Math.Max(source.Width, source.Height);
                int width = Math.Clamp((int)Math.Round(source.Width * scale), 1, size);
                int height = Math.Clamp((int)Math.Round(source.Height * scale), 1, size);
                Raster<Rgba8> resized = rgba.Derive(ops => ops.Resize(width, height, options.Filter));
                rgba.Dispose();
                rgba = resized;
                if (width != size || height != size)
                {
                    Raster<Rgba8> padded = rgba.Derive(ops => ops.Pad(size, size, default, Anchor.Center));
                    rgba.Dispose();
                    rgba = padded;
                }
            }

            RasterMetadata metadata = rgba.Metadata;
            metadata.Exif = null;
            metadata.Xmp = default;
            metadata.Iptc = null;
            metadata.Text.Clear();
            using MemoryStream png = new();
            PngWriter.Instance.Write(rgba, png, new PngWriterOptions { ColorType = PngColorType.Rgba, BitDepth = 8, StripMetadata = options.StripMetadata, OmitSrgbProfile = options.OmitSrgbProfile });
            return png.ToArray();
        }
        finally
        {
            rgba.Dispose();
        }
    }

    /// <summary>Reports what the save drops: depth beyond 8 bits, frames after the first, EXIF, XMP, IPTC and text.</summary>
    private sealed class LossVisitor(IcnsWriterOptions options) : IRasterVisitor<bool>
    {
        public bool Visit<TPixel>(Raster<TPixel> raster)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            EncodeStart<TPixel> start = new(raster.Width, raster.Height, raster.Metadata, options, raster.Context, raster, seekable: false);
            SaveLosses.Report(start, "ICNS", SaveKeeps.Alpha | SaveKeeps.Icc | SaveKeeps.Resolution);
            return true;
        }
    }
}
