using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Formats.Extended.Textures;
using Egami.Formats.Extended.Textures.Blocks;
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
internal sealed class DdsWriter : IFormatWriter, IMultiImageWriter, ITextureWriter
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

        (Target target, bool srgb, int dxgi) = Choose(dds, first);
        int full = target.IsPlanar ? 1 : 1 + (int)Math.Log2(Math.Max(first.Width, first.Height));
        int mips = dds.MipLevels == 0 ? (target.IsData ? 1 : full) : Math.Min(dds.MipLevels, full);
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
        int headerLength = Header(header, first.Width, first.Height, 1, mips, target, dxgi, dx10, dds.Cube, arraySize, premultiplied);
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

    public void WriteTexture(Texture texture, Stream output, WriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(output);
        DdsWriterOptions dds = options as DdsWriterOptions ?? DdsWriterOptions.Default;
        if (texture.IsVolume && texture.Layers > 1)
        {
            throw new NotSupportedException("DDS holds one volume texture, not an array of them; save it as KTX 2.");
        }

        Raster first = texture.Level(0)[0];
        (Target target, bool srgb, int dxgi) = Choose(dds, first);
        int mips = dds.MipLevels == 0 ? texture.MipLevels : Math.Min(dds.MipLevels, texture.MipLevels);
        if (target.IsPlanar && mips > 1)
        {
            throw new ArgumentException($"DDS: {target.Format} holds mip 0 alone; the texture has {mips} mips (TrimMips(1) drops the rest).", nameof(options));
        }

        bool premultiplied = dds.PremultipliedAlpha;
        int arraySize = texture.IsVolume ? 1 : texture.Layers;
        bool dx10 = dds.Dx10Header || srgb || target.Legacy is null || arraySize > 1 || (premultiplied && target.Format is not (DdsTextureFormat.Bc2 or DdsTextureFormat.Bc3));
        Span<byte> header = stackalloc byte[DdsHeader.Length + DdsHeader.Dx10Length];
        int headerLength = Header(header, texture.Width, texture.Height, texture.Depth, mips, target, dxgi, dx10, texture.IsCube, arraySize, premultiplied);
        output.Write(header[..headerLength]);
        if (texture.IsVolume)
        {
            // A volume is stored mip by mip, each mip's slices front to back.
            for (int m = 0; m < mips; m++)
            {
                foreach (Raster slice in texture.Level(m))
                {
                    output.Write(Encode(slice, target, dds.Effort, premultiplied));
                }
            }

            return;
        }

        // Surfaces are stored one after another, each with its mips.
        for (int i = 0; i < texture.Level(0).Count; i++)
        {
            for (int m = 0; m < mips; m++)
            {
                output.Write(Encode(texture.Level(m)[i], target, dds.Effort, premultiplied));
            }
        }
    }

    /// <summary>Picks the format, its sRGB form and DXGI number for what is written, from the options or the first image.</summary>
    private static (Target Target, bool Srgb, int Dxgi) Choose(DdsWriterOptions dds, Raster first)
    {
        DdsDetails? source = first.Metadata.FormatDetails as DdsDetails;
        Target target = Target.For(dds.Format ?? FromSource(source) ?? FromPixels(first.Layout));
        bool srgb = dds.Srgb ?? source?.IsSrgb ?? false;
        if (srgb && target.SrgbDxgi == 0)
        {
            if (dds.Srgb == true)
            {
                throw new ArgumentException($"DDS: {target.Format} has no sRGB form.", nameof(dds));
            }

            srgb = false;
        }

        if (target.IsPlanar && (first.Width % 2 != 0 || first.Height % 2 != 0))
        {
            throw new ArgumentException($"DDS: {target.Format} is 4:2:0 and needs an even width and height; {first.Width}×{first.Height} was given.", nameof(dds));
        }

        return (target, srgb, srgb ? target.SrgbDxgi : target.Dxgi);
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
            97 or 98 or 99 => DdsTextureFormat.Bc7,
            94 or 95 => DdsTextureFormat.Bc6h,
            96 => DdsTextureFormat.Bc6hSigned,
            62 => DdsTextureFormat.R8UInt,
            64 => DdsTextureFormat.R8SInt,
            50 => DdsTextureFormat.Rg8UInt,
            52 => DdsTextureFormat.Rg8SInt,
            30 => DdsTextureFormat.Rgba8UInt,
            32 => DdsTextureFormat.Rgba8SInt,
            57 => DdsTextureFormat.R16UInt,
            59 => DdsTextureFormat.R16SInt,
            36 => DdsTextureFormat.Rg16UInt,
            38 => DdsTextureFormat.Rg16SInt,
            12 => DdsTextureFormat.Rgba16UInt,
            14 => DdsTextureFormat.Rgba16SInt,
            42 => DdsTextureFormat.R32UInt,
            43 => DdsTextureFormat.R32SInt,
            17 => DdsTextureFormat.Rg32UInt,
            18 => DdsTextureFormat.Rg32SInt,
            7 => DdsTextureFormat.Rgb32UInt,
            8 => DdsTextureFormat.Rgb32SInt,
            3 => DdsTextureFormat.Rgba32UInt,
            4 => DdsTextureFormat.Rgba32SInt,
            55 => DdsTextureFormat.D16,
            44 or 45 or 46 => DdsTextureFormat.D24S8,
            40 => DdsTextureFormat.D32F,
            19 or 20 or 21 => DdsTextureFormat.D32FS8,
            103 => DdsTextureFormat.Nv12,
            104 => DdsTextureFormat.P010,
            105 => DdsTextureFormat.P016,
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
    private static int Header(Span<byte> h, int width, int height, int depth, int mips, Target target, int dxgi, bool dx10, bool cube, int arraySize, bool premultiplied)
    {
        h.Clear();
        long pitch = target.IsBlock ? Math.Max(1, (width + 3) / 4) * (long)target.BlockBytes * Math.Max(1, (height + 3) / 4) : (long)width * target.PixelBytes;
        bool volume = depth > 1;
        uint flags = 0x1007 | (mips > 1 ? 0x20000u : 0) | (target.IsBlock ? 0x80000u : 0x8u) | (volume ? 0x800000u : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h, DdsHeader.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 124);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)Math.Min(pitch, uint.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], volume ? (uint)depth : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)mips);
        BinaryPrimitives.WriteUInt32LittleEndian(h[76..], 32);
        uint caps = 0x1000 | (mips > 1 || cube || volume ? 0x8u : 0) | (mips > 1 ? 0x400000u : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h[108..], caps);
        BinaryPrimitives.WriteUInt32LittleEndian(h[112..], cube ? 0xFE00u : volume ? 0x200000u : 0);
        if (dx10)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[80..], 0x4);
            BinaryPrimitives.WriteUInt32LittleEndian(h[84..], 0x30315844);
            BinaryPrimitives.WriteUInt32LittleEndian(h[128..], (uint)dxgi);
            BinaryPrimitives.WriteUInt32LittleEndian(h[132..], volume ? 4u : 3);
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

        int pixel = TextureDecode.NaturalBytes(target.Work);
        if (target.IsPlanar)
        {
            return Planar(target.Format, work, width, height);
        }

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
            Span<byte> gathered = stackalloc byte[16 * 8];
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
                    case DdsTextureFormat.Bc7:
                        Bc7Encoder.Encode(gathered, block, effort);
                        break;
                    case DdsTextureFormat.Bc6h:
                    case DdsTextureFormat.Bc6hSigned:
                        Bc6hEncoder.Encode(MemoryMarshal.Cast<byte, ushort>(gathered), block, target.Format == DdsTextureFormat.Bc6hSigned, effort);
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
    internal static byte[] Work(Raster level, TextureNatural natural)
    {
        return natural switch
        {
            TextureNatural.Gray8 => Bytes(level.As<Gray8>()),
            TextureNatural.Gray16 => Bytes(level.As<Gray16>()),
            TextureNatural.GrayF32 => Bytes(level.As<GrayF32>()),
            TextureNatural.Alpha8 => Bytes(level.As<Alpha8>()),
            TextureNatural.Rgb8 => Bytes(level.As<Rgb8>()),
            TextureNatural.Rgb16 => Bytes(level.As<Rgb16>()),
            TextureNatural.RgbF32 => Bytes(level.As<RgbF32>()),
            TextureNatural.Rgba16 => Bytes(level.As<Rgba16>()),
            TextureNatural.RgbaF16 => Bytes(level.As<RgbaF16>()),
            TextureNatural.RgbaF32 => Bytes(level.As<RgbaF32>()),
            TextureNatural.Rgb10A2 => Bytes(level.As<Rgb10A2>()),
            TextureNatural.Bgr565 => Bytes(level.As<Bgr565>()),
            TextureNatural.Bgra5551 => Bytes(level.As<Bgra5551>()),
            TextureNatural.Bgra4444 => Bytes(level.As<Bgra4444>()),
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

    internal static void Premultiply(byte[] work, TextureNatural natural)
    {
        switch (natural)
        {
            case TextureNatural.Rgba8:
                for (int i = 0; i < work.Length; i += 4)
                {
                    int a = work[i + 3];
                    for (int c = 0; c < 3; c++)
                    {
                        work[i + c] = (byte)(((work[i + c] * a) + 127) / 255);
                    }
                }

                break;
            case TextureNatural.Rgba16:
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

            case TextureNatural.RgbaF16:
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

            case TextureNatural.RgbaF32:
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
            case DdsTextureFormat.Rg8UInt:
            case DdsTextureFormat.Rg8SInt:
                {
                    byte flip = format == DdsTextureFormat.Rg8SInt ? (byte)0x80 : (byte)0;
                    for (int i = 0, o = 0; i < work.Length; i += 3, o += 2)
                    {
                        stored[o] = (byte)(work[i] ^ flip);
                        stored[o + 1] = (byte)(work[i + 1] ^ flip);
                    }

                    break;
                }

            case DdsTextureFormat.Rg16:
            case DdsTextureFormat.Rg16UInt:
            case DdsTextureFormat.Rg16SInt:
                for (int i = 0, o = 0; i < work.Length; i += 6, o += 4)
                {
                    work.AsSpan(i, 4).CopyTo(stored.AsSpan(o));
                }

                if (format == DdsTextureFormat.Rg16SInt)
                {
                    FlipSign16(stored);
                }

                break;
            case DdsTextureFormat.R8SInt:
            case DdsTextureFormat.Rgba8SInt:
                // The reader maps a signed integer v to v + 128; storing the raster's byte with its top bit flipped is the inverse.
                for (int i = 0; i < work.Length; i++)
                {
                    stored[i] = (byte)(work[i] ^ 0x80);
                }

                break;
            case DdsTextureFormat.R16SInt:
            case DdsTextureFormat.Rgba16SInt:
                work.CopyTo(stored, 0);
                FlipSign16(stored);
                break;
            case DdsTextureFormat.R32UInt:
            case DdsTextureFormat.R32SInt:
            case DdsTextureFormat.Rg32UInt:
            case DdsTextureFormat.Rg32SInt:
            case DdsTextureFormat.Rgb32UInt:
            case DdsTextureFormat.Rgb32SInt:
            case DdsTextureFormat.Rgba32UInt:
            case DdsTextureFormat.Rgba32SInt:
                {
                    // Floats in (gray, RGB or RGBA), integers out; R32G32 drops the working RGB's blue.
                    ReadOnlySpan<float> p = MemoryMarshal.Cast<byte, float>(work.AsSpan());
                    bool signed = format is DdsTextureFormat.R32SInt or DdsTextureFormat.Rg32SInt or DdsTextureFormat.Rgb32SInt or DdsTextureFormat.Rgba32SInt;
                    int channels = format switch
                    {
                        DdsTextureFormat.R32UInt or DdsTextureFormat.R32SInt => 1,
                        DdsTextureFormat.Rg32UInt or DdsTextureFormat.Rg32SInt => 2,
                        DdsTextureFormat.Rgb32UInt or DdsTextureFormat.Rgb32SInt => 3,
                        _ => 4,
                    };
                    int step = channels == 2 ? 3 : channels;
                    for (int i = 0, o = 0; i < p.Length; i += step)
                    {
                        for (int c = 0; c < channels; c++, o += 4)
                        {
                            BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(o), Integer32(p[i + c], signed));
                        }
                    }

                    break;
                }

            case DdsTextureFormat.D24S8:
                {
                    ReadOnlySpan<float> p = MemoryMarshal.Cast<byte, float>(work.AsSpan());
                    for (int i = 0; i < p.Length; i++)
                    {
                        // Stencil, the top byte, stays 0.
                        uint depth = p[i] > 0 ? (uint)Math.Round(Math.Min(p[i], 1f) * 16777215.0) : 0;
                        BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(i * 4), depth);
                    }

                    break;
                }

            case DdsTextureFormat.D32FS8:
                for (int i = 0; i < work.Length / 4; i++)
                {
                    // Depth, then the stencil byte and 24 unused bits, all 0.
                    work.AsSpan(i * 4, 4).CopyTo(stored.AsSpan(i * 8));
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

    /// <summary>Flips the sign bit of every little-endian 16-bit sample: the inverse of the reader's v + 32768 for a SINT format.</summary>
    private static void FlipSign16(byte[] stored)
    {
        for (int i = 1; i < stored.Length; i += 2)
        {
            stored[i] ^= 0x80;
        }
    }

    /// <summary>Rounds a float to the nearest 32-bit integer, clamped to the type's range (NaN becomes 0), as stored bits.</summary>
    private static uint Integer32(float value, bool signed)
    {
        if (float.IsNaN(value))
        {
            return 0;
        }

        double rounded = Math.Round((double)value, MidpointRounding.ToEven);
        return signed ? (uint)(int)Math.Clamp(rounded, int.MinValue, int.MaxValue) : (uint)Math.Clamp(rounded, 0, uint.MaxValue);
    }

    /// <summary>
    /// Converts working RGB (<see cref="Rgb8"/> for NV12, <see cref="Rgb16"/> for P010 and P016) into a 4:2:0 surface: BT.601
    /// Y′CbCr in studio range, the inverse of the reader's conversion (Microsoft's "Recommended 8-Bit YUV Formats for Video
    /// Rendering" and its 10- and 16-bit forms), chroma the mean of each 2 × 2 block's.
    /// </summary>
    private static byte[] Planar(DdsTextureFormat format, byte[] work, int width, int height)
    {
        bool wide = format != DdsTextureFormat.Nv12;
        int bits = format == DdsTextureFormat.P010 ? 10 : wide ? 16 : 8;
        int sample = wide ? 2 : 1;
        int shift = format == DdsTextureFormat.P010 ? 6 : 0;
        double input = wide ? 65535 : 255;
        double unit = Math.ScaleB(1.0, bits - 8);
        double maximum = Math.ScaleB(1.0, bits) - 1;
        double yScale = 219 * unit / input;
        double cScale = 224 * unit / input;
        long pitch = (long)width * sample;
        long luma = pitch * height;
        byte[] surface = new byte[luma + (pitch * (height / 2))];
        ReadOnlySpan<ushort> words = MemoryMarshal.Cast<byte, ushort>(work.AsSpan());
        for (int cy = 0; cy < height / 2; cy++)
        {
            for (int cx = 0; cx < width / 2; cx++)
            {
                double cb = 0, cr = 0;
                for (int i = 0; i < 4; i++)
                {
                    int x = (cx * 2) + (i & 1);
                    int y = (cy * 2) + (i >> 1);
                    int at = ((y * width) + x) * 3;
                    double r = wide ? words[at] : work[at];
                    double g = wide ? words[at + 1] : work[at + 1];
                    double b = wide ? words[at + 2] : work[at + 2];
                    double l = (0.299 * r) + (0.587 * g) + (0.114 * b);
                    Store(surface, (y * pitch) + (x * sample), (16 * unit) + (yScale * l));
                    cb += (b - l) / 1.772;
                    cr += (r - l) / 1.402;
                }

                long chroma = luma + (cy * pitch) + (cx * 2 * sample);
                Store(surface, chroma, (128 * unit) + (cScale * cb / 4));
                Store(surface, chroma + sample, (128 * unit) + (cScale * cr / 4));
            }
        }

        return surface;

        void Store(byte[] to, long at, double value)
        {
            int code = (int)Math.Clamp(Math.Round(value), 0, maximum);
            if (wide)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(to.AsSpan((int)at), (ushort)(code << shift));
            }
            else
            {
                to[at] = (byte)code;
            }
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
    private sealed record Target(DdsTextureFormat Format, int Dxgi, int SrgbDxgi, TextureNatural Work, int PixelBytes, int BlockBytes, Legacy? Legacy)
    {
        public bool IsBlock => BlockBytes > 0;

        /// <summary>Gets whether the format is a 4:2:0 planar video surface.</summary>
        public bool IsPlanar => Format is DdsTextureFormat.Nv12 or DdsTextureFormat.P010 or DdsTextureFormat.P016;

        /// <summary>Gets whether the format holds integer or depth data, which averaged mips would corrupt.</summary>
        public bool IsData => Format is >= DdsTextureFormat.R8UInt and <= DdsTextureFormat.D32FS8;

        public static Target For(DdsTextureFormat format)
        {
            const uint Rgb = 0x40, RgbA = 0x41, Lum = 0x20000, FourCC = 0x4;
            return format switch
            {
                DdsTextureFormat.Rgba8 => new(format, 28, 29, TextureNatural.Rgba8, 4, 0, new Legacy(RgbA, 0, 32, 0xFF, 0xFF00, 0xFF0000, 0xFF000000)),
                DdsTextureFormat.Bgra8 => new(format, 87, 91, TextureNatural.Rgba8, 4, 0, new Legacy(RgbA, 0, 32, 0xFF0000, 0xFF00, 0xFF, 0xFF000000)),
                DdsTextureFormat.Bgrx8 => new(format, 88, 93, TextureNatural.Rgba8, 4, 0, new Legacy(Rgb, 0, 32, 0xFF0000, 0xFF00, 0xFF, 0)),
                DdsTextureFormat.R8 => new(format, 61, 0, TextureNatural.Gray8, 1, 0, new Legacy(Lum, 0, 8, 0xFF, 0, 0, 0)),
                DdsTextureFormat.Rg8 => new(format, 49, 0, TextureNatural.Rgb8, 2, 0, null),
                DdsTextureFormat.A8 => new(format, 65, 0, TextureNatural.Alpha8, 1, 0, new Legacy(0x2, 0, 8, 0, 0, 0, 0xFF)),
                DdsTextureFormat.R16 => new(format, 56, 0, TextureNatural.Gray16, 2, 0, new Legacy(Lum, 0, 16, 0xFFFF, 0, 0, 0)),
                DdsTextureFormat.Rg16 => new(format, 35, 0, TextureNatural.Rgb16, 4, 0, new Legacy(Rgb, 0, 32, 0xFFFF, 0xFFFF0000, 0, 0)),
                DdsTextureFormat.Rgba16 => new(format, 11, 0, TextureNatural.Rgba16, 8, 0, new Legacy(FourCC, 36, 0, 0, 0, 0, 0)),
                DdsTextureFormat.RgbaF16 => new(format, 10, 0, TextureNatural.RgbaF16, 8, 0, null),
                DdsTextureFormat.R32F => new(format, 41, 0, TextureNatural.GrayF32, 4, 0, null),
                DdsTextureFormat.RgbF32 => new(format, 6, 0, TextureNatural.RgbF32, 12, 0, null),
                DdsTextureFormat.RgbaF32 => new(format, 2, 0, TextureNatural.RgbaF32, 16, 0, null),
                DdsTextureFormat.Rgb10A2 => new(format, 24, 0, TextureNatural.Rgb10A2, 4, 0, null),
                DdsTextureFormat.R11G11B10F => new(format, 26, 0, TextureNatural.RgbaF32, 4, 0, null),
                DdsTextureFormat.Rgb9E5 => new(format, 67, 0, TextureNatural.RgbaF32, 4, 0, null),
                DdsTextureFormat.Bgr565 => new(format, 85, 0, TextureNatural.Bgr565, 2, 0, new Legacy(Rgb, 0, 16, 0xF800, 0x7E0, 0x1F, 0)),
                DdsTextureFormat.Bgra5551 => new(format, 86, 0, TextureNatural.Bgra5551, 2, 0, new Legacy(RgbA, 0, 16, 0x7C00, 0x3E0, 0x1F, 0x8000)),
                DdsTextureFormat.Bgra4444 => new(format, 115, 0, TextureNatural.Bgra4444, 2, 0, new Legacy(RgbA, 0, 16, 0xF00, 0xF0, 0xF, 0xF000)),
                DdsTextureFormat.Bc1 => new(format, 71, 72, TextureNatural.Rgba8, 0, 8, new Legacy(FourCC, 0x31545844, 0, 0, 0, 0, 0)),
                DdsTextureFormat.Bc2 => new(format, 74, 75, TextureNatural.Rgba8, 0, 16, new Legacy(FourCC, 0x33545844, 0, 0, 0, 0, 0)),
                DdsTextureFormat.Bc3 => new(format, 77, 78, TextureNatural.Rgba8, 0, 16, new Legacy(FourCC, 0x35545844, 0, 0, 0, 0, 0)),
                DdsTextureFormat.Bc4 => new(format, 80, 0, TextureNatural.Gray8, 0, 8, new Legacy(FourCC, 0x31495441, 0, 0, 0, 0, 0)),
                DdsTextureFormat.Bc5 => new(format, 83, 0, TextureNatural.Rgb8, 0, 16, new Legacy(FourCC, 0x32495441, 0, 0, 0, 0, 0)),
                DdsTextureFormat.Bc7 => new(format, 98, 99, TextureNatural.Rgba8, 0, 16, null),
                DdsTextureFormat.Bc6h => new(format, 95, 0, TextureNatural.RgbaF16, 0, 16, null),
                DdsTextureFormat.Bc6hSigned => new(format, 96, 0, TextureNatural.RgbaF16, 0, 16, null),
                DdsTextureFormat.R8UInt => new(format, 62, 0, TextureNatural.Gray8, 1, 0, null),
                DdsTextureFormat.R8SInt => new(format, 64, 0, TextureNatural.Gray8, 1, 0, null),
                DdsTextureFormat.Rg8UInt => new(format, 50, 0, TextureNatural.Rgb8, 2, 0, null),
                DdsTextureFormat.Rg8SInt => new(format, 52, 0, TextureNatural.Rgb8, 2, 0, null),
                DdsTextureFormat.Rgba8UInt => new(format, 30, 0, TextureNatural.Rgba8, 4, 0, null),
                DdsTextureFormat.Rgba8SInt => new(format, 32, 0, TextureNatural.Rgba8, 4, 0, null),
                DdsTextureFormat.R16UInt => new(format, 57, 0, TextureNatural.Gray16, 2, 0, null),
                DdsTextureFormat.R16SInt => new(format, 59, 0, TextureNatural.Gray16, 2, 0, null),
                DdsTextureFormat.Rg16UInt => new(format, 36, 0, TextureNatural.Rgb16, 4, 0, null),
                DdsTextureFormat.Rg16SInt => new(format, 38, 0, TextureNatural.Rgb16, 4, 0, null),
                DdsTextureFormat.Rgba16UInt => new(format, 12, 0, TextureNatural.Rgba16, 8, 0, null),
                DdsTextureFormat.Rgba16SInt => new(format, 14, 0, TextureNatural.Rgba16, 8, 0, null),
                DdsTextureFormat.R32UInt => new(format, 42, 0, TextureNatural.GrayF32, 4, 0, null),
                DdsTextureFormat.R32SInt => new(format, 43, 0, TextureNatural.GrayF32, 4, 0, null),
                DdsTextureFormat.Rg32UInt => new(format, 17, 0, TextureNatural.RgbF32, 8, 0, null),
                DdsTextureFormat.Rg32SInt => new(format, 18, 0, TextureNatural.RgbF32, 8, 0, null),
                DdsTextureFormat.Rgb32UInt => new(format, 7, 0, TextureNatural.RgbF32, 12, 0, null),
                DdsTextureFormat.Rgb32SInt => new(format, 8, 0, TextureNatural.RgbF32, 12, 0, null),
                DdsTextureFormat.Rgba32UInt => new(format, 3, 0, TextureNatural.RgbaF32, 16, 0, null),
                DdsTextureFormat.Rgba32SInt => new(format, 4, 0, TextureNatural.RgbaF32, 16, 0, null),
                DdsTextureFormat.D16 => new(format, 55, 0, TextureNatural.Gray16, 2, 0, null),
                DdsTextureFormat.D24S8 => new(format, 45, 0, TextureNatural.GrayF32, 4, 0, null),
                DdsTextureFormat.D32F => new(format, 40, 0, TextureNatural.GrayF32, 4, 0, null),
                DdsTextureFormat.D32FS8 => new(format, 20, 0, TextureNatural.GrayF32, 8, 0, null),
                DdsTextureFormat.Nv12 => new(format, 103, 0, TextureNatural.Rgb8, 1, 0, null),
                DdsTextureFormat.P010 => new(format, 104, 0, TextureNatural.Rgb16, 2, 0, null),
                DdsTextureFormat.P016 => new(format, 105, 0, TextureNatural.Rgb16, 2, 0, null),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, "The DDS format is not defined."),
            };
        }
    }
}
