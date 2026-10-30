using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Formats.Extended.Dds;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Dds;

/// <summary>The DDS reader: headers, pixel formats, blocks and surfaces, on files built byte by byte.</summary>
public class DdsReaderTests
{
    private static EgamiContext Context => TestImages.Context;

    [Fact]
    public void ReadsLegacyBgraAndKeepsDetails()
    {
        byte[] file = DdsFiles.Masks(2, 1, DdsFiles.Rgb | DdsFiles.AlphaPixels, 32, 0xFF0000, 0xFF00, 0xFF, 0xFF000000, [1, 2, 3, 4, 5, 6, 7, 8]);
        RasterInfo info = Raster.Probe(file, Context);
        Assert.Equal("DDS", info.Format.Name);
        Assert.Equal(1, info.ImageCount);
        Assert.Equal(Rgba8.Layout, info.Layout);
        using Raster<Rgba8> raster = Assert.IsType<Raster<Rgba8>>(Raster.Load(file, Context));
        Assert.Equal(new Rgba8(3, 2, 1, 4), raster.Row(0)[0]);
        Assert.Equal(new Rgba8(7, 6, 5, 8), raster.Row(0)[1]);
        DdsDetails details = Assert.IsType<DdsDetails>(raster.Metadata.FormatDetails);
        Assert.Equal(87, details.DxgiFormat);
        Assert.Equal("B8G8R8A8_UNORM", details.FormatName);
        Assert.False(details.Dx10Header);
        Assert.False(details.IsSrgb);
        Assert.Equal(DdsDimension.Texture2D, details.Dimension);
        Assert.Empty(raster.Metadata.Warnings);
        Assert.Throws<UnsupportedFormatException>(() => Raster.Probe(file));
    }

    [Fact]
    public void TagsSrgbFormats()
    {
        using Raster raster = Raster.Load(DdsFiles.Dx10(1, 1, 29, [10, 20, 30, 40]), Context);
        Assert.Equal(ColorSpaceHint.Srgb, raster.Metadata.ColorSpace);
        DdsDetails details = Assert.IsType<DdsDetails>(raster.Metadata.FormatDetails);
        Assert.True(details.IsSrgb);
        Assert.True(details.Dx10Header);
        Assert.Equal("R8G8B8A8_UNORM_SRGB", details.FormatName);
    }

    [Fact]
    public void ReadsLegacyMaskFormats()
    {
        using Raster<Bgr565> b565 = Assert.IsType<Raster<Bgr565>>(Raster.Load(DdsFiles.Masks(1, 1, DdsFiles.Rgb, 16, 0xF800, 0x7E0, 0x1F, 0, [0x34, 0x12]), Context));
        Assert.Equal(0x1234, b565.Row(0)[0].Value);
        using Raster<Rgb8> x8 = Assert.IsType<Raster<Rgb8>>(Raster.Load(DdsFiles.Masks(1, 1, DdsFiles.Rgb, 32, 0xFF0000, 0xFF00, 0xFF, 0, [1, 2, 3, 4]), Context));
        Assert.Equal(new Rgb8(3, 2, 1), x8.Row(0)[0]);
        using Raster<Gray8> l8 = Assert.IsType<Raster<Gray8>>(Raster.Load(DdsFiles.Masks(1, 1, DdsFiles.Luminance, 8, 0xFF, 0, 0, 0, [77]), Context));
        Assert.Equal(77, l8.Row(0)[0].L);
        using Raster<GrayAlpha8> a8l8 = Assert.IsType<Raster<GrayAlpha8>>(Raster.Load(DdsFiles.Masks(1, 1, DdsFiles.Luminance | DdsFiles.AlphaPixels, 16, 0xFF, 0, 0, 0xFF00, [77, 99]), Context));
        Assert.Equal(new GrayAlpha8(77, 99), a8l8.Row(0)[0]);
        using Raster<Alpha8> a8 = Assert.IsType<Raster<Alpha8>>(Raster.Load(DdsFiles.Masks(1, 1, DdsFiles.Alpha, 8, 0, 0, 0, 0xFF, [42]), Context));
        Assert.Equal(42, a8.Row(0)[0].A);

        // R3G3B2: any masks scale each field to 8 bits, rounding to nearest.
        using Raster<Rgb8> r332 = Assert.IsType<Raster<Rgb8>>(Raster.Load(DdsFiles.Masks(1, 1, DdsFiles.Rgb, 8, 0xE0, 0x1C, 0x3, 0, [0b101_011_10]), Context));
        Assert.Equal(new Rgb8(182, 109, 170), r332.Row(0)[0]);

        // 10:10:10:2 masks are read with red and blue swapped, as D3DX wrote them.
        uint v = 1023 | (512u << 10) | (0u << 20) | (3u << 30);
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, v);
        using Raster<Rgb10A2> ten = Assert.IsType<Raster<Rgb10A2>>(Raster.Load(DdsFiles.Masks(1, 1, DdsFiles.Rgb | DdsFiles.AlphaPixels, 32, 0x3FF00000, 0xFFC00, 0x3FF, 0xC0000000, bytes), Context));
        Assert.Equal(v, ten.Row(0)[0].Value);
    }

    [Fact]
    public void ReadsPalettes()
    {
        byte[] palette = new byte[1024];
        palette[4] = 200;
        palette[5] = 100;
        palette[6] = 50;
        using Raster<Rgba8> p8 = Assert.IsType<Raster<Rgba8>>(Raster.Load(DdsFiles.Masks(2, 1, DdsFiles.Pal8, 8, 0, 0, 0, 0, [1, 0], palette: palette), Context));
        Assert.Equal(new Rgba8(200, 100, 50, 255), p8.Row(0)[0]);
        Assert.Equal(new Rgba8(0, 0, 0, 255), p8.Row(0)[1]);
        using Raster<Rgba8> a8p8 = Assert.IsType<Raster<Rgba8>>(Raster.Load(DdsFiles.Masks(1, 1, DdsFiles.Pal8 | DdsFiles.AlphaPixels, 16, 0, 0, 0, 0xFF00, [1, 9], palette: palette), Context));
        Assert.Equal(new Rgba8(200, 100, 50, 9), a8p8.Row(0)[0]);
    }

    [Fact]
    public void ReadsSignedAndIntegerFormats()
    {
        using Raster<Rgb8> v8u8 = Assert.IsType<Raster<Rgb8>>(Raster.Load(DdsFiles.Masks(2, 1, DdsFiles.BumpDuDv, 16, 0xFF, 0xFF00, 0, 0, [0x81, 0x7F, 0, 0x80]), Context));
        Assert.Equal(new Rgb8(0, 255, 0), v8u8.Row(0)[0]);
        Assert.Equal(new Rgb8(128, 0, 0), v8u8.Row(0)[1]);
        using Raster<Gray8> sint = Assert.IsType<Raster<Gray8>>(Raster.Load(DdsFiles.Dx10(2, 1, 64, [0x80, 0x7F]), Context));
        Assert.Equal([0, 255], [sint.Row(0)[0].L, sint.Row(0)[1].L]);
        byte[] u32 = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(u32, 70000);
        using Raster<GrayF32> r32 = Assert.IsType<Raster<GrayF32>>(Raster.Load(DdsFiles.Dx10(1, 1, 42, u32), Context));
        Assert.Equal(70000f, r32.Row(0)[0].L);
        byte[] d24 = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(d24, 0xAB000000 | 0xFFFFFF);
        using Raster<GrayF32> depth = Assert.IsType<Raster<GrayF32>>(Raster.Load(DdsFiles.Dx10(1, 1, 45, d24), Context));
        Assert.Equal(1f, depth.Row(0)[0].L);
        using Raster<Gray8> stencil = Assert.IsType<Raster<Gray8>>(Raster.Load(DdsFiles.Dx10(1, 1, 47, d24), Context));
        Assert.Equal(0xAB, stencil.Row(0)[0].L);
        using Raster<Gray8> bits = Assert.IsType<Raster<Gray8>>(Raster.Load(DdsFiles.Dx10(10, 1, 66, [0b1010_0000, 0b0100_0000]), Context));
        Assert.Equal([255, 0, 255, 0, 0, 0, 0, 0, 0, 255], bits.Row(0).ToArray().Select(p => (int)p.L));
    }

    [Fact]
    public void ReadsPackedFloats()
    {
        byte[] data = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (15u << 6) | ((15u << 6) << 11) | ((15u << 5) << 22));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 256u | (128u << 9) | (0u << 18) | (16u << 27));
        using Raster<RgbaF16> r11 = Assert.IsType<Raster<RgbaF16>>(Raster.Load(DdsFiles.Dx10(1, 1, 26, data.AsSpan(0, 4)), Context));
        Assert.Equal(new Half[] { Half.One, Half.One, Half.One, Half.One }, Channels(r11.Row(0)[0]));
        using Raster<RgbaF16> e5 = Assert.IsType<Raster<RgbaF16>>(Raster.Load(DdsFiles.Dx10(1, 1, 67, data.AsSpan(4)), Context));
        Assert.Equal(new Half[] { Half.One, (Half)0.5f, Half.Zero, Half.One }, Channels(e5.Row(0)[0]));
    }

    [Fact]
    public void ReadsYuv()
    {
        // Studio-range white and black.
        using Raster<Rgb8> yuy2 = Assert.IsType<Raster<Rgb8>>(Raster.Load(DdsFiles.FourCCFile(2, 1, "YUY2", [235, 128, 16, 128]), Context));
        Assert.Equal([new Rgb8(255, 255, 255), new Rgb8(0, 0, 0)], yuy2.Row(0).ToArray());
        using Raster<Rgb8> uyvy = Assert.IsType<Raster<Rgb8>>(Raster.Load(DdsFiles.FourCCFile(2, 1, "UYVY", [128, 235, 128, 16]), Context));
        Assert.Equal([new Rgb8(255, 255, 255), new Rgb8(0, 0, 0)], uyvy.Row(0).ToArray());

        // NV12: a 2 × 2 luma plane, then one interleaved chroma pair.
        using Raster<Rgb8> nv12 = Assert.IsType<Raster<Rgb8>>(Raster.Load(DdsFiles.Dx10(2, 2, 103, [235, 16, 16, 235, 128, 128]), Context));
        Assert.Equal(new Rgb8(255, 255, 255), nv12.Row(1)[1]);
        Assert.Equal(new Rgb8(0, 0, 0), nv12.Row(1)[0]);
    }

    [Fact]
    public void DecodesBc1()
    {
        // Red and blue endpoints; index 2 is two thirds red, index 3 one third.
        byte[] block = [0x00, 0xF8, 0x1F, 0x00, 0b11_10_01_00, 0, 0, 0];
        using Raster<Rgba8> raster = Assert.IsType<Raster<Rgba8>>(Raster.Load(DdsFiles.FourCCFile(4, 4, "DXT1", block), Context));
        Assert.Equal([new Rgba8(255, 0, 0, 255), new Rgba8(0, 0, 255, 255), new Rgba8(170, 0, 85, 255), new Rgba8(85, 0, 170, 255)], raster.Row(0).ToArray());

        // Endpoint 0 at most endpoint 1: three colours and transparent black.
        byte[] three = [0x1F, 0x00, 0x00, 0xF8, 0b11_10_01_00, 0, 0, 0];
        using Raster<Rgba8> transparent = Raster.Load<Rgba8>(DdsFiles.FourCCFile(4, 4, "DXT1", three), Context);
        Assert.Equal([new Rgba8(0, 0, 255, 255), new Rgba8(255, 0, 0, 255), new Rgba8(128, 0, 128, 255), new Rgba8(0, 0, 0, 0)], transparent.Row(0).ToArray());
    }

    [Fact]
    public void DecodesBc2AndBc3Alpha()
    {
        byte[] bc2 = [0x10, 0, 0, 0, 0, 0, 0, 0xF0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0];
        using Raster<Rgba8> two = Raster.Load<Rgba8>(DdsFiles.FourCCFile(4, 4, "DXT3", bc2), Context);
        Assert.Equal(17, two.Row(0)[1].A);
        Assert.Equal(0, two.Row(0)[0].A);
        Assert.Equal(255, two.Row(3)[3].A);

        // Alpha 255 to 0 in 8 steps: index 2 is 6/7 of 255.
        byte[] bc3 = [255, 0, 0b10_001_000, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0];
        using Raster<Rgba8> three = Raster.Load<Rgba8>(DdsFiles.FourCCFile(4, 4, "DXT5", bc3), Context);
        Assert.Equal([255, 0, 219], three.Row(0).ToArray().Take(3).Select(p => (int)p.A));
    }

    [Fact]
    public void UnpremultipliesDxt2()
    {
        // Grey 0x7BEF (123, 125, 123 in 8 bits) at alpha 136.
        byte[] bc2 = [0x88, 0x88, 0x88, 0x88, 0x88, 0x88, 0x88, 0x88, 0xEF, 0x7B, 0xEF, 0x7B, 0, 0, 0, 0];
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(DdsFiles.FourCCFile(4, 4, "DXT2", bc2), Context);
        Assert.Equal(new Rgba8(231, 234, 231, 136), raster.Row(0)[0]);
        Assert.Equal(DdsAlphaMode.Premultiplied, Assert.IsType<DdsDetails>(raster.Metadata.FormatDetails).AlphaMode);
    }

    [Fact]
    public void DecodesBc4AndBc5()
    {
        byte[] unorm = [255, 0, 0b10_001_000, 0, 0, 0, 0, 0];
        using Raster<Gray8> bc4 = Assert.IsType<Raster<Gray8>>(Raster.Load(DdsFiles.FourCCFile(4, 4, "ATI1", unorm), Context));
        Assert.Equal([255, 0, 219], bc4.Row(0).ToArray().Take(3).Select(p => (int)p.L));

        // Signed: 127 and -127 map to 255 and 0; index 2 is (6 × 127 − 127) / 7 mapped from −1..1.
        byte[] snorm = [127, 0x81, 0b10_001_000, 0, 0, 0, 0, 0];
        using Raster<Gray8> bc4s = Raster.Load<Gray8>(DdsFiles.Dx10(4, 4, 81, snorm), Context);
        Assert.Equal([255, 0, 219], bc4s.Row(0).ToArray().Take(3).Select(p => (int)p.L));

        byte[] bc5 = [.. unorm, 128, 128, 0, 0, 0, 0, 0, 0];
        using Raster<Rgb8> normals = Assert.IsType<Raster<Rgb8>>(Raster.Load(DdsFiles.FourCCFile(4, 4, "ATI2", bc5), Context));
        Assert.Equal(new Rgb8(255, 128, 0), normals.Row(0)[0]);
        using Raster<Rgb8> z = Raster.Load<Rgb8>(DdsFiles.Dx10(4, 4, 83, [128, 128, 0, 0, 0, 0, 0, 0, 128, 128, 0, 0, 0, 0, 0, 0]), Context, new DdsReaderOptions { ReconstructNormalZ = true });
        Assert.Equal(new Rgb8(128, 128, 255), z.Row(0)[0]);
    }

    [Fact]
    public void DecodesBc7()
    {
        // Mode 6: one subset, RGBA 7 bits and a p-bit per endpoint, 4-bit indices.
        DdsFiles.BlockBits bits = new DdsFiles.BlockBits().Put(1 << 6, 7);
        foreach (int channel in (int[])[127, 0, 64, 64, 0, 127, 127, 127])
        {
            bits.Put(channel, 7);
        }

        bits.Put(1, 1).Put(0, 1);
        bits.Put(0, 3).Put(15, 4).Put(5, 4);
        byte[] block = bits.ToArray();
        using Raster<Rgba8> raster = Assert.IsType<Raster<Rgba8>>(Raster.Load(DdsFiles.Dx10(4, 4, 98, block), Context));
        Assert.Equal(new Rgba8(255, 129, 1, 255), raster.Row(0)[0]);
        Assert.Equal(new Rgba8(0, 128, 254, 254), raster.Row(0)[1]);
        Assert.Equal(new Rgba8(171, 129, 84, 255), raster.Row(0)[2]);

        // A reserved mode decodes to transparent black.
        using Raster<Rgba8> reserved = Raster.Load<Rgba8>(DdsFiles.Dx10(4, 4, 98, new byte[16]), Context);
        Assert.Equal(default, reserved.Row(2)[2]);
    }

    [Fact]
    public void DecodesBc7WithSubsetsAndRotation()
    {
        // Mode 1, partition 0 (left half subset 0): subset 0 white to black, subset 1 red, shared p-bits 0.
        DdsFiles.BlockBits bits = new DdsFiles.BlockBits().Put(0b10, 2).Put(0, 6);
        foreach (int[] channel in (int[][])[[63, 0, 63, 63], [63, 0, 0, 0], [63, 0, 0, 0]])
        {
            foreach (int v in channel)
            {
                bits.Put(v, 6);
            }
        }

        bits.Put(0, 1).Put(0, 1);
        byte[] block = bits.ToArray();
        using Raster<Rgba8> two = Raster.Load<Rgba8>(DdsFiles.Dx10(4, 4, 98, block), Context);
        Assert.Equal(new Rgba8(253, 253, 253, 255), two.Row(0)[0]);
        Assert.Equal(new Rgba8(253, 0, 0, 255), two.Row(0)[3]);

        // Mode 5 with rotation 1: the alpha interpolation lands in red.
        DdsFiles.BlockBits rot = new DdsFiles.BlockBits().Put(1 << 5, 6).Put(1, 2);
        foreach (int v in (int[])[0, 0, 0, 0, 0, 0])
        {
            rot.Put(v, 7);
        }

        rot.Put(255, 8).Put(255, 8);
        using Raster<Rgba8> rotated = Raster.Load<Rgba8>(DdsFiles.Dx10(4, 4, 98, rot.ToArray()), Context);
        Assert.Equal(new Rgba8(255, 0, 0, 0), rotated.Row(1)[1]);
    }

    [Fact]
    public void DecodesBc6h()
    {
        // Mode 11: one region, 10-bit endpoints, 4-bit indices.
        DdsFiles.BlockBits bits = new DdsFiles.BlockBits().Put(0x03, 5).Put(1023, 10).Put(1023, 10).Put(1023, 10).Put(0, 10).Put(0, 10).Put(0, 10);
        bits.Put(0, 3).Put(15, 4);
        using Raster<RgbaF16> raster = Assert.IsType<Raster<RgbaF16>>(Raster.Load(DdsFiles.Dx10(4, 4, 95, bits.ToArray()), Context));
        Assert.Equal((ushort)0x7BFF, BitConverter.HalfToUInt16Bits(raster.Row(0)[0].R));
        Assert.Equal(Half.Zero, raster.Row(0)[1].G);
        Assert.Equal(Half.One, raster.Row(0)[1].A);

        DdsFiles.BlockBits signed = new DdsFiles.BlockBits().Put(0x03, 5).Put(-511, 10).Put(511, 10).Put(0, 10).Put(0, 10).Put(0, 10).Put(0, 10);
        using Raster<RgbaF16> negative = Raster.Load<RgbaF16>(DdsFiles.Dx10(4, 4, 96, signed.ToArray()), Context);
        Assert.Equal((ushort)0xFBFF, BitConverter.HalfToUInt16Bits(negative.Row(0)[0].R));
        Assert.Equal((ushort)0x7BFF, BitConverter.HalfToUInt16Bits(negative.Row(0)[0].G));

        // A reserved mode decodes to black.
        using Raster<RgbaF16> reserved = Raster.Load<RgbaF16>(DdsFiles.Dx10(4, 4, 95, [0x13, .. new byte[15]]), Context);
        Assert.Equal(Half.Zero, reserved.Row(0)[0].R);
    }

    [Fact]
    public void ClipsBlocksToOddSizes()
    {
        byte[] block = [0x00, 0xF8, 0x1F, 0x00, 0b11_10_01_00, 0b11_10_01_00, 0b11_10_01_00, 0b11_10_01_00];
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(DdsFiles.FourCCFile(5, 3, "DXT1", [.. block, .. block]), Context);
        Assert.Equal((5, 3), (raster.Width, raster.Height));
        Assert.Equal(new Rgba8(255, 0, 0, 255), raster.Row(2)[4]);
    }

    [Fact]
    public void ReachesSurfacesAndMips()
    {
        // Two array slices of 4 × 4 R8 with 3 mips, each surface filled with its own value.
        List<byte> data = [];
        for (int s = 0; s < 2; s++)
        {
            for (int m = 0; m < 3; m++)
            {
                int side = 4 >> m;
                data.AddRange(Enumerable.Repeat((byte)((s * 10) + m), side * side));
            }
        }

        byte[] file = DdsFiles.Dx10(4, 4, 61, data.ToArray(), mips: 3, arraySize: 2);
        Assert.Equal(2, Raster.Probe(file, Context).ImageCount);
        using (Raster<Gray8> first = Raster.Load<Gray8>(file, Context))
        {
            Assert.Equal(0, first.Row(3)[3].L);
        }

        using (Raster<Gray8> picked = Raster.Load<Gray8>(file, Context, new DdsReaderOptions { Surface = 1, MipLevel = 2 }))
        {
            Assert.Equal((1, 12), (picked.Width, (int)picked.Row(0)[0].L));
            DdsDetails details = Assert.IsType<DdsDetails>(picked.Metadata.FormatDetails);
            Assert.Equal((1, 2, 3, 2), (details.Surface, details.MipLevel, details.MipLevels, details.ArraySize));
        }

        using (RasterList all = Raster.LoadAll(file, Context))
        {
            Assert.Equal([0, 10], all.Select(r => (int)((Raster<Gray8>)r).Row(0)[0].L));
        }

        using (RasterList mips = Raster.LoadAll(file, Context, new DdsReaderOptions { IncludeMipLevels = true }))
        {
            Assert.Equal([0, 1, 2, 10, 11, 12], mips.Select(r => (int)((Raster<Gray8>)r).Row(0)[0].L));
            Assert.Equal([4, 2, 1, 4, 2, 1], mips.Select(r => r.Width));
        }

        using (Raster<Gray8> small = Raster.Load<Gray8>(file, Context, new ReaderOptions { MinimumSize = new Size(2, 2) }))
        {
            Assert.Equal((2, 1), (small.Width, (int)small.Row(0)[0].L));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Load(file, Context, new DdsReaderOptions { Surface = 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Load(file, Context, new DdsReaderOptions { MipLevel = 3 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DdsReaderOptions { Surface = -1 });
    }

    [Fact]
    public void ReadsCubesAndVolumes()
    {
        byte[] faces = [.. Enumerable.Range(0, 6).Select(i => (byte)i)];
        byte[] cube = DdsFiles.Masks(1, 1, DdsFiles.Luminance, 8, 0xFF, 0, 0, 0, faces, caps2: 0xFE00);
        using (RasterList all = Raster.LoadAll(cube, Context))
        {
            Assert.Equal([0, 1, 2, 3, 4, 5], all.Select(r => (int)((Raster<Gray8>)r).Row(0)[0].L));
            Assert.True(Assert.IsType<DdsDetails>(all[0].Metadata.FormatDetails).IsCube);
        }

        // A 2 × 2 × 4 volume with 3 mips: 4 slices, then 2, then 1, each slice filled with its mip × 10 + slice.
        List<byte> data = [];
        for (int m = 0; m < 3; m++)
        {
            int side = Math.Max(1, 2 >> m);
            for (int s = 0; s < 4 >> m; s++)
            {
                data.AddRange(Enumerable.Repeat((byte)((m * 10) + s), side * side));
            }
        }

        byte[] volume = DdsFiles.Dx10(2, 2, 61, data.ToArray(), mips: 3, dimension: 4, depth: 4);
        using (RasterList slices = Raster.LoadAll(volume, Context))
        {
            Assert.Equal([0, 1, 2, 3], slices.Select(r => (int)((Raster<Gray8>)r).Row(0)[0].L));
            Assert.Equal(DdsDimension.Texture3D, Assert.IsType<DdsDetails>(slices[0].Metadata.FormatDetails).Dimension);
        }

        using (RasterList withMips = Raster.LoadAll(volume, Context, new DdsReaderOptions { IncludeMipLevels = true }))
        {
            Assert.Equal([0, 10, 20, 1, 2, 11, 3], withMips.Select(r => (int)((Raster<Gray8>)r).Row(0)[0].L));
        }

        using Raster<Gray8> slice = Raster.Load<Gray8>(volume, Context, new DdsReaderOptions { Surface = 3, MipLevel = 1 });
        Assert.Equal(11, slice.Row(0)[0].L);
    }

    [Fact]
    public void RecoversFromShortFiles()
    {
        byte[] file = DdsFiles.Dx10(4, 4, 28, new byte[40]);
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(file, Context);
        Assert.Equal("Dds.RowsMissing", Assert.Single(raster.Metadata.Warnings).Id);
        Assert.Throws<CorruptDataException>(() => Raster.Load(file, Context, new ReaderOptions { Recovery = RecoveryMode.Strict }));

        byte[] lying = DdsFiles.Dx10(2, 2, 61, new byte[5], mips: 9);
        using Raster repaired = Raster.Load(lying, Context);
        Assert.Equal("Dds.MipCountRepaired", Assert.Single(repaired.Metadata.Warnings).Id);
        Assert.Equal(2, Assert.IsType<DdsDetails>(repaired.Metadata.FormatDetails).MipLevels);
        Assert.Throws<CorruptDataException>(() => Raster.Load(lying, Context, new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Fact]
    public void RejectsWhatItCannotRead()
    {
        Assert.Throws<UnsupportedFormatException>(() => Raster.Load(DdsFiles.Dx10(1, 1, 113, [0]), Context));
        Assert.Throws<UnsupportedFormatException>(() => Raster.Load(DdsFiles.FourCCFile(4, 4, "ETC2", new byte[8]), Context));
        Assert.Throws<CorruptDataException>(() => Raster.Load(DdsFiles.Dx10(0, 1, 28, []), Context));
        Assert.Throws<LimitExceededException>(() => Raster.Load(DdsFiles.Dx10(1 << 20, 1 << 20, 28, []), Context));
    }

    [Fact]
    public void StaysInsideTheLimitsOnGarbage()
    {
        Random random = new(7);
        byte[] header = DdsFiles.Dx10(64, 64, 98, []);
        for (int i = 0; i < 200; i++)
        {
            byte[] file = [.. header, .. new byte[random.Next(0, 2048)]];
            random.NextBytes(file.AsSpan(header.Length));
            file[random.Next(8, header.Length)] = (byte)random.Next(256);
            try
            {
                using Raster raster = Raster.Load(file, Context);
            }
            catch (Exception e) when (e is CorruptDataException or UnsupportedFormatException or LimitExceededException or ArgumentOutOfRangeException)
            {
            }
        }
    }

    private static Half[] Channels(RgbaF16 pixel)
    {
        return MemoryMarshal.Cast<RgbaF16, Half>(new[] { pixel }).ToArray();
    }
}
