using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Formats.Extended.Ktx;
using Egami.Formats.Extended.Textures.Blocks;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Ktx;

/// <summary>
/// The KTX 1.1 and KTX 2.0 readers: the corpus KTX-Software made (tools/make-ktx-references.py) against `ktx extract` and the
/// input images, the raw corpus against DDS files of the same bytes, and files built byte by byte for what the tools never write.
/// </summary>
public class KtxReaderTests
{
    private static EgamiContext Context => TestImages.Context;

    public static TheoryData<string, string, int, int> Created => new()
    {
        // File, reference, surface, mip.
        { "rgba8-srgb-mips.ktx2", "rgba8-srgb-mips.png", 0, 0 },
        { "rgba8-srgb-mips.ktx2", "rgba8-srgb-mips-l1.png", 0, 1 },
        { "rgba8-linear.ktx2", "rgba8-linear.png", 0, 0 },
        { "rgba8-zstd.ktx2", "rgba8-zstd.png", 0, 0 },
        { "rgba8-zstd.ktx2", "rgba8-zstd-l1.png", 0, 1 },
        { "rgb8-zlib.ktx2", "rgb8-zlib.png", 0, 0 },
        { "rgb8-zlib.ktx2", "rgb8-zlib-l2.png", 0, 2 },
        { "rgba8-p3.ktx2", "rgba8-p3.png", 0, 0 },
        { "rgba8-bottom-left.ktx2", "rgba8-ktx1.png", 0, 0 },
        { "rgba8-cube.ktx2", "rgba8-cube.png", 0, 0 },
        { "rgba8-cube.ktx2", "rgba8-cube-s5.png", 5, 0 },
        { "rgba8-array.ktx2", "rgba8-array.png", 0, 0 },
        { "rgba8-array.ktx2", "rgba8-array-s2.png", 2, 0 },
        { "rgba8-volume.ktx2", "rgba8-volume.png", 0, 0 },
        { "rgba8-volume.ktx2", "rgba8-volume-s2.png", 2, 0 },
        { "rgba8-1d.ktx2", "rgba8-1d.png", 0, 0 },
        { "rgba8.ktx", "rgba8-ktx1.png", 0, 0 },
        { "rgba8-bottom-left.ktx", "rgba8-ktx1.png", 0, 0 },
        { "rgb8-mips.ktx", "rgb8-mips-ktx1.png", 0, 0 },
        { "r8.ktx", "r8-ktx1.png", 0, 0 },
        { "cube.ktx", "cube-ktx1.png", 0, 0 },
        { "cube.ktx", "cube-ktx1-s5.png", 5, 0 },
        { "array.ktx", "array-ktx1.png", 0, 0 },
        { "array.ktx", "array-ktx1-s2.png", 2, 0 },
    };

    public static TheoryData<string, int> Raw => new()
    {
        // File, the DXGI format of the same bytes.
        { "raw-bc1-rgba", 71 },
        { "raw-bc2", 74 },
        { "raw-bc3-srgb", 78 },
        { "raw-bc4", 80 },
        { "raw-bc4-snorm", 81 },
        { "raw-bc5", 83 },
        { "raw-bc6h-ufloat", 95 },
        { "raw-bc6h-sfloat", 96 },
        { "raw-bc7", 98 },
        { "raw-bc7-zstd", 99 },
        { "raw-bgra8", 87 },
        { "raw-r5g6b5", 85 },
        { "raw-a2b10g10r10", 24 },
        { "raw-rgba16", 11 },
        { "raw-rgba16f", 10 },
        { "raw-rgba32f", 2 },
        { "raw-b10g11r11", 26 },
        { "raw-e5b9g9r9", 67 },
    };

    public static TheoryData<string, string> Blocks => new()
    {
        // File, the reference decode: astcenc for ASTC, etcpak or Compressonator for ETC.
        { "astc-4x4.ktx2", "astc-4x4.png" },
        { "astc-5x4.ktx2", "astc-5x4.png" },
        { "astc-5x5.ktx2", "astc-5x5.png" },
        { "astc-5x5.ktx", "astc-5x5.png" },
        { "astc-6x5.ktx2", "astc-6x5.png" },
        { "astc-6x6.ktx2", "astc-6x6.png" },
        { "astc-8x5.ktx2", "astc-8x5.png" },
        { "astc-8x6.ktx2", "astc-8x6.png" },
        { "astc-8x8.ktx2", "astc-8x8.png" },
        { "astc-10x5.ktx2", "astc-10x5.png" },
        { "astc-10x6.ktx2", "astc-10x6.png" },
        { "astc-10x8.ktx2", "astc-10x8.png" },
        { "astc-10x10.ktx2", "astc-10x10.png" },
        { "astc-12x10.ktx2", "astc-12x10.png" },
        { "astc-12x12.ktx2", "astc-12x12.png" },
        { "astc-6x6-srgb.ktx2", "astc-6x6-srgb.png" },
        { "astc-12x10-srgb.ktx2", "astc-12x10-srgb.png" },
        { "astc-4x4-mutated.ktx2", "astc-4x4-mutated.png" },
        { "astc-8x5-mutated.ktx2", "astc-8x5-mutated.png" },
        { "etc1.ktx", "etc1.png" },
        { "etc2-rgb.ktx", "etc2-rgb.png" },
        { "etc2-rgb.ktx2", "etc2-rgb.png" },
        { "etc2-a1.ktx", "etc2-a1.png" },
        { "etc2-a1.ktx2", "etc2-a1.png" },
        { "etc2-rgba.ktx", "etc2-rgba.png" },
        { "etc2-rgba.ktx2", "etc2-rgba.png" },
        { "etc2-random.ktx2", "etc2-random.png" },
    };

    [Theory]
    [MemberData(nameof(Blocks))]
    public void DecodesEtcAndAstcAsTheReferenceDecoders(string file, string reference)
    {
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(KtxFiles.Reference(reference));
        using Raster<Rgba8> actual = Raster.Load<Rgba8>(KtxFiles.Corpus(file), Context);
        TestImages.AssertEqual(expected, actual);
        Assert.Empty(actual.Metadata.Warnings);
    }

    [Theory]
    [InlineData("eac-r11.ktx", "eac-r11.png", 1)]
    [InlineData("eac-rg11.ktx", "eac-rg11.png", 2)]
    [InlineData("eac-rg11.ktx2", "eac-rg11.png", 2)]
    public void DecodesEacAsEtcpak(string file, string reference, int channels)
    {
        // etcpak writes the top 8 of the 11 bits; Egami widens the 11 bits to 16 by replication, so its top 8 bits are those.
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(KtxFiles.Reference(reference));
        using Raster texture = Raster.Load(KtxFiles.Corpus(file), Context);
        Assert.True(channels == 1 ? texture is Raster<Gray16> : texture is Raster<Rgb16>);
        using Raster<Rgba16> actual = texture.As<Rgba16>();
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Rgba8 e = expected.Row(y)[x];
                Rgba16 a = actual.Row(y)[x];
                Assert.Equal(e.R, a.R >> 8);
                Assert.Equal(channels == 1 ? a.R >> 8 : e.G, a.G >> 8);
            }
        }
    }

    [Theory]
    [InlineData("astc-6x6-hdr.ktx2")]
    [InlineData("astc-8x8-hdr-mutated.ktx2")]
    public void DecodesAstcHdrAsAstcenc(string file)
    {
        using Raster<RgbaF16> actual = Raster.Load<RgbaF16>(KtxFiles.Corpus(file), Context);
        byte[] expected = KtxFiles.Gunzip(file.Replace(".ktx2", ".f16.gz", StringComparison.Ordinal));
        for (int y = 0; y < actual.Height; y++)
        {
            Assert.Equal(expected.AsSpan(y * actual.Width * 8, actual.Width * 8), MemoryMarshal.AsBytes(actual.Row(y)));
        }
    }

    [Theory]
    [InlineData("eac-r11-signed.ktx", 1)]
    [InlineData("eac-rg11-signed.ktx", 2)]
    [InlineData("eac-rg11-signed.ktx2", 2)]
    [InlineData("etc2-srgb.ktx", 3)]
    [InlineData("etc2-srgb.ktx2", 3)]
    public void ReadsTheFormatsWithoutAReferenceDecoder(string file, int channels)
    {
        using Raster texture = Raster.Load(KtxFiles.Corpus(file), Context);
        Assert.True(channels switch { 1 => texture is Raster<Gray16>, 2 => texture is Raster<Rgb16>, _ => texture is Raster<Rgb8> });
        Assert.Equal((37, 23), (texture.Width, texture.Height));
        // EtcTool writes SRGB8 under the plain RGB8 internal format, so only the KTX 2 copy says sRGB.
        Assert.Equal(file == "etc2-srgb.ktx2", texture.Metadata.ColorSpace == ColorSpaceHint.Srgb);

        // The same blocks in the other container read the same.
        string other = file.EndsWith(".ktx2", StringComparison.Ordinal) ? file[..^1] : file + "2";
        if (File.Exists(KtxFiles.Corpus(other)))
        {
            using Raster twin = Raster.Load(KtxFiles.Corpus(other), Context);
            Assert.Equal(KtxFiles.Floats(twin), KtxFiles.Floats(texture));
        }
    }

    [Theory]
    [InlineData(0x80, 0, 0, 7, 673)] // −128 reads as −127: −1016 + 14 = −1002.
    [InlineData(0x81, 15, 0, 3, 0)] // −1016 − 15 × 15 × 8 clamps to −1023, which is −1.
    [InlineData(0x7F, 15, 0, 7, 65535)] // 1016 + 14 × 15 × 8 clamps to 1023, which is 1.
    [InlineData(0x00, 0, 0, 4, 32832)] // Multiplier 0 adds the modifier unscaled: 0 + 2.
    [InlineData(0x00, 1, 0, 0, 31999)] // 0 − 3 × 8 = −24.
    public void DecodesSignedEacAsTheSpecificationSays(int baseCodeword, int multiplier, int table, int index, int expected)
    {
        ulong bits = ((ulong)baseCodeword << 56) | ((ulong)multiplier << 52) | ((ulong)table << 48);
        for (int k = 0; k < 16; k++)
        {
            bits |= (ulong)index << (45 - (3 * k));
        }

        byte[] block = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(block, bits);
        ushort[] values = new ushort[16];
        Etc.DecodeEac11(block, values, 1, signed: true);
        Assert.All(values, v => Assert.Equal(expected, v));
    }

    [Theory]
    [MemberData(nameof(Created))]
    public void MatchesKtxSoftware(string file, string reference, int surface, int mip)
    {
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(KtxFiles.Reference(reference));
        using Raster<Rgba8> actual = Raster.Load<Rgba8>(KtxFiles.Corpus(file), Context, new KtxReaderOptions { Surface = surface, MipLevel = mip });
        TestImages.AssertEqual(expected, actual);
        KtxDetails details = Assert.IsType<KtxDetails>(actual.Metadata.FormatDetails);
        Assert.Equal((surface, mip), (details.Surface, details.MipLevel));
        Assert.Empty(actual.Metadata.Warnings);

        // Strict, from a stream that cannot seek, and row by row, the same pixels.
        using Raster<Rgba8> strict = Raster.Load<Rgba8>(new ForwardOnlyStream(File.ReadAllBytes(KtxFiles.Corpus(file))), Context,
            new KtxReaderOptions { Surface = surface, MipLevel = mip, Recovery = RecoveryMode.Strict });
        TestImages.AssertEqual(expected, strict);
    }

    [Theory]
    [MemberData(nameof(Raw))]
    public void ReadsBlocksAndPackedFormatsAsDdsDoes(string name, int dxgi)
    {
        byte[] ktx = File.ReadAllBytes(KtxFiles.Corpus(name + ".ktx2"));
        using Raster texture = Raster.Load(ktx, Context);
        using Raster dds = Raster.Load(KtxFiles.Dds(37, 23, dxgi, KtxFiles.RawBytes(name)), Context);
        Assert.Equal(dds.Layout, texture.Layout);
        Assert.Equal(KtxFiles.Floats(dds), KtxFiles.Floats(texture));
        Assert.Equal(dds.Metadata.ColorSpace == ColorSpaceHint.Srgb, texture.Metadata.ColorSpace == ColorSpaceHint.Srgb);
        Assert.Equal(Raster.Probe(ktx, Context).Layout, texture.Layout);
    }

    [Fact]
    public void ReadsBc1WithoutAlphaOpaque()
    {
        using Raster<Rgba8> texture = Raster.Load<Rgba8>(KtxFiles.Corpus("raw-bc1-rgb.ktx2"), Context);
        using Raster<Rgba8> dds = Raster.Load<Rgba8>(KtxFiles.Dds(37, 23, 71, KtxFiles.RawBytes("raw-bc1-rgb")), Context);
        bool punchThrough = false;
        for (int y = 0; y < dds.Height; y++)
        {
            for (int x = 0; x < dds.Width; x++)
            {
                Rgba8 p = dds.Row(y)[x];
                punchThrough |= p.A == 0;
                Assert.Equal(p with { A = 255 }, texture.Row(y)[x]);
            }
        }

        Assert.True(punchThrough);
        Assert.Equal("BC1_RGB_UNORM_BLOCK", ((KtxDetails)texture.Metadata.FormatDetails!).FormatName);
    }

    [Fact]
    public void KeepsDetails()
    {
        using Raster raster = Raster.Load(KtxFiles.Corpus("rgba8-zstd.ktx2"), Context);
        Assert.Equal("KTX2", raster.Metadata.FormatName);
        KtxDetails details = Assert.IsType<KtxDetails>(raster.Metadata.FormatDetails);
        Assert.Equal((43, "R8G8B8A8_SRGB", 0), (details.VkFormat, details.FormatName, details.GlInternalFormat));
        Assert.Equal(KtxSupercompression.Zstandard, details.Supercompression);
        Assert.Equal((KtxDimension.Texture2D, false, 1, 1, 1, 6), (details.Dimension, details.IsCube, details.Layers, details.Faces, details.Depth, details.MipLevels));
        Assert.True(details.IsSrgb);
        Assert.Equal((KtxAlphaMode.Straight, "rd", "rgba"), (details.AlphaMode, details.Orientation, details.Swizzle));
        Assert.StartsWith("ktx create", details.KeyValues["KTXwriter"], StringComparison.Ordinal);
        Assert.False(details.KeyValues.ContainsKey("KTXorientation"));
        Assert.Equal(ColorSpaceHint.Srgb, raster.Metadata.ColorSpace);
        Assert.Equal(Rgba8.Layout, raster.Layout);

        KtxDetails copy = Assert.IsType<KtxDetails>(details.Copy());
        Assert.Equal((details.FormatName, details.MipLevels, details.KeyValues), (copy.FormatName, copy.MipLevels, copy.KeyValues));

        using Raster ktx1 = Raster.Load(KtxFiles.Corpus("cube.ktx"), Context);
        KtxDetails cube = Assert.IsType<KtxDetails>(ktx1.Metadata.FormatDetails);
        Assert.Equal("KTX", ktx1.Metadata.FormatName);
        Assert.Equal((0x8C43, 43, true, 6), (cube.GlInternalFormat, cube.VkFormat, cube.IsCube, cube.Faces));
    }

    [Fact]
    public void TagsColourSpaces()
    {
        using Raster linear = Raster.Load(KtxFiles.Corpus("rgba8-linear.ktx2"), Context);
        Assert.Equal(ColorSpaceHint.Linear, linear.Metadata.ColorSpace);
        using Raster p3 = Raster.Load(KtxFiles.Corpus("rgba8-p3.ktx2"), Context);
        Assert.Equal(new ColorCodePoints(12, 13, 0, true), p3.Metadata.CodePoints);
        Assert.Equal(ColorProfile.DisplayP3, p3.Metadata.GetColorProfile());
        using Raster gray = Raster.Load(KtxFiles.Corpus("r8-srgb.ktx2"), Context);
        Assert.Equal(Gray8.Layout, gray.Layout);
        Assert.Equal(ColorSpaceHint.Srgb, gray.Metadata.ColorSpace);
    }

    [Fact]
    public void ReadsRedAsGray()
    {
        // ktx extract writes R8 as red and R8G8 as red-green; Egami loads R8 as gray and R8G8 as RGB with blue 0, as DDS does.
        foreach (string name in (string[])["r8-srgb", "r8-swizzle"])
        {
            using Raster<Rgba8> expected = Raster.Load<Rgba8>(KtxFiles.Reference(name + ".png"));
            using Raster<Gray8> gray = Assert.IsType<Raster<Gray8>>(Raster.Load(KtxFiles.Corpus(name + ".ktx2"), Context));
            for (int y = 0; y < gray.Height; y++)
            {
                for (int x = 0; x < gray.Width; x++)
                {
                    Assert.Equal(expected.Row(y)[x].R, gray.Row(y)[x].L);
                }
            }
        }

        using Raster<Rgb8> reference = Raster.Load<Rgb8>(KtxFiles.Reference("rg8-swizzle.png"));
        using Raster<Rgb8> stored = Raster.Load<Rgb8>(KtxFiles.Corpus("rg8-swizzle.ktx2"), Context, new KtxReaderOptions { KeepStoredLayout = true });
        TestImages.AssertEqual(reference, stored);
    }

    [Fact]
    public void AppliesTheSwizzle()
    {
        // rrr1 on R8 is gray; rrrg on R8G8 is gray with alpha in a four-channel raster.
        using Raster gray = Raster.Load(KtxFiles.Corpus("r8-swizzle.ktx2"), Context);
        Assert.Equal(Gray8.Layout, gray.Layout);
        using Raster<Rgba8> swizzled = Assert.IsType<Raster<Rgba8>>(Raster.Load(KtxFiles.Corpus("rg8-swizzle.ktx2"), Context));
        using Raster<Rgb8> stored = Assert.IsType<Raster<Rgb8>>(Raster.Load(KtxFiles.Corpus("rg8-swizzle.ktx2"), Context, new KtxReaderOptions { KeepStoredLayout = true }));
        for (int x = 0; x < stored.Width; x++)
        {
            Rgb8 s = stored.Row(3)[x];
            Assert.Equal(new Rgba8(s.R, s.R, s.R, s.G), swizzled.Row(3)[x]);
        }

        Assert.Equal("rrrg", ((KtxDetails)swizzled.Metadata.FormatDetails!).Swizzle);
    }

    [Fact]
    public void TurnsBottomUpTexturesUpright()
    {
        foreach (string file in (string[])["rgba8-bottom-left.ktx2", "rgba8-bottom-left.ktx"])
        {
            using Raster<Rgba8> upright = Raster.Load<Rgba8>(KtxFiles.Corpus(file), Context);
            using Raster<Rgba8> stored = Raster.Load<Rgba8>(KtxFiles.Corpus(file), Context, new KtxReaderOptions { KeepStoredLayout = true });
            Assert.Equal("ru", ((KtxDetails)upright.Metadata.FormatDetails!).Orientation);
            for (int y = 0; y < upright.Height; y++)
            {
                Assert.True(upright.Row(y).SequenceEqual(stored.Row(upright.Height - 1 - y)), $"{file}: row {y}");
            }

            Assert.Equal(RasterAccess.Buffered, Raster.Probe(KtxFiles.Corpus(file), Context).Access);
            Assert.Equal(RasterAccess.Streamed, Raster.Probe(KtxFiles.Corpus(file), Context, new KtxReaderOptions { KeepStoredLayout = true }).Access);
        }
    }

    [Fact]
    public void ReachesSurfacesAndMips()
    {
        RasterInfo info = Raster.Probe(KtxFiles.Corpus("rgba8-cube.ktx2"), Context);
        Assert.Equal((16, 16, 6), (info.Width, info.Height, info.ImageCount));
        using RasterList faces = Raster.LoadAll(KtxFiles.Corpus("rgba8-cube.ktx2"), Context);
        Assert.Equal(6, faces.Count);

        // Every mip after its surface; a thumbnail reads the smallest mip that still covers the size.
        using RasterList all = Raster.LoadAll(KtxFiles.Corpus("rgba8-srgb-mips.ktx2"), Context, new KtxReaderOptions { IncludeMipLevels = true });
        Assert.Equal([37, 18, 9, 4, 2, 1], all.Select(r => r.Width));
        using Raster thumbnail = Raster.Load(KtxFiles.Corpus("rgba8-zstd.ktx2"), Context, new ReaderOptions { MinimumSize = new Size(8, 5) });
        Assert.Equal((9, 5), (thumbnail.Width, thumbnail.Height));

        // A volume's depth slices are its surfaces; mip 1 has half as many.
        using RasterList volume = Raster.LoadAll(KtxFiles.Corpus("rgba8-volume.ktx2"), Context);
        Assert.Equal(3, volume.Count);

        Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Load(KtxFiles.Corpus("rgba8-cube.ktx2"), Context, new KtxReaderOptions { Surface = 6 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Load(KtxFiles.Corpus("rgba8-cube.ktx2"), Context, new KtxReaderOptions { MipLevel = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new KtxReaderOptions { Surface = -1 });
    }

    [Theory]
    [InlineData("unsupported-uastc.ktx2", "UASTC")]
    [InlineData("unsupported-etc1s.ktx2", "ETC1S")]
    public void RefusesFormatsNotSupportedYet(string file, string family)
    {
        UnsupportedFormatException e = Assert.Throws<UnsupportedFormatException>(() => Raster.Probe(KtxFiles.Corpus(file), Context));
        Assert.Contains(family, e.Message, StringComparison.Ordinal);
        Assert.Contains("not supported yet", e.Message, StringComparison.Ordinal);
        Assert.Throws<UnsupportedFormatException>(() => Raster.Load(KtxFiles.Corpus(file), Context));
    }

    [Fact]
    public void RefusesThreeDimensionalAstcAndPvrtc()
    {
        Assert.Contains("3D ASTC", Assert.Throws<UnsupportedFormatException>(() => Raster.Load(KtxFiles.Ktx2(1000288000, 4, 4, [new byte[16]]), Context)).Message, StringComparison.Ordinal);
        Assert.Contains("PVRTC", Assert.Throws<UnsupportedFormatException>(() => Raster.Load(KtxFiles.Ktx2(1000054000, 4, 4, [new byte[8]]), Context)).Message, StringComparison.Ordinal);
        Assert.Contains("3D ASTC", Assert.Throws<UnsupportedFormatException>(() => Raster.Load(KtxFiles.Ktx1(0, 1, 0, 0x93C0, 4, 4, [new byte[16]]), Context)).Message, StringComparison.Ordinal);
        Assert.Contains("PVRTC", Assert.Throws<UnsupportedFormatException>(() => Raster.Load(KtxFiles.Ktx1(0, 1, 0, 0x8C00, 4, 4, [new byte[8]]), Context)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsBothByteOrders()
    {
        // RGBA16, 2 × 1: the big-endian file swaps each 16-bit value as glTypeSize 2 says.
        ushort[] values = [1, 2, 3, 0xFFFF, 0x1234, 0x5678, 0x9ABC, 0xDEF0];
        byte[] little = new byte[16];
        byte[] big = new byte[16];
        for (int i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(little.AsSpan(i * 2), values[i]);
            BinaryPrimitives.WriteUInt16BigEndian(big.AsSpan(i * 2), values[i]);
        }

        using Raster<Rgba16> le = Assert.IsType<Raster<Rgba16>>(Raster.Load(KtxFiles.Ktx1(0x1403, 2, 0x1908, 0x805B, 2, 1, [little]), Context));
        using Raster<Rgba16> be = Assert.IsType<Raster<Rgba16>>(Raster.Load(KtxFiles.Ktx1(0x1403, 2, 0x1908, 0x805B, 2, 1, [big], bigEndian: true,
            keyValues: [("KTXwriter", "Egami tests")]), Context));
        Assert.Equal(new Rgba16(0x1234, 0x5678, 0x9ABC, 0xDEF0), le.Row(0)[1]);
        TestImages.AssertEqual(le, be);
        Assert.Equal("Egami tests", ((KtxDetails)be.Metadata.FormatDetails!).KeyValues["KTXwriter"]);
    }

    [Fact]
    public void SkipsKtx1RowPadding()
    {
        // RGB8, 3 × 2: each 9-byte row is padded to 12; the image is 24 bytes.
        byte[] level = [1, 2, 3, 4, 5, 6, 7, 8, 9, 0, 0, 0, 10, 11, 12, 13, 14, 15, 16, 17, 18, 0, 0, 0];
        using Raster<Rgb8> raster = Assert.IsType<Raster<Rgb8>>(Raster.Load(KtxFiles.Ktx1(0x1401, 1, 0x1907, 0x8051, 3, 2, [level]), Context));
        Assert.Equal(new Rgb8(7, 8, 9), raster.Row(0)[2]);
        Assert.Equal(new Rgb8(10, 11, 12), raster.Row(1)[0]);

        // BGR is read in its own order, and S=r,T=u turns the rows over.
        using Raster<Rgb8> bgr = Assert.IsType<Raster<Rgb8>>(Raster.Load(KtxFiles.Ktx1(0x1401, 1, 0x80E0, 0x8051, 3, 2, [level],
            keyValues: [("KTXorientation", "S=r,T=u")]), Context));
        Assert.Equal(new Rgb8(12, 11, 10), bgr.Row(0)[0]);
        Assert.Equal(new Rgb8(3, 2, 1), bgr.Row(1)[0]);
    }

    [Fact]
    public void ReadsKtx1BlocksAsDdsDoes()
    {
        byte[] blocks = KtxFiles.RawBytes("raw-bc7");
        using Raster ktx = Raster.Load(KtxFiles.Ktx1(0, 1, 0, 0x8E8C, 37, 23, [blocks]), Context);
        using Raster dds = Raster.Load(KtxFiles.Dds(37, 23, 98, blocks), Context);
        Assert.Equal(KtxFiles.Floats(dds), KtxFiles.Floats(ktx));
    }

    [Fact]
    public void UnpremultipliesWhenTheDescriptorSays()
    {
        using Raster<Rgba8> raster = Assert.IsType<Raster<Rgba8>>(Raster.Load(KtxFiles.Ktx2(37, 1, 1, [[50, 25, 0, 100]], flags: 1), Context));
        Assert.Equal(new Rgba8(128, 64, 0, 100), raster.Row(0)[0]);
        Assert.Equal(KtxAlphaMode.Premultiplied, ((KtxDetails)raster.Metadata.FormatDetails!).AlphaMode);
    }

    [Fact]
    public void CountsBlockRowsOfHugeTexturesWithoutOverflow()
    {
        // A fuzz finding: a height near 2^31 overflowed the count of block rows, and the plan threw ArgumentException.
        byte[] file = KtxFiles.Ktx2(147, 37, int.MaxValue, [new byte[80]]);
        Exception? e = Record.Exception(() => Raster.Probe(file, Context));
        Assert.True(e is null or EgamiException, e?.ToString());
        e = Record.Exception(() => Raster.Load(file, Context).Dispose());
        Assert.True(e is null or EgamiException, e?.ToString());
    }

    [Fact]
    public void RecoversFromShortAndDamagedFiles()
    {
        byte[] file = File.ReadAllBytes(KtxFiles.Corpus("rgba8.ktx"));
        byte[] cut = file[..(file.Length - 200)];
        using Raster lenient = Raster.Load(cut, Context);
        Assert.Contains(lenient.Metadata.Warnings, w => w.Id == "Ktx.RowsMissing");
        Assert.Throws<CorruptDataException>(() => Raster.Load(cut, Context, new ReaderOptions { Recovery = RecoveryMode.Strict }));

        // A wrong imageSize word is noted; the format's size is used.
        byte[] wrong = KtxFiles.Ktx1(0x1401, 1, 0x1908, 0x8058, 1, 1, [[1, 2, 3, 4]], imageSize: 8);
        using Raster sized = Raster.Load(wrong, Context);
        Assert.Contains(sized.Metadata.Warnings, w => w.Id == "Ktx.ImageSizeWrong");
        Assert.Throws<CorruptDataException>(() => Raster.Load(wrong, Context, new ReaderOptions { Recovery = RecoveryMode.Strict }));

        // A Zstandard level that does not inflate reads as black, or throws when strict.
        byte[] zstd = File.ReadAllBytes(KtxFiles.Corpus("rgba8-zstd.ktx2"));
        long level0 = BinaryPrimitives.ReadInt64LittleEndian(zstd.AsSpan(80));
        zstd[(int)level0 + 10] ^= 0xFF;
        zstd[(int)level0 + 11] ^= 0xFF;
        Assert.Throws<CorruptDataException>(() => Raster.Load(zstd, Context, new ReaderOptions { Recovery = RecoveryMode.Strict }));

        // Bad identifiers and impossible counts are corrupt.
        byte[] faces = KtxFiles.Ktx2(37, 1, 1, [new byte[4]], faces: 3);
        Assert.Throws<CorruptDataException>(() => Raster.Load(faces, Context));

        // A level index that points back into the header is corrupt (found by fuzzing).
        byte[] back = KtxFiles.Ktx2(37, 1, 1, [[1, 2, 3, 4]]);
        BinaryPrimitives.WriteUInt64LittleEndian(back.AsSpan(80), 16);
        Assert.Throws<CorruptDataException>(() => Raster.Load(back, Context));

        // A zlib level whose stream breaks so ZLibStream throws an IOException reads as black, or throws when strict (found by
        // fuzzing: the bytes below land in a small mip of rgb8-zlib.ktx2).
        byte[] zlib = File.ReadAllBytes(KtxFiles.Corpus("rgb8-zlib.ktx2"));
        new byte[] { 0x7D, 0xDA, 0x35, 0x3C, 0x50, 0x7F, 0xA4, 0xB3, 0x6A, 0x78, 0x4F, 0xE4 }.CopyTo(zlib, 395);
        using (RasterList levels = Raster.LoadAll(zlib, Context, new KtxReaderOptions { IncludeMipLevels = true }))
        {
            Assert.Contains(levels.SelectMany(r => r.Metadata.Warnings), w => w.Id == "Ktx2.LevelDamaged");
        }

        Assert.Throws<CorruptDataException>(() => Raster.LoadAll(zlib, Context, new KtxReaderOptions { IncludeMipLevels = true, Recovery = RecoveryMode.Strict }));
    }

    [Fact]
    public void IgnoresBadOrientationAndSwizzle()
    {
        using Raster raster = Raster.Load(KtxFiles.Ktx2(37, 1, 1, [[1, 2, 3, 4]], keyValues: [("KTXorientation", "xx"), ("KTXswizzle", "rgbz")]), Context);
        KtxDetails details = (KtxDetails)raster.Metadata.FormatDetails!;
        Assert.Equal(("rd", "rgba"), (details.Orientation, details.Swizzle));
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Ktx2.OrientationIgnored");
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Ktx2.SwizzleIgnored");
    }
}
