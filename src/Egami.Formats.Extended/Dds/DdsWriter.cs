using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Formats.Extended.Dds.Blocks;
using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Formats.Extended.Dds;

/// <summary>
/// Writes DDS from Microsoft's DDS programming guide: the legacy header whenever the format, sRGB flag, alpha mode and layout
/// allow it, else the DX10 header; every raster's mips after it, made with <see cref="MipChain"/>. Block formats encode rows of
/// blocks in parallel; each block depends only on its own pixels, so the bytes never depend on the thread count. A texture is
/// written whole, so the writer has no row encoder: Save and a streamed encode gather the image first.
/// </summary>
internal sealed class DdsWriter : IFormatWriter, IMultiImageWriter
{
    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(raster);
        WriteAll([raster], output, options);
    }

    public void WriteAll(IReadOnlyList<Raster> rasters, Stream output, WriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(rasters);
        ArgumentNullException.ThrowIfNull(output);
        DdsWriterOptions dds = options as DdsWriterOptions ?? DdsWriterOptions.Default;
        Raster first = rasters[0];
        foreach (Raster raster in rasters)
        {
            if (raster.Width != first.Width || raster.Height != first.Height)
            {
                throw new ArgumentException($"Every surface of a DDS texture has the same size; {raster.Width}×{raster.Height} differs from {first.Width}×{first.Height}.", nameof(rasters));
            }
        }

        if (dds.Cube && (rasters.Count % 6 != 0 || first.Width != first.Height))
        {
            throw new ArgumentException($"A cube map needs square faces, six a cube; {rasters.Count} rasters of {first.Width}×{first.Height} were given.", nameof(rasters));
        }

        DdsDetails? source = first.Metadata.FormatDetails as DdsDetails;
        Target target = Target.For(dds.Format ?? FromSource(source) ?? FromPixels(first.Layout));
        bool srgb = dds.Srgb ?? source?.IsSrgb ?? false;
        if (srgb && target.SrgbDxgi == 0)
        {
            if (dds.Srgb == true)
            {
                throw new ArgumentException($"DDS: {target.Format} has no sRGB form.", nameof(options));
            }

            srgb = false;
        }

        int dxgi = srgb ? target.SrgbDxgi : target.Dxgi;
        int full = 1 + (int)Math.Log2(Math.Max(first.Width, first.Height));
        int mips = dds.MipLevels == 0 ? full : Math.Min(dds.MipLevels, full);
        bool premultiplied = dds.PremultipliedAlpha;
        StoredMips?[] stored = [.. rasters.Select(r => (r.Metadata.FormatDetails as DdsDetails)?.Mips)];
        if (!premultiplied && stored.All(s => s is { Bytes: not null } && s.Dxgi == dxgi && s.Size == new Size(first.Width, first.Height))
            && stored.Select(s => s!.Count).Distinct().Count() == 1 && (dds.MipLevels == 0 || dds.MipLevels == stored[0]!.Count + 1))
        {
            // Unchanged since a load at mip 0: rewrite the file's own mips.
            mips = stored[0]!.Count + 1;
        }
        else
        {
            stored = new StoredMips?[rasters.Count];
        }

        int cubes = dds.Cube ? rasters.Count / 6 : 0;
        int arraySize = dds.Cube ? cubes : rasters.Count;
        bool dx10 = dds.Dx10Header || srgb || target.Legacy is null || arraySize > 1 || (premultiplied && target.Format is not (DdsTextureFormat.Bc2 or DdsTextureFormat.Bc3));
        Span<byte> header = stackalloc byte[DdsHeader.Length + DdsHeader.Dx10Length];
        int headerLength = Header(header, first.Width, first.Height, mips, target, dxgi, dx10, dds.Cube, arraySize, premultiplied);
        output.Write(header[..headerLength]);
        for (int i = 0; i < rasters.Count; i++)
        {
            Raster raster = rasters[i];
            if (stored[i] is { Bytes: byte[] bytes })
            {
                output.Write(Encode(raster, target, dds.Effort, premultiplied));
                output.Write(bytes);
                continue;
            }

            if (mips == 1)
            {
                output.Write(Encode(raster, target, dds.Effort, premultiplied));
                continue;
            }

            using MipChain chain = MipChain.Build(raster, srgb ? ColorSpace.Linear : ColorSpace.Encoded);
            for (int m = 0; m < mips; m++)
            {
                // A DDS mip rounds its size down; the chain's halving rounds up, so an odd size takes a box resize from it.
                int w = Math.Max(1, raster.Width >> m);
                int h = Math.Max(1, raster.Height >> m);
                Raster level = chain.Levels[m];
                if (level.Width == w && level.Height == h)
                {
                    output.Write(Encode(level, target, dds.Effort, premultiplied));
                }
                else
                {
                    using Raster resized = chain.Resize(w, h, ResampleFilter.Box);
                    output.Write(Encode(resized, target, dds.Effort, premultiplied));
                }
            }
        }
    }

    /// <summary>Returns a DDS source's format when the writer can write it.</summary>
    private static DdsTextureFormat? FromSource(DdsDetails? details)
    {
        if (details is null || details.FormatName == "A8L8")
        {
            return null;
        }

        return details.DxgiFormat switch
        {
            27 or 28 or 29 => DdsTextureFormat.Rgba8,
            87 or 90 or 91 => DdsTextureFormat.Bgra8,
            88 or 92 or 93 => DdsTextureFormat.Bgrx8,
            60 or 61 => DdsTextureFormat.R8,
            48 or 49 => DdsTextureFormat.Rg8,
            65 => DdsTextureFormat.A8,
            53 or 56 => DdsTextureFormat.R16,
            33 or 35 => DdsTextureFormat.Rg16,
            9 or 11 => DdsTextureFormat.Rgba16,
            10 => DdsTextureFormat.RgbaF16,
            39 or 41 => DdsTextureFormat.R32F,
            5 or 6 => DdsTextureFormat.RgbF32,
            1 or 2 => DdsTextureFormat.RgbaF32,
            23 or 24 => DdsTextureFormat.Rgb10A2,
            26 => DdsTextureFormat.R11G11B10F,
            67 => DdsTextureFormat.Rgb9E5,
            85 => DdsTextureFormat.Bgr565,
            86 => DdsTextureFormat.Bgra5551,
            115 => DdsTextureFormat.Bgra4444,
            70 or 71 or 72 => DdsTextureFormat.Bc1,
            73 or 74 or 75 => DdsTextureFormat.Bc2,
            76 or 77 or 78 => DdsTextureFormat.Bc3,
            79 or 80 => DdsTextureFormat.Bc4,
            82 or 83 => DdsTextureFormat.Bc5,
            _ => null,
        };
    }

    /// <summary>Returns the format nearest a pixel type.</summary>
    private static DdsTextureFormat FromPixels(PixelLayoutInfo layout)
    {
        if (layout == Rgb10A2.Layout)
        {
            return DdsTextureFormat.Rgb10A2;
        }

        if (layout == Bgr565.Layout)
        {
            return DdsTextureFormat.Bgr565;
        }

        if (layout == Bgra5551.Layout)
        {
            return DdsTextureFormat.Bgra5551;
        }

        if (layout == Bgra4444.Layout)
        {
            return DdsTextureFormat.Bgra4444;
        }

        return (layout.ChannelOrder, layout.ChannelType) switch
        {
            (PixelChannelOrder.Gray, PixelChannelType.Unorm8) => DdsTextureFormat.R8,
            (PixelChannelOrder.Gray, PixelChannelType.Unorm16) => DdsTextureFormat.R16,
            (PixelChannelOrder.Gray, PixelChannelType.Float32 or PixelChannelType.Float16) => DdsTextureFormat.R32F,
            (PixelChannelOrder.Alpha, _) => DdsTextureFormat.A8,
            (_, PixelChannelType.Unorm16) => DdsTextureFormat.Rgba16,
            (_, PixelChannelType.Float16) => DdsTextureFormat.RgbaF16,
            (PixelChannelOrder.Rgb or PixelChannelOrder.Bgr, PixelChannelType.Float32) => DdsTextureFormat.RgbF32,
            (_, PixelChannelType.Float32) => DdsTextureFormat.RgbaF32,
            _ => DdsTextureFormat.Rgba8,
        };
    }

    /// <summary>Writes the header and returns its length.</summary>
    private static int Header(Span<byte> h, int width, int height, int mips, Target target, int dxgi, bool dx10, bool cube, int arraySize, bool premultiplied)
    {
        h.Clear();
        long pitch = target.IsBlock ? Math.Max(1, (width + 3) / 4) * (long)target.BlockBytes * Math.Max(1, (height + 3) / 4) : (long)width * target.PixelBytes;
        uint flags = 0x1007 | (mips > 1 ? 0x20000u : 0) | (target.IsBlock ? 0x80000u : 0x8u);
        BinaryPrimitives.WriteUInt32LittleEndian(h, DdsHeader.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 124);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)Math.Min(pitch, uint.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)mips);
        BinaryPrimitives.WriteUInt32LittleEndian(h[76..], 32);
        uint caps = 0x1000 | (mips > 1 || cube ? 0x8u : 0) | (mips > 1 ? 0x400000u : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h[108..], caps);
        BinaryPrimitives.WriteUInt32LittleEndian(h[112..], cube ? 0xFE00u : 0);
        if (dx10)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[80..], 0x4);
            BinaryPrimitives.WriteUInt32LittleEndian(h[84..], 0x30315844);
            BinaryPrimitives.WriteUInt32LittleEndian(h[128..], (uint)dxgi);
            BinaryPrimitives.WriteUInt32LittleEndian(h[132..], 3);
            BinaryPrimitives.WriteUInt32LittleEndian(h[136..], cube ? 4u : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(h[140..], (uint)arraySize);
            BinaryPrimitives.WriteUInt32LittleEndian(h[144..], premultiplied ? 2u : 0);
            return DdsHeader.Length + DdsHeader.Dx10Length;
        }

        Legacy legacy = target.Legacy!.Value;
        uint fourCC = legacy.FourCC;
        if (premultiplied)
        {
            fourCC = target.Format == DdsTextureFormat.Bc2 ? 0x32545844u : 0x34545844u; // "DXT2", "DXT4"
        }

        BinaryPrimitives.WriteUInt32LittleEndian(h[80..], legacy.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(h[84..], fourCC);
        BinaryPrimitives.WriteUInt32LittleEndian(h[88..], legacy.Bits);
        BinaryPrimitives.WriteUInt32LittleEndian(h[92..], legacy.R);
        BinaryPrimitives.WriteUInt32LittleEndian(h[96..], legacy.G);
        BinaryPrimitives.WriteUInt32LittleEndian(h[100..], legacy.B);
        BinaryPrimitives.WriteUInt32LittleEndian(h[104..], legacy.A);
        return DdsHeader.Length;
    }

    /// <summary>Encodes one surface in the target format.</summary>
    private static byte[] Encode(Raster level, Target target, int effort, bool premultiplied)
    {
        int width = level.Width;
        int height = level.Height;
        byte[] work = Work(level, target.Work);
        if (premultiplied)
        {
            Premultiply(work, target.Work);
        }

        int pixel = DdsDecode.NaturalBytes(target.Work);
        if (!target.IsBlock)
        {
            byte[] stored = new byte[(long)width * height * target.PixelBytes];
            Pack(target.Format, work, stored);
            return stored;
        }

        int blocksWide = (width + 3) / 4;
        int blocksHigh = (height + 3) / 4;
        byte[] blocks = new byte[(long)blocksWide * blocksHigh * target.BlockBytes];
        ParallelOptions parallel = new() { MaxDegreeOfParallelism = Math.Max(1, level.Context.MaxParallelism) };
        Parallel.For(0, blocksHigh, parallel, by =>
        {
            Span<byte> gathered = stackalloc byte[16 * 4];
            for (int bx = 0; bx < blocksWide; bx++)
            {
                for (int i = 0; i < 16; i++)
                {
                    int x = Math.Min((bx * 4) + (i & 3), width - 1);
                    int y = Math.Min((by * 4) + (i >> 2), height - 1);
                    work.AsSpan(((y * width) + x) * pixel, pixel).CopyTo(gathered[(i * pixel)..]);
                }

                Span<byte> block = blocks.AsSpan(((by * blocksWide) + bx) * target.BlockBytes, target.BlockBytes);
                switch (target.Format)
                {
                    case DdsTextureFormat.Bc1:
                        Bc1To5Encoder.EncodeBc1(gathered, block, transparent: true, effort);
                        break;
                    case DdsTextureFormat.Bc2:
                        Bc1To5Encoder.EncodeBc2(gathered, block, effort);
                        break;
                    case DdsTextureFormat.Bc3:
                        Bc1To5Encoder.EncodeBc3(gathered, block, effort);
                        break;
                    case DdsTextureFormat.Bc4:
                        Bc1To5Encoder.EncodeUnorm(gathered, 1, block, effort);
                        break;
                    default:
                        Bc1To5Encoder.EncodeUnorm(gathered, 3, block, effort);
                        Bc1To5Encoder.EncodeUnorm(gathered[1..], 3, block[8..], effort);
                        break;
                }
            }
        });
        return blocks;
    }

    /// <summary>Converts a raster into the working pixel type's memory, rows packed.</summary>
    private static byte[] Work(Raster level, DdsNatural natural)
    {
        return natural switch
        {
            DdsNatural.Gray8 => Bytes(level.As<Gray8>()),
            DdsNatural.Gray16 => Bytes(level.As<Gray16>()),
            DdsNatural.GrayF32 => Bytes(level.As<GrayF32>()),
            DdsNatural.Alpha8 => Bytes(level.As<Alpha8>()),
            DdsNatural.Rgb8 => Bytes(level.As<Rgb8>()),
            DdsNatural.Rgb16 => Bytes(level.As<Rgb16>()),
            DdsNatural.RgbF32 => Bytes(level.As<RgbF32>()),
            DdsNatural.Rgba16 => Bytes(level.As<Rgba16>()),
            DdsNatural.RgbaF16 => Bytes(level.As<RgbaF16>()),
            DdsNatural.RgbaF32 => Bytes(level.As<RgbaF32>()),
            DdsNatural.Rgb10A2 => Bytes(level.As<Rgb10A2>()),
            DdsNatural.Bgr565 => Bytes(level.As<Bgr565>()),
            DdsNatural.Bgra5551 => Bytes(level.As<Bgra5551>()),
            DdsNatural.Bgra4444 => Bytes(level.As<Bgra4444>()),
            _ => Bytes(level.As<Rgba8>()),
        };
    }

    private static byte[] Bytes<TPixel>(Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using (raster)
        {
            int row = raster.Width * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>();
            byte[] bytes = new byte[(long)row * raster.Height];
            for (int y = 0; y < raster.Height; y++)
            {
                MemoryMarshal.AsBytes(raster.Row(y)).CopyTo(bytes.AsSpan(y * row, row));
            }

            return bytes;
        }
    }

    private static void Premultiply(byte[] work, DdsNatural natural)
    {
        switch (natural)
        {
            case DdsNatural.Rgba8:
                for (int i = 0; i < work.Length; i += 4)
                {
                    int a = work[i + 3];
                    for (int c = 0; c < 3; c++)
                    {
                        work[i + c] = (byte)(((work[i + c] * a) + 127) / 255);
                    }
                }

                break;
            case DdsNatural.Rgba16:
                {
                    Span<ushort> p = MemoryMarshal.Cast<byte, ushort>(work.AsSpan());
                    for (int i = 0; i < p.Length; i += 4)
                    {
                        long a = p[i + 3];
                        for (int c = 0; c < 3; c++)
                        {
                            p[i + c] = (ushort)(((p[i + c] * a) + 32767) / 65535);
                        }
                    }

                    break;
                }

            case DdsNatural.RgbaF16:
                {
                    Span<Half> p = MemoryMarshal.Cast<byte, Half>(work.AsSpan());
                    for (int i = 0; i < p.Length; i += 4)
                    {
                        float a = (float)p[i + 3];
                        for (int c = 0; c < 3; c++)
                        {
                            p[i + c] = (Half)((float)p[i + c] * a);
                        }
                    }

                    break;
                }

            case DdsNatural.RgbaF32:
                {
                    Span<float> p = MemoryMarshal.Cast<byte, float>(work.AsSpan());
                    for (int i = 0; i < p.Length; i += 4)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            p[i + c] *= p[i + 3];
                        }
                    }

                    break;
                }
        }
    }

    /// <summary>Turns working pixels into stored ones.</summary>
    private static void Pack(DdsTextureFormat format, byte[] work, byte[] stored)
    {
        switch (format)
        {
            case DdsTextureFormat.Bgra8:
            case DdsTextureFormat.Bgrx8:
                for (int i = 0; i < work.Length; i += 4)
                {
                    stored[i] = work[i + 2];
                    stored[i + 1] = work[i + 1];
                    stored[i + 2] = work[i];
                    stored[i + 3] = format == DdsTextureFormat.Bgrx8 ? (byte)255 : work[i + 3];
                }

                break;
            case DdsTextureFormat.Rg8:
                for (int i = 0, o = 0; i < work.Length; i += 3, o += 2)
                {
                    stored[o] = work[i];
                    stored[o + 1] = work[i + 1];
                }

                break;
            case DdsTextureFormat.Rg16:
                for (int i = 0, o = 0; i < work.Length; i += 6, o += 4)
                {
                    work.AsSpan(i, 4).CopyTo(stored.AsSpan(o));
                }

                break;
            case DdsTextureFormat.R11G11B10F:
            case DdsTextureFormat.Rgb9E5:
                {
                    ReadOnlySpan<float> p = MemoryMarshal.Cast<byte, float>(work.AsSpan());
                    for (int i = 0, o = 0; i < p.Length; i += 4, o += 4)
                    {
                        uint v = format == DdsTextureFormat.R11G11B10F ? PackR11G11B10(p[i], p[i + 1], p[i + 2]) : PackRgb9E5(p[i], p[i + 1], p[i + 2]);
                        BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(o), v);
                    }

                    break;
                }

            default:
                work.CopyTo(stored, 0);
                break;
        }
    }

    /// <summary>Packs unsigned 11-, 11- and 10-bit floats, rounding to nearest; negatives and NaN become 0, overflow the largest finite value.</summary>
    internal static uint PackR11G11B10(float r, float g, float b)
    {
        return Small(r, 6) | (Small(g, 6) << 11) | (Small(b, 5) << 22);

        static uint Small(float value, int mantissa)
        {
            if (!(value > 0))
            {
                return 0;
            }

            int drop = 10 - mantissa;
            uint half = BitConverter.HalfToUInt16Bits((Half)value);
            uint largest = (30u << mantissa) | ((1u << mantissa) - 1);
            if (half >= 0x7C00)
            {
                return largest;
            }

            uint rounded = (half + (1u << (drop - 1))) >> drop;
            return Math.Min(rounded, largest);
        }
    }

    /// <summary>Packs RGB into R9G9B9E5, as the DXGI shared-exponent rules give it.</summary>
    internal static uint PackRgb9E5(float r, float g, float b)
    {
        const float Max = 511f / 512f * 65536f;
        r = Clamp(r);
        g = Clamp(g);
        b = Clamp(b);
        float largest = Math.Max(r, Math.Max(g, b));
        int exponent = Math.Max(-16, (int)MathF.Floor(MathF.Log2(Math.Max(largest, 1e-30f)))) + 1 + 15;
        float denominator = MathF.ScaleB(1, exponent - 15 - 9);
        if ((int)MathF.Round(largest / denominator) == 512)
        {
            denominator *= 2;
            exponent++;
        }

        uint m(float v) => (uint)Math.Min(511, (int)MathF.Round(v / denominator));
        return m(r) | (m(g) << 9) | (m(b) << 18) | ((uint)exponent << 27);

        static float Clamp(float v) => v > 0 ? Math.Min(v, Max) : 0;
    }

    /// <summary>A legacy header's pixel format.</summary>
    private readonly record struct Legacy(uint Flags, uint FourCC, uint Bits, uint R, uint G, uint B, uint A);

    /// <summary>What a writable format is: its DXGI numbers, how it is made and its legacy header, if it has one.</summary>
    private sealed record Target(DdsTextureFormat Format, int Dxgi, int SrgbDxgi, DdsNatural Work, int PixelBytes, int BlockBytes, Legacy? Legacy)
    {
        public bool IsBlock => BlockBytes > 0;

        public static Target For(DdsTextureFormat format)
        {
            const uint Rgb = 0x40, RgbA = 0x41, Lum = 0x20000, FourCC = 0x4;
            return format switch
            {
                DdsTextureFormat.Rgba8 => new(format, 28, 29, DdsNatural.Rgba8, 4, 0, new Legacy(RgbA, 0, 32, 0xFF, 0xFF00, 0xFF0000, 0xFF000000)),
                DdsTextureFormat.Bgra8 => new(format, 87, 91, DdsNatural.Rgba8, 4, 0, new Legacy(RgbA, 0, 32, 0xFF0000, 0xFF00, 0xFF, 0xFF000000)),
                DdsTextureFormat.Bgrx8 => new(format, 88, 93, DdsNatural.Rgba8, 4, 0, new Legacy(Rgb, 0, 32, 0xFF0000, 0xFF00, 0xFF, 0)),
                DdsTextureFormat.R8 => new(format, 61, 0, DdsNatural.Gray8, 1, 0, new Legacy(Lum, 0, 8, 0xFF, 0, 0, 0)),
                DdsTextureFormat.Rg8 => new(format, 49, 0, DdsNatural.Rgb8, 2, 0, null),
                DdsTextureFormat.A8 => new(format, 65, 0, DdsNatural.Alpha8, 1, 0, new Legacy(0x2, 0, 8, 0, 0, 0, 0xFF)),
                DdsTextureFormat.R16 => new(format, 56, 0, DdsNatural.Gray16, 2, 0, new Legacy(Lum, 0, 16, 0xFFFF, 0, 0, 0)),
                DdsTextureFormat.Rg16 => new(format, 35, 0, DdsNatural.Rgb16, 4, 0, new Legacy(Rgb, 0, 32, 0xFFFF, 0xFFFF0000, 0, 0)),
                DdsTextureFormat.Rgba16 => new(format, 11, 0, DdsNatural.Rgba16, 8, 0, new Legacy(FourCC, 36, 0, 0, 0, 0, 0)),
                DdsTextureFormat.RgbaF16 => new(format, 10, 0, DdsNatural.RgbaF16, 8, 0, null),
                DdsTextureFormat.R32F => new(format, 41, 0, DdsNatural.GrayF32, 4, 0, null),
                DdsTextureFormat.RgbF32 => new(format, 6, 0, DdsNatural.RgbF32, 12, 0, null),
                DdsTextureFormat.RgbaF32 => new(format, 2, 0, DdsNatural.RgbaF32, 16, 0, null),
                DdsTextureFormat.Rgb10A2 => new(format, 24, 0, DdsNatural.Rgb10A2, 4, 0, null),
                DdsTextureFormat.R11G11B10F => new(format, 26, 0, DdsNatural.RgbaF32, 4, 0, null),
                DdsTextureFormat.Rgb9E5 => new(format, 67, 0, DdsNatural.RgbaF32, 4, 0, null),
                DdsTextureFormat.Bgr565 => new(format, 85, 0, DdsNatural.Bgr565, 2, 0, new Legacy(Rgb, 0, 16, 0xF800, 0x7E0, 0x1F, 0)),
                DdsTextureFormat.Bgra5551 => new(format, 86, 0, DdsNatural.Bgra5551, 2, 0, new Legacy(RgbA, 0, 16, 0x7C00, 0x3E0, 0x1F, 0x8000)),
                DdsTextureFormat.Bgra4444 => new(format, 115, 0, DdsNatural.Bgra4444, 2, 0, new Legacy(RgbA, 0, 16, 0xF00, 0xF0, 0xF, 0xF000)),
                DdsTextureFormat.Bc1 => new(format, 71, 72, DdsNatural.Rgba8, 0, 8, new Legacy(FourCC, 0x31545844, 0, 0, 0, 0, 0)),
                DdsTextureFormat.Bc2 => new(format, 74, 75, DdsNatural.Rgba8, 0, 16, new Legacy(FourCC, 0x33545844, 0, 0, 0, 0, 0)),
                DdsTextureFormat.Bc3 => new(format, 77, 78, DdsNatural.Rgba8, 0, 16, new Legacy(FourCC, 0x35545844, 0, 0, 0, 0, 0)),
                DdsTextureFormat.Bc4 => new(format, 80, 0, DdsNatural.Gray8, 0, 8, new Legacy(FourCC, 0x31495441, 0, 0, 0, 0, 0)),
                DdsTextureFormat.Bc5 => new(format, 83, 0, DdsNatural.Rgb8, 0, 16, new Legacy(FourCC, 0x32495441, 0, 0, 0, 0, 0)),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, "The DDS format is not defined."),
            };
        }
    }
}
