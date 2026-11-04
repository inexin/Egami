using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Colors;
using Egami.Formats.Extended.Dds;
using Egami.Formats.Extended.Textures;
using Egami.Formats.Extended.Textures.Blocks;
using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Formats.Extended.Ktx;

/// <summary>
/// Writes KTX 2 from the Khronos KTX 2.0 specification: the header, the level index, a data format descriptor built from the
/// format (Khronos Data Format Specification 1.4) and the raster's colour tag, the KTXorientation and KTXwriter keys, then
/// every mip level, smallest first, each holding every layer and face. Mips are made with <see cref="MipChain"/>. Block formats
/// encode rows of blocks in parallel; each block depends only on its own pixels (UASTC's rate-distortion pass works in fixed
/// bands, ETC1S builds its codebooks deterministically), so the bytes never depend on the thread count. A texture is written
/// whole, so the writer has no row encoder.
/// </summary>
internal sealed partial class Ktx2Writer : IFormatWriter, IMultiImageWriter, ITextureWriter
{
    private static readonly byte[] Identifier = [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly string WriterName = "Egami " + (typeof(Ktx2Writer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0").Split('+')[0];

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
        Ktx2WriterOptions ktx = options as Ktx2WriterOptions ?? Ktx2WriterOptions.Default;
        Raster first = rasters[0];
        foreach (Raster raster in rasters)
        {
            if (raster.Width != first.Width || raster.Height != first.Height)
            {
                throw new ArgumentException($"Every surface of a KTX 2 texture has the same size; {raster.Width}×{raster.Height} differs from {first.Width}×{first.Height}.", nameof(rasters));
            }
        }

        if (ktx.Cube && (rasters.Count % 6 != 0 || first.Width != first.Height))
        {
            throw new ArgumentException($"A cube map needs square faces, six a cube; {rasters.Count} rasters of {first.Width}×{first.Height} were given.", nameof(rasters));
        }

        int full = 1 + (int)Math.Log2(Math.Max(first.Width, first.Height));
        int mips = ktx.MipLevels == 0 ? full : Math.Min(ktx.MipLevels, full);
        int layers = ktx.Cube ? rasters.Count / 6 : rasters.Count;
        Write(output, ktx, rasters, first.Width, first.Height, 0, layers > 1 ? layers : 0, ktx.Cube ? 6 : 1, mips, _ => rasters.Count, srgb => Chains(rasters, mips, srgb));
    }

    public void WriteTexture(Texture texture, Stream output, WriterOptions options)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(output);
        Ktx2WriterOptions ktx = options as Ktx2WriterOptions ?? Ktx2WriterOptions.Default;
        int mips = ktx.MipLevels == 0 ? texture.MipLevels : Math.Min(ktx.MipLevels, texture.MipLevels);
        Write(output, ktx, texture.Level(0), texture.Width, texture.Height, texture.IsVolume ? texture.Depth : 0, texture.Layers > 1 ? texture.Layers : 0,
            texture.Faces, mips, m => texture.Level(m).Count, _ => Images(texture, mips));

        static IEnumerable<(int, int, Raster)> Images(Texture texture, int mips)
        {
            for (int m = 0; m < mips; m++)
            {
                IReadOnlyList<Raster> level = texture.Level(m);
                for (int i = 0; i < level.Count; i++)
                {
                    yield return (m, i, level[i]);
                }
            }
        }
    }

    /// <summary>Yields each raster's mips, made with <see cref="MipChain"/>, as (mip, index in the level, image).</summary>
    private static IEnumerable<(int, int, Raster)> Chains(IReadOnlyList<Raster> rasters, int mips, bool srgb)
    {
        for (int i = 0; i < rasters.Count; i++)
        {
            Raster raster = rasters[i];
            using MipChain? chain = mips > 1 ? MipChain.Build(raster, srgb ? ColorSpace.Linear : ColorSpace.Encoded) : null;
            for (int m = 0; m < mips; m++)
            {
                // A KTX mip rounds its size down; the chain's halving rounds up, so an odd size takes a box resize from it.
                int w = Math.Max(1, raster.Width >> m);
                int h = Math.Max(1, raster.Height >> m);
                Raster level = chain is null ? raster : chain.Levels[m];
                if (level.Width == w && level.Height == h)
                {
                    yield return (m, i, level);
                }
                else
                {
                    using Raster resized = chain!.Resize(w, h, ResampleFilter.Box);
                    yield return (m, i, resized);
                }
            }
        }
    }

    /// <summary>
    /// Writes a texture whose images <paramref name="images"/> yields, in any order, as (mip, index in the level, image); a level
    /// holds <paramref name="count"/> images in the file's order: every layer, every face of it, every depth slice of that.
    /// </summary>
    private static void Write(Stream output, Ktx2WriterOptions ktx, IReadOnlyList<Raster> surfaces, int width, int height, int depth, int layers, int faces,
        int mips, Func<int, int> count, Func<bool, IEnumerable<(int Mip, int Index, Raster Image)>> images)
    {
        Raster first = surfaces[0];
        KtxDetails? ktxSource = first.Metadata.FormatDetails as KtxDetails;
        DdsDetails? ddsSource = first.Metadata.FormatDetails as DdsDetails;
        Target target = Target.For(ktx.Format ?? FromKtx(ktxSource) ?? FromDds(ddsSource) ?? FromPixels(first.Layout));
        bool srgb = ktx.Srgb ?? ktxSource?.IsSrgb ?? ddsSource?.IsSrgb ?? TaggedSrgb(first.Metadata);
        if (srgb && !target.HasSrgb)
        {
            if (ktx.Srgb == true)
            {
                throw new ArgumentException($"KTX 2: {target.Format} has no sRGB form.", nameof(ktx));
            }

            srgb = false;
        }

        KtxSupercompression scheme = target.Format == KtxTextureFormat.Etc1s ? KtxSupercompression.BasisLZ : ktx.Supercompression;
        if (scheme == KtxSupercompression.BasisLZ && target.Format != KtxTextureFormat.Etc1s)
        {
            throw new ArgumentException("KTX 2: BasisLZ supercompression is for KtxTextureFormat.Etc1s only.", nameof(ktx));
        }

        if (scheme is not (KtxSupercompression.None or KtxSupercompression.BasisLZ or KtxSupercompression.Zstandard or KtxSupercompression.Zlib))
        {
            throw new ArgumentOutOfRangeException(nameof(ktx), scheme, "The KTX 2 supercompression scheme is not defined.");
        }

        if (scheme == KtxSupercompression.Zlib && ktx.CompressionLevel > 9)
        {
            throw new ArgumentException($"KTX 2: zlib levels run 1 to 9; {ktx.CompressionLevel} was given.", nameof(ktx));
        }

        bool premultiplied = ktx.PremultipliedAlpha;
        int maxParallelism = Math.Max(1, first.Context.MaxParallelism);
        bool alpha = target.Format is KtxTextureFormat.Etc1s or KtxTextureFormat.Uastc && surfaces.Any(HasTransparency);
        byte[][][] encoded = new byte[mips][][];
        for (int m = 0; m < mips; m++)
        {
            encoded[m] = new byte[count(m)][];
        }

        List<(int Index, Etc1sImage Image)> etc1s = [];
        foreach ((int m, int index, Raster image) in images(srgb))
        {
            byte[] work = DdsWriter.Work(image, target.Work);
            if (premultiplied)
            {
                DdsWriter.Premultiply(work, target.Work);
            }

            if (target.Format == KtxTextureFormat.Etc1s)
            {
                etc1s.Add((index, new Etc1sImage(work, image.Width, image.Height, m)));
            }
            else
            {
                encoded[m][index] = Encode(work, image.Width, image.Height, target, ktx, srgb, maxParallelism);
            }
        }

        byte[][] levels;
        byte[] globalData = [];
        if (target.Format == KtxTextureFormat.Etc1s)
        {
            // Etc1sEncoder wants mip level order, then layer, face and slice.
            Etc1sImage[] ordered = [.. etc1s.OrderBy(i => i.Image.Level).ThenBy(i => i.Index).Select(i => i.Image)];
            Etc1sEncoded basis = Etc1sEncoder.Encode(ordered, alpha, ktx.BasisQuality, ktx.Effort, maxParallelism);
            globalData = basis.GlobalData;
            levels = basis.Levels;
        }
        else
        {
            levels = [.. encoded.Select(level => level.Length == 1 ? level[0] : [.. level.SelectMany(i => i)])];
        }

        long[] uncompressed = new long[mips];
        for (int m = 0; m < mips; m++)
        {
            uncompressed[m] = scheme == KtxSupercompression.BasisLZ ? 0 : levels[m].Length;
            levels[m] = scheme switch
            {
                KtxSupercompression.Zstandard => Zstd(levels[m], ktx.CompressionLevel == 0 ? Compression.Zstandard.DefaultLevel : ktx.CompressionLevel),
                KtxSupercompression.Zlib => Zlib(levels[m], ktx.CompressionLevel == 0 ? 6 : ktx.CompressionLevel),
                _ => levels[m],
            };
        }

        byte[] dfd = Dfd(target, srgb, alpha, premultiplied, first.Metadata);
        byte[] kvd = KeyValues(depth > 0 ? "rdi" : "rd");
        Assemble(output, target.VkFor(srgb), target, width, height, depth, layers, faces, scheme, dfd, kvd, globalData, levels, uncompressed);
    }

    /// <summary>Returns whether any pixel of a raster with an alpha channel is not opaque.</summary>
    private static bool HasTransparency(Raster raster)
    {
        if (!raster.Layout.HasAlpha)
        {
            return false;
        }

        using Raster<Rgba8> rgba = raster.As<Rgba8>();
        for (int y = 0; y < rgba.Height; y++)
        {
            foreach (Rgba8 p in rgba.Row(y))
            {
                if (p.A != 255)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Returns whether a raster's tag is sRGB or missing (Egami reads untagged pixels as sRGB).</summary>
    private static bool TaggedSrgb(RasterMetadata metadata)
    {
        if (metadata.CodePoints is ColorCodePoints points)
        {
            return points.TransferCharacteristics == 13;
        }

        return metadata.ColorSpace is ColorSpaceHint.Unknown or ColorSpaceHint.Srgb;
    }

    /// <summary>Returns a KTX or KTX 2 source's format when the writer can write it.</summary>
    private static KtxTextureFormat? FromKtx(KtxDetails? details)
    {
        if (details is null)
        {
            return null;
        }

        if (details.VkFormat == 0)
        {
            return details.FormatName.StartsWith("ETC1S", StringComparison.Ordinal) ? KtxTextureFormat.Etc1s
                : details.FormatName.StartsWith("UASTC_4x4", StringComparison.Ordinal) ? KtxTextureFormat.Uastc
                : null;
        }

        foreach (KtxTextureFormat format in Enum.GetValues<KtxTextureFormat>())
        {
            Target target = Target.For(format);
            if (format != KtxTextureFormat.Etc1 && (target.Vk == details.VkFormat || target.SrgbVk == details.VkFormat))
            {
                return format;
            }
        }

        return null;
    }

    /// <summary>Returns a DDS source's format when the writer can write it.</summary>
    private static KtxTextureFormat? FromDds(DdsDetails? details)
    {
        if (details is null || details.FormatName == "A8L8")
        {
            return null;
        }

        return details.DxgiFormat switch
        {
            27 or 28 or 29 => KtxTextureFormat.Rgba8,
            87 or 90 or 91 => KtxTextureFormat.Bgra8,
            60 or 61 => KtxTextureFormat.R8,
            48 or 49 => KtxTextureFormat.Rg8,
            53 or 56 => KtxTextureFormat.R16,
            33 or 35 => KtxTextureFormat.Rg16,
            9 or 11 => KtxTextureFormat.Rgba16,
            10 => KtxTextureFormat.RgbaF16,
            39 or 41 => KtxTextureFormat.R32F,
            5 or 6 => KtxTextureFormat.RgbF32,
            1 or 2 => KtxTextureFormat.RgbaF32,
            23 or 24 => KtxTextureFormat.Rgb10A2,
            26 => KtxTextureFormat.R11G11B10F,
            67 => KtxTextureFormat.Rgb9E5,
            85 => KtxTextureFormat.Bgr565,
            86 => KtxTextureFormat.Bgra5551,
            115 => KtxTextureFormat.Bgra4444,
            70 or 71 or 72 => KtxTextureFormat.Bc1,
            73 or 74 or 75 => KtxTextureFormat.Bc2,
            76 or 77 or 78 => KtxTextureFormat.Bc3,
            79 or 80 => KtxTextureFormat.Bc4,
            82 or 83 => KtxTextureFormat.Bc5,
            97 or 98 or 99 => KtxTextureFormat.Bc7,
            94 or 95 => KtxTextureFormat.Bc6h,
            96 => KtxTextureFormat.Bc6hSigned,
            _ => null,
        };
    }

    /// <summary>Returns the uncompressed format nearest a pixel type.</summary>
    private static KtxTextureFormat FromPixels(PixelLayoutInfo layout)
    {
        if (layout == Rgb10A2.Layout)
        {
            return KtxTextureFormat.Rgb10A2;
        }

        if (layout == Bgr565.Layout)
        {
            return KtxTextureFormat.Bgr565;
        }

        if (layout == Bgra5551.Layout)
        {
            return KtxTextureFormat.Bgra5551;
        }

        if (layout == Bgra4444.Layout)
        {
            return KtxTextureFormat.Bgra4444;
        }

        return (layout.ChannelOrder, layout.ChannelType) switch
        {
            (PixelChannelOrder.Gray, PixelChannelType.Unorm8) => KtxTextureFormat.R8,
            (PixelChannelOrder.Gray, PixelChannelType.Unorm16) => KtxTextureFormat.R16,
            (PixelChannelOrder.Gray, PixelChannelType.Float32 or PixelChannelType.Float16) => KtxTextureFormat.R32F,
            (PixelChannelOrder.Rgb or PixelChannelOrder.Bgr, PixelChannelType.Unorm8) => KtxTextureFormat.Rgb8,
            (PixelChannelOrder.Rgb or PixelChannelOrder.Bgr, PixelChannelType.Unorm16) => KtxTextureFormat.Rgb16,
            (_, PixelChannelType.Unorm16) => KtxTextureFormat.Rgba16,
            (_, PixelChannelType.Float16) => KtxTextureFormat.RgbaF16,
            (PixelChannelOrder.Rgb or PixelChannelOrder.Bgr, PixelChannelType.Float32) => KtxTextureFormat.RgbF32,
            (_, PixelChannelType.Float32) => KtxTextureFormat.RgbaF32,
            _ => KtxTextureFormat.Rgba8,
        };
    }

    /// <summary>Encodes one image, its working pixels packed, in the target format.</summary>
    private static byte[] Encode(byte[] work, int width, int height, Target target, Ktx2WriterOptions options, bool srgb, int maxParallelism)
    {
        if (!target.IsBlock)
        {
            byte[] stored = new byte[(long)width * height * target.PixelBytes];
            Pack(target.Format, work, stored);
            return stored;
        }

        int pixel = TextureDecode.NaturalBytes(target.Work);
        int bw = target.BlockWidth;
        int bh = target.BlockHeight;
        int blocksWide = (width + bw - 1) / bw;
        int blocksHigh = (height + bh - 1) / bh;
        byte[] blocks = new byte[(long)blocksWide * blocksHigh * target.BlockBytes];
        Astc? astc = target.IsAstc ? Astc.For(bw, bh) : null;
        int effort = options.Effort;
        ParallelOptions parallel = new() { MaxDegreeOfParallelism = maxParallelism };
        Parallel.For(0, blocksHigh, parallel, by =>
        {
            Span<byte> gathered = stackalloc byte[144 * 8];
            int texels = bw * bh;
            for (int bx = 0; bx < blocksWide; bx++)
            {
                for (int i = 0; i < texels; i++)
                {
                    int x = Math.Min((bx * bw) + (i % bw), width - 1);
                    int y = Math.Min((by * bh) + (i / bw), height - 1);
                    work.AsSpan(((y * width) + x) * pixel, pixel).CopyTo(gathered[(i * pixel)..]);
                }

                Span<byte> block = blocks.AsSpan(((by * blocksWide) + bx) * target.BlockBytes, target.BlockBytes);
                EncodeBlock(target, astc, gathered, block, effort, srgb);
            }
        });

        if (target.Format == KtxTextureFormat.Uastc && options.UastcRdo > 0)
        {
            UastcEncoder.Rdo(work, width, height, blocks, options.UastcRdo, effort, maxParallelism);
        }

        return blocks;
    }

    private static void EncodeBlock(Target target, Astc? astc, Span<byte> gathered, Span<byte> block, int effort, bool srgb)
    {
        switch (target.Format)
        {
            case KtxTextureFormat.Bc1:
                Bc1To5Encoder.EncodeBc1(gathered, block, transparent: true, effort);
                break;
            case KtxTextureFormat.Bc2:
                Bc1To5Encoder.EncodeBc2(gathered, block, effort);
                break;
            case KtxTextureFormat.Bc3:
                Bc1To5Encoder.EncodeBc3(gathered, block, effort);
                break;
            case KtxTextureFormat.Bc4:
                Bc1To5Encoder.EncodeUnorm(gathered, 1, block, effort);
                break;
            case KtxTextureFormat.Bc5:
                Bc1To5Encoder.EncodeUnorm(gathered, 3, block, effort);
                Bc1To5Encoder.EncodeUnorm(gathered[1..], 3, block[8..], effort);
                break;
            case KtxTextureFormat.Bc6h:
            case KtxTextureFormat.Bc6hSigned:
                Bc6hEncoder.Encode(MemoryMarshal.Cast<byte, ushort>(gathered), block, target.Format == KtxTextureFormat.Bc6hSigned, effort);
                break;
            case KtxTextureFormat.Bc7:
                Bc7Encoder.Encode(gathered, block, effort);
                break;
            case KtxTextureFormat.Etc1:
            case KtxTextureFormat.Etc2Rgb:
            case KtxTextureFormat.Etc2RgbA1:
                EtcEncoder.EncodeRgb(gathered, block, effort, etc2: target.Format != KtxTextureFormat.Etc1, punchThrough: target.Format == KtxTextureFormat.Etc2RgbA1);
                break;
            case KtxTextureFormat.Etc2Rgba:
                EtcEncoder.EncodeRgba(gathered, block, effort);
                break;
            case KtxTextureFormat.EacR11:
            case KtxTextureFormat.EacR11Signed:
                EtcEncoder.EncodeEac11(MemoryMarshal.Cast<byte, ushort>(gathered), 1, block, effort, target.Format == KtxTextureFormat.EacR11Signed);
                break;
            case KtxTextureFormat.EacRg11:
            case KtxTextureFormat.EacRg11Signed:
                {
                    ReadOnlySpan<ushort> values = MemoryMarshal.Cast<byte, ushort>(gathered);
                    bool signed = target.Format == KtxTextureFormat.EacRg11Signed;
                    EtcEncoder.EncodeEac11(values, 3, block, effort, signed);
                    EtcEncoder.EncodeEac11(values[1..], 3, block[8..], effort, signed);
                    break;
                }

            case KtxTextureFormat.Uastc:
                UastcEncoder.Encode(gathered, block, effort);
                break;
            default:
                AstcEncoder.Encode(astc!, gathered, block, effort, srgb);
                break;
        }
    }

    /// <summary>Turns working pixels into stored ones.</summary>
    private static void Pack(KtxTextureFormat format, byte[] work, byte[] stored)
    {
        switch (format)
        {
            case KtxTextureFormat.Bgra8:
                for (int i = 0; i < work.Length; i += 4)
                {
                    stored[i] = work[i + 2];
                    stored[i + 1] = work[i + 1];
                    stored[i + 2] = work[i];
                    stored[i + 3] = work[i + 3];
                }

                break;
            case KtxTextureFormat.Rg8:
                for (int i = 0, o = 0; i < work.Length; i += 3, o += 2)
                {
                    stored[o] = work[i];
                    stored[o + 1] = work[i + 1];
                }

                break;
            case KtxTextureFormat.Rg16:
                for (int i = 0, o = 0; i < work.Length; i += 6, o += 4)
                {
                    work.AsSpan(i, 4).CopyTo(stored.AsSpan(o));
                }

                break;
            case KtxTextureFormat.R11G11B10F:
            case KtxTextureFormat.Rgb9E5:
                {
                    ReadOnlySpan<float> p = MemoryMarshal.Cast<byte, float>(work.AsSpan());
                    for (int i = 0, o = 0; i < p.Length; i += 4, o += 4)
                    {
                        uint v = format == KtxTextureFormat.R11G11B10F ? DdsWriter.PackR11G11B10(p[i], p[i + 1], p[i + 2]) : DdsWriter.PackRgb9E5(p[i], p[i + 1], p[i + 2]);
                        BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(o), v);
                    }

                    break;
                }

            default:
                work.CopyTo(stored, 0);
                break;
        }
    }

    private static byte[] Zstd(byte[] level, int compressionLevel)
    {
        byte[] compressed = new byte[Compression.Zstandard.MaxCompressedLength(level.Length)];
        int length = Compression.Zstandard.Compress(level, compressed, compressionLevel);
        return compressed[..length];
    }

    private static byte[] Zlib(byte[] level, int compressionLevel)
    {
        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, new ZLibCompressionOptions { CompressionLevel = compressionLevel }))
        {
            zlib.Write(level);
        }

        return compressed.ToArray();
    }

    /// <summary>The key/value data: KTXorientation (rows top-down, columns left to right, slices front to back) and KTXwriter, sorted by key.</summary>
    private static byte[] KeyValues(string orientation)
    {
        using MemoryStream data = new();
        Span<byte> length = stackalloc byte[4];
        foreach ((string key, string value) in (ReadOnlySpan<(string, string)>)[("KTXorientation", orientation), ("KTXwriter", WriterName)])
        {
            byte[] pair = [.. Encoding.UTF8.GetBytes(key), 0, .. Encoding.UTF8.GetBytes(value), 0];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)pair.Length);
            data.Write(length);
            data.Write(pair);
            data.Write(new byte[(4 - (pair.Length % 4)) % 4]);
        }

        return data.ToArray();
    }

    /// <summary>Writes the header, level index, descriptor, key/values, global data and levels, each placed as the specification aligns it.</summary>
    private static void Assemble(Stream output, int vk, Target target, int width, int height, int depth, int layers, int faces, KtxSupercompression scheme,
        byte[] dfd, byte[] kvd, byte[] globalData, byte[][] levels, long[] uncompressed)
    {
        int mips = levels.Length;
        long dfdOffset = 80 + (24L * mips);
        long kvdOffset = dfdOffset + dfd.Length;
        long sgdOffset = globalData.Length > 0 ? Align(kvdOffset + kvd.Length, 8) : 0;
        long end = globalData.Length > 0 ? sgdOffset + globalData.Length : kvdOffset + kvd.Length;

        // Levels that are not supercompressed start on a multiple of the texel block size and 4; supercompressed ones anywhere.
        int alignment = scheme != KtxSupercompression.None ? 1 : (int)Lcm(target.IsBlock ? target.BlockBytes : target.PixelBytes, 4);
        long[] offsets = new long[mips];
        for (int m = mips - 1; m >= 0; m--)
        {
            offsets[m] = Align(end, alignment);
            end = offsets[m] + levels[m].Length;
        }

        byte[] head = new byte[dfdOffset];
        Span<byte> h = head;
        Identifier.CopyTo(h);
        uint[] fields = [(uint)vk, (uint)target.TypeSize, (uint)width, (uint)height, (uint)depth, (uint)layers, (uint)faces, (uint)mips, (uint)scheme,
            (uint)dfdOffset, (uint)dfd.Length, kvd.Length > 0 ? (uint)kvdOffset : 0, (uint)kvd.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[(12 + (i * 4))..], fields[i]);
        }

        if (globalData.Length > 0)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(h[64..], (ulong)sgdOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(h[72..], (ulong)globalData.Length);
        }

        for (int m = 0; m < mips; m++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(h[(80 + (m * 24))..], (ulong)offsets[m]);
            BinaryPrimitives.WriteUInt64LittleEndian(h[(88 + (m * 24))..], (ulong)levels[m].Length);
            BinaryPrimitives.WriteUInt64LittleEndian(h[(96 + (m * 24))..], (ulong)uncompressed[m]);
        }

        output.Write(head);
        output.Write(dfd);
        output.Write(kvd);
        long position = kvdOffset + kvd.Length;
        if (globalData.Length > 0)
        {
            Pad(output, sgdOffset - position);
            output.Write(globalData);
            position = sgdOffset + globalData.Length;
        }

        for (int m = mips - 1; m >= 0; m--)
        {
            Pad(output, offsets[m] - position);
            output.Write(levels[m]);
            position = offsets[m] + levels[m].Length;
        }

        static void Pad(Stream output, long count)
        {
            Span<byte> zeros = stackalloc byte[16];
            zeros.Clear();
            output.Write(zeros[..(int)count]);
        }
    }

    private static long Align(long value, int to) => (value + to - 1) / to * to;

    private static long Lcm(int a, int b)
    {
        int x = a, y = b;
        while (y != 0)
        {
            (x, y) = (y, x % y);
        }

        return (long)a / x * b;
    }
}
