using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;

using Egami.Colors;
using Egami.Formats.Extended.Dds;
using Egami.Formats.Extended.Ktx;
using Egami.Formats.Extended.Textures;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Ktx;

/// <summary>The KTX 2 writer: every format, the descriptor, mips, arrays, cube maps, supercompression and the defaults.</summary>
public class Ktx2WriterTests
{
    private static readonly Lazy<JsonElement> s_written = new(() => JsonDocument.Parse(File.ReadAllBytes(KtxFiles.Reference("written.json"))).RootElement);

    private static EgamiContext Context => TestImages.Context;

    public static TheoryData<KtxTextureFormat> Uncompressed =>
    [
        KtxTextureFormat.Rgba8, KtxTextureFormat.Bgra8, KtxTextureFormat.Rgb8, KtxTextureFormat.R8, KtxTextureFormat.Rg8, KtxTextureFormat.R16,
        KtxTextureFormat.Rg16, KtxTextureFormat.Rgb16, KtxTextureFormat.Rgba16, KtxTextureFormat.RgbaF16, KtxTextureFormat.R32F, KtxTextureFormat.RgbF32,
        KtxTextureFormat.RgbaF32, KtxTextureFormat.Rgb10A2, KtxTextureFormat.R11G11B10F, KtxTextureFormat.Rgb9E5, KtxTextureFormat.Bgr565,
        KtxTextureFormat.Bgra5551, KtxTextureFormat.Bgra4444, KtxTextureFormat.R8UInt, KtxTextureFormat.R8SInt, KtxTextureFormat.Rg8UInt,
        KtxTextureFormat.Rg8SInt, KtxTextureFormat.Rgba8UInt, KtxTextureFormat.Rgba8SInt, KtxTextureFormat.R16UInt, KtxTextureFormat.R16SInt,
        KtxTextureFormat.Rg16UInt, KtxTextureFormat.Rg16SInt, KtxTextureFormat.Rgba16UInt, KtxTextureFormat.Rgba16SInt, KtxTextureFormat.D16,
        KtxTextureFormat.D32F,
    ];

    /// <summary>Every block format with the PSNR its 37 × 23 photo keeps at effort 4, over the channels it stores.</summary>
    public static TheoryData<KtxTextureFormat, double> Blocks => new()
    {
        { KtxTextureFormat.Bc1, 33 }, { KtxTextureFormat.Bc2, 33 }, { KtxTextureFormat.Bc3, 34 }, { KtxTextureFormat.Bc4, 45 },
        { KtxTextureFormat.Bc5, 42 }, { KtxTextureFormat.Bc7, 38 }, { KtxTextureFormat.Etc1, 32 }, { KtxTextureFormat.Etc2Rgb, 32 },
        { KtxTextureFormat.Etc2RgbA1, 30 }, { KtxTextureFormat.Etc2Rgba, 32 }, { KtxTextureFormat.EacR11, 45 }, { KtxTextureFormat.EacR11Signed, 45 },
        { KtxTextureFormat.EacRg11, 42 }, { KtxTextureFormat.EacRg11Signed, 42 }, { KtxTextureFormat.Astc4x4, 38 }, { KtxTextureFormat.Astc5x4, 36 },
        { KtxTextureFormat.Astc5x5, 34 }, { KtxTextureFormat.Astc6x5, 33 }, { KtxTextureFormat.Astc6x6, 32 }, { KtxTextureFormat.Astc8x5, 31 },
        { KtxTextureFormat.Astc8x6, 30 }, { KtxTextureFormat.Astc8x8, 28 }, { KtxTextureFormat.Astc10x5, 29 }, { KtxTextureFormat.Astc10x6, 28 },
        { KtxTextureFormat.Astc10x8, 27 }, { KtxTextureFormat.Astc10x10, 26 }, { KtxTextureFormat.Astc12x10, 25 }, { KtxTextureFormat.Astc12x12, 24 },
        { KtxTextureFormat.Etc1s, 28 }, { KtxTextureFormat.Uastc, 38 },
    };

    /// <summary>The files tools/make-ktx-references.py has `ktx validate` and libktx check; written.json holds the verdicts.</summary>
    public static TheoryData<string> CheckedNames => [.. Checked().Select(c => c.Name)];

    [Theory]
    [MemberData(nameof(Uncompressed))]
    public void RoundTripsUncompressedFormats(KtxTextureFormat format)
    {
        using Raster<Rgba8> colors = TestImages.Colors();
        byte[] file = Save(colors, new Ktx2WriterOptions { Format = format, MipLevels = 1 });
        using Raster loaded = Raster.Load(file, Context);
        using Raster<Rgba8> back = loaded.As<Rgba8>();

        // What a direct conversion to the format's pixel type keeps is what the file keeps; the small floats round their
        // 5- and 6-bit mantissas.
        using Raster<Rgba8> expected = Expected(colors, format);
        int tolerance = format is KtxTextureFormat.R11G11B10F or KtxTextureFormat.Rgb9E5 ? 2 : 0;
        for (int y = 0; y < colors.Height; y++)
        {
            for (int x = 0; x < colors.Width; x++)
            {
                Rgba8 e = expected.Row(y)[x], a = back.Row(y)[x];
                Assert.True(Math.Abs(e.R - a.R) <= tolerance && Math.Abs(e.G - a.G) <= tolerance && Math.Abs(e.B - a.B) <= tolerance && e.A == a.A, $"({x}, {y}): {e} became {a}");
            }
        }

        Assert.Empty(loaded.Metadata.Warnings);
        Assert.Equal(KtxSupercompression.None, Details(file).Supercompression);
    }

    [Theory]
    [InlineData(KtxTextureFormat.R32UInt)]
    [InlineData(KtxTextureFormat.R32SInt)]
    [InlineData(KtxTextureFormat.Rg32UInt)]
    [InlineData(KtxTextureFormat.Rgb32SInt)]
    [InlineData(KtxTextureFormat.Rgba32UInt)]
    [InlineData(KtxTextureFormat.Rgba32SInt)]
    public void RoundTrips32BitIntegers(KtxTextureFormat format)
    {
        // The reader gives 32-bit integers as floats; whole numbers come back exactly, and the integer formats keep mip 0 alone.
        // A one-channel format stores the raster's gray, so its test colours are grays.
        int channels = format.ToString().StartsWith("Rgba", StringComparison.Ordinal) ? 4 : format.ToString().StartsWith("Rgb", StringComparison.Ordinal) ? 3
            : format.ToString().StartsWith("Rg", StringComparison.Ordinal) ? 2 : 1;
        using Raster<RgbaF32> values = Raster.Create<RgbaF32>(5, 3);
        for (int y = 0; y < values.Height; y++)
        {
            for (int x = 0; x < values.Width; x++)
            {
                values.Row(y)[x] = channels == 1 ? new RgbaF32(x * 1000, x * 1000, x * 1000, 1) : new RgbaF32(x * 1000, y * 7, x + y, 3);
            }
        }

        byte[] file = Save(values, new Ktx2WriterOptions { Format = format });
        Assert.Equal(1, Details(file).MipLevels);
        using Raster<RgbaF32> back = Raster.Load<RgbaF32>(file, Context);
        for (int y = 0; y < values.Height; y++)
        {
            for (int x = 0; x < values.Width; x++)
            {
                RgbaF32 e = values.Row(y)[x], a = back.Row(y)[x];
                float[] expected = [e.R, e.G, e.B, e.A], actual = [a.R, a.G, a.B, a.A];
                for (int c = 0; c < channels; c++)
                {
                    Assert.Equal(expected[c], actual[c]);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Blocks))]
    public void EncodesBlockFormats(KtxTextureFormat format, double minimumPsnr)
    {
        // The 1-bit alpha formats get an opaque photo, so their colour error is what is measured.
        using Raster<Rgba8> photo = Photo(opaque: format is KtxTextureFormat.Bc1 or KtxTextureFormat.Etc1 or KtxTextureFormat.Etc2Rgb or KtxTextureFormat.Etc2RgbA1);
        byte[] file = Save(photo, new Ktx2WriterOptions { Format = format, MipLevels = 1, Srgb = false });
        using Raster<Rgba8> back = Raster.Load<Rgba8>(file, Context);
        double psnr = Psnr(photo, back, format);
        Assert.True(psnr >= minimumPsnr, $"{format}: {psnr:F2} dB");
        KtxDetails details = Details(file);
        Assert.False(details.IsSrgb);
        Assert.Equal(format == KtxTextureFormat.Etc1s ? KtxSupercompression.BasisLZ : KtxSupercompression.None, details.Supercompression);
    }

    [Fact]
    public void EncodesTheSameBytesOnAnyNumberOfThreads()
    {
        foreach (Ktx2WriterOptions options in (Ktx2WriterOptions[])[
            new() { Format = KtxTextureFormat.Bc7 }, new() { Format = KtxTextureFormat.Astc6x6 }, new() { Format = KtxTextureFormat.Etc2Rgba },
            new() { Format = KtxTextureFormat.Etc1s }, new() { Format = KtxTextureFormat.Uastc, UastcRdo = 2, Supercompression = KtxSupercompression.Zstandard }])
        {
            using Raster<Rgba8> serial = Photo(Context with { MaxParallelism = 1 });
            using Raster<Rgba8> parallel = Photo(Context with { MaxParallelism = 8 });
            Assert.Equal(Save(serial, options), Save(parallel, options));
        }
    }

    [Fact]
    public void PicksTheFormatAndSrgb()
    {
        // Untagged 8-bit colour is sRGB, as Egami reads it; a linear tag or a format with no sRGB form writes UNORM.
        using Raster<Rgba8> colors = TestImages.Colors();
        Assert.Equal(("R8G8B8A8_SRGB", true), (Details(Save(colors, null)).FormatName, Details(Save(colors, null)).IsSrgb));
        colors.Metadata.ColorSpace = ColorSpaceHint.Linear;
        Assert.Equal("R8G8B8A8_UNORM", Details(Save(colors, null)).FormatName);
        Assert.Equal("BC7_SRGB_BLOCK", Details(Save(colors, new Ktx2WriterOptions { Format = KtxTextureFormat.Bc7, Srgb = true })).FormatName);
        Assert.Equal("BC4_UNORM_BLOCK", Details(Save(colors, new Ktx2WriterOptions { Format = KtxTextureFormat.Bc4 })).FormatName);
        Assert.Throws<ArgumentException>(() => Save(colors, new Ktx2WriterOptions { Format = KtxTextureFormat.R16, Srgb = true }));
        Assert.Throws<ArgumentException>(() => Save(colors, new Ktx2WriterOptions { Supercompression = KtxSupercompression.BasisLZ }));

        // The format follows the pixel type, or a KTX or DDS source's own format.
        using Raster<GrayF32> gray = Raster.Create<GrayF32>(4, 4, new GrayF32(0.25f));
        Assert.Equal("R32_SFLOAT", Details(Save(gray, new Ktx2WriterOptions { MipLevels = 1 })).FormatName);
        using Raster<RgbaF16> half = Raster.Create<RgbaF16>(4, 4);
        Assert.Equal("R16G16B16A16_SFLOAT", Details(Save(half, null)).FormatName);
        using Raster astc = Raster.Load(Save(colors, new Ktx2WriterOptions { Format = KtxTextureFormat.Astc5x4, Srgb = true }), Context);
        Assert.Equal("ASTC_5x4_SRGB_BLOCK", Details(Save(astc, null)).FormatName);
        using Raster etc1s = Raster.Load(Save(colors, new Ktx2WriterOptions { Format = KtxTextureFormat.Etc1s }), Context);
        Assert.Equal(KtxSupercompression.BasisLZ, Details(Save(etc1s, null)).Supercompression);
        using MemoryStream dds = new();
        colors.Save(dds, DdsFormat.Info, new DdsWriterOptions { Format = DdsTextureFormat.Bc3, Srgb = true });
        using Raster bc3 = Raster.Load(dds.ToArray(), Context);
        Assert.Equal("BC3_SRGB_BLOCK", Details(Save(bc3, null)).FormatName);
    }

    [Fact]
    public void WritesMipChains()
    {
        using Raster<Rgba8> colors = TestImages.Colors();
        byte[] full = Save(colors, null);
        Assert.Equal(4, Details(full).MipLevels);
        using RasterList mips = Raster.LoadAll(full, Context, new KtxReaderOptions { IncludeMipLevels = true });
        Assert.Equal([(13, 7), (6, 3), (3, 1), (1, 1)], mips.Select(m => (m.Width, m.Height)));
        Assert.Equal(2, Details(Save(colors, new Ktx2WriterOptions { MipLevels = 2 })).MipLevels);
        Assert.Equal(4, Details(Save(colors, new Ktx2WriterOptions { MipLevels = 40 })).MipLevels);

        // A flat image stays flat at every level, in every format family.
        using Raster<Rgba8> flat = Raster.Create<Rgba8>(16, 16, new Rgba8(10, 200, 30, 255));
        foreach (KtxTextureFormat format in (KtxTextureFormat[])[KtxTextureFormat.Rgba8, KtxTextureFormat.Bc7, KtxTextureFormat.Astc6x6, KtxTextureFormat.Etc1s, KtxTextureFormat.Uastc])
        {
            byte[] file = Save(flat, new Ktx2WriterOptions { Format = format, Srgb = false, Supercompression = format == KtxTextureFormat.Uastc ? KtxSupercompression.Zstandard : KtxSupercompression.None });
            using RasterList flatMips = Raster.LoadAll(file, Context, new KtxReaderOptions { IncludeMipLevels = true });
            Assert.Equal(5, flatMips.Count);
            Assert.All(flatMips, m =>
            {
                using Raster<Rgba8> rgba = m.As<Rgba8>();
                Rgba8 p = rgba.Row(0)[0];
                Assert.True(Math.Abs(p.R - 10) <= 4 && Math.Abs(p.G - 200) <= 4 && Math.Abs(p.B - 30) <= 4, $"{format}: {p}");
            });
        }
    }

    [Theory]
    [InlineData(KtxSupercompression.Zstandard, 0)]
    [InlineData(KtxSupercompression.Zstandard, 19)]
    [InlineData(KtxSupercompression.Zlib, 0)]
    [InlineData(KtxSupercompression.Zlib, 9)]
    public void Supercompresses(KtxSupercompression scheme, int level)
    {
        using Raster<Rgba8> photo = Photo();
        foreach (KtxTextureFormat format in (KtxTextureFormat[])[KtxTextureFormat.Rgba8, KtxTextureFormat.Bc1, KtxTextureFormat.Uastc])
        {
            byte[] plain = Save(photo, new Ktx2WriterOptions { Format = format });
            byte[] packed = Save(photo, new Ktx2WriterOptions { Format = format, Supercompression = scheme, CompressionLevel = level });
            Assert.Equal(scheme, Details(packed).Supercompression);
            Assert.True(packed.Length < plain.Length, $"{format}: {packed.Length} bytes, {plain.Length} plain");
            using RasterList a = Raster.LoadAll(plain, Context, new KtxReaderOptions { IncludeMipLevels = true });
            using RasterList b = Raster.LoadAll(packed, Context, new KtxReaderOptions { IncludeMipLevels = true });
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(KtxFiles.Floats(a[i]), KtxFiles.Floats(b[i]));
            }
        }

        Assert.Throws<ArgumentException>(() => Save(photo, new Ktx2WriterOptions { Supercompression = KtxSupercompression.Zlib, CompressionLevel = 12 }));
    }

    [Fact]
    public void RdoMakesUastcSmallerUnderZstandard()
    {
        using Raster<Rgba8> photo = Photo();
        byte[] plain = Save(photo, new Ktx2WriterOptions { Format = KtxTextureFormat.Uastc, MipLevels = 1, Supercompression = KtxSupercompression.Zstandard });
        byte[] rdo = Save(photo, new Ktx2WriterOptions { Format = KtxTextureFormat.Uastc, MipLevels = 1, Supercompression = KtxSupercompression.Zstandard, UastcRdo = 8 });
        Assert.True(rdo.Length < plain.Length, $"{rdo.Length} bytes with RDO, {plain.Length} without");
        using Raster<Rgba8> back = Raster.Load<Rgba8>(rdo, Context);
        Assert.True(Psnr(photo, back, KtxTextureFormat.Uastc) > 30);
    }

    [Fact]
    public void WritesArraysAndCubes()
    {
        Raster<Rgba8>[] faces = [.. Enumerable.Range(0, 6).Select(i => Raster.Create<Rgba8>(8, 8, new Rgba8((byte)(i * 40), 0, 0, 255)))];
        try
        {
            foreach (KtxTextureFormat format in (KtxTextureFormat[])[KtxTextureFormat.Bc1, KtxTextureFormat.Etc1s, KtxTextureFormat.Uastc])
            {
                using MemoryStream cube = new();
                Raster.SaveAll(faces, cube, Ktx2Format.Info, new Ktx2WriterOptions { Cube = true, Format = format, Srgb = false });
                using RasterList loaded = Raster.LoadAll(cube.ToArray(), Context);
                Assert.Equal(6, loaded.Count);
                KtxDetails details = Details(cube.ToArray());
                Assert.Equal((true, 6, 1, 4), (details.IsCube, details.Faces, details.Layers, details.MipLevels));
                for (int i = 0; i < 6; i++)
                {
                    using Raster<Rgba8> face = loaded[i].As<Rgba8>();
                    Assert.InRange(face.Row(0)[0].R, (i * 40) - 4, (i * 40) + 4);
                }
            }

            using MemoryStream array = new();
            Raster.SaveAll(faces[..3], array, Ktx2Format.Info, new Ktx2WriterOptions { MipLevels = 1 });
            KtxDetails arrayDetails = Details(array.ToArray());
            Assert.Equal((3, 1, false), (arrayDetails.Layers, arrayDetails.Faces, arrayDetails.IsCube));
            Assert.Equal(3, Raster.Probe(array.ToArray(), Context).ImageCount);

            // Twelve faces are two cubes: a cube map array.
            using MemoryStream cubes = new();
            Raster.SaveAll([.. faces, .. faces], cubes, Ktx2Format.Info, new Ktx2WriterOptions { Cube = true, MipLevels = 1 });
            Assert.Equal((2, 6), (Details(cubes.ToArray()).Layers, Details(cubes.ToArray()).Faces));

            Assert.Throws<ArgumentException>(() => Raster.SaveAll(faces[..5], new MemoryStream(), Ktx2Format.Info, new Ktx2WriterOptions { Cube = true }));
            using Raster<Rgba8> other = Raster.Create<Rgba8>(4, 4);
            Assert.Throws<ArgumentException>(() => Raster.SaveAll([faces[0], other], new MemoryStream(), Ktx2Format.Info));
        }
        finally
        {
            foreach (Raster<Rgba8> face in faces)
            {
                face.Dispose();
            }
        }
    }

    [Fact]
    public void WritesTheDescriptorAndKeys()
    {
        using Raster<Rgba8> half = Raster.Create<Rgba8>(8, 8, new Rgba8(200, 100, 50, 128));
        byte[] file = Save(half, new Ktx2WriterOptions { PremultipliedAlpha = true, MipLevels = 1 });
        KtxDetails details = Details(file);
        Assert.Equal(KtxAlphaMode.Premultiplied, details.AlphaMode);
        Assert.Equal("rd", details.Orientation);
        Assert.StartsWith("Egami ", details.KeyValues["KTXwriter"]);
        using Raster<Rgba8> back = Raster.Load<Rgba8>(file, Context);
        Rgba8 p = back.Row(3)[3];
        Assert.InRange(p.R, 196, 204);
        Assert.InRange(p.G, 96, 104);
        Assert.Equal(128, p.A);

        // The colour tag goes into the descriptor's primaries and transfer function, and comes back.
        using Raster<RgbaF16> hdr = Raster.Create<RgbaF16>(4, 4);
        hdr.Metadata.CodePoints = ColorCodePoints.Rec2100Pq;
        using Raster pq = Raster.Load(Save(hdr, null), Context);
        Assert.Equal(ColorCodePoints.Rec2100Pq, pq.Metadata.CodePoints);
        using Raster<Rgba8> linear = Raster.Create<Rgba8>(4, 4);
        linear.Metadata.ColorSpace = ColorSpaceHint.Linear;
        using Raster linearBack = Raster.Load(Save(linear, null), Context);
        Assert.Equal(ColorSpaceHint.Linear, linearBack.Metadata.ColorSpace);
    }

    [Theory]
    [MemberData(nameof(CheckedNames))]
    public void PassesKtxValidateAndLibktx(string name)
    {
        // tools/make-ktx-references.py writes these files with WritesTheCheckedFiles, runs `ktx validate` on each, reads level 0
        // back with `ktx extract` (transcoded for Basis Universal) and compares the data format descriptor with the one
        // `ktx create` writes for the same format. The descriptor written today must still be the one checked.
        Assert.True(s_written.Value.TryGetProperty(name, out JsonElement entry), $"{name} is not in written.json: run tools/make-ktx-references.py --written");
        Assert.Equal("valid", entry.GetProperty("validate").GetString());
        Assert.Equal("same", entry.GetProperty("libktx").GetString());
        Assert.Equal("same", entry.GetProperty("dfd").GetString());
        byte[] file = Checked().Single(c => c.Name == name).Save();
        int offset = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(48));
        int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(52));
        Assert.Equal(entry.GetProperty("descriptor").GetString(), Convert.ToHexStringLower(file.AsSpan(offset, length)));
    }

    [Fact]
    public void WritesTheCheckedFiles()
    {
        // Run by tools/make-ktx-references.py with EGAMI_KTX_WRITTEN set: each file, and Egami's own decode of mip 0 as PNG.
        string? folder = Environment.GetEnvironmentVariable("EGAMI_KTX_WRITTEN");
        if (folder is null)
        {
            return;
        }

        foreach ((string name, Func<byte[]> save) in Checked())
        {
            byte[] file = save();
            File.WriteAllBytes(Path.Combine(folder, name + ".ktx2"), file);
            using Raster<Rgba8> decoded = Raster.Load<Rgba8>(file, Context);
            decoded.Save(Path.Combine(folder, name + ".png"));
            File.WriteAllText(Path.Combine(folder, name + ".txt"), Details(file).FormatName);
        }
    }

    /// <summary>
    /// The files checked against KTX-Software: every format at effort 0 with its full mip chain, and the variants that change the
    /// layout or descriptor (sRGB, supercompression, arrays, cube maps, premultiplied alpha, colour tags).
    /// </summary>
    private static IEnumerable<(string Name, Func<byte[]> Save)> Checked()
    {
        foreach ((string name, Func<Raster[]> rasters, Ktx2WriterOptions options) in CheckedRasters())
        {
            yield return (name, () =>
            {
                Raster[] made = rasters();
                try
                {
                    return SaveAll(made, options);
                }
                finally
                {
                    foreach (Raster raster in made)
                    {
                        raster.Dispose();
                    }
                }
            });
        }

        // Textures saved with their own mips: a volume with three mips, and a cube map with hand-made ones.
        yield return ("w-texture-volume", () =>
        {
            using Texture volume = Texture.Load(KtxFiles.Corpus("rgba8-volume.ktx2"), Context);
            return SaveTexture(volume, new Ktx2WriterOptions { Format = KtxTextureFormat.Bc7, Effort = 0 });
        });
        yield return ("w-texture-cube", () =>
        {
            using Texture cube = Texture.Load(KtxFiles.Corpus("rgba8-cube.ktx2"), Context);
            cube.GenerateMips();
            return SaveTexture(cube, new Ktx2WriterOptions { Format = KtxTextureFormat.Etc1s, Effort = 0 });
        });
    }

    private static IEnumerable<(string Name, Func<Raster[]> Rasters, Ktx2WriterOptions Options)> CheckedRasters()
    {
        foreach (KtxTextureFormat format in Enum.GetValues<KtxTextureFormat>())
        {
            yield return ($"w-{format}".ToLowerInvariant(), () => [Input(format)], new Ktx2WriterOptions { Format = format, Effort = 0, Srgb = false });
        }

        foreach (KtxTextureFormat format in (KtxTextureFormat[])[KtxTextureFormat.Rgba8, KtxTextureFormat.Bgra8, KtxTextureFormat.Rgb8, KtxTextureFormat.R8,
            KtxTextureFormat.Rg8, KtxTextureFormat.Bc1, KtxTextureFormat.Bc2, KtxTextureFormat.Bc3, KtxTextureFormat.Bc7, KtxTextureFormat.Etc2Rgb,
            KtxTextureFormat.Etc2RgbA1, KtxTextureFormat.Etc2Rgba, KtxTextureFormat.Astc6x5, KtxTextureFormat.Etc1s, KtxTextureFormat.Uastc])
        {
            yield return ($"w-{format}-srgb".ToLowerInvariant(), () => [Input(format)], new Ktx2WriterOptions { Format = format, Effort = 0, Srgb = true });
        }

        yield return ("w-rgba8-zstd", () => [Input(KtxTextureFormat.Rgba8)], new Ktx2WriterOptions { Supercompression = KtxSupercompression.Zstandard });
        yield return ("w-bc7-zlib", () => [Input(KtxTextureFormat.Bc7)], new Ktx2WriterOptions { Format = KtxTextureFormat.Bc7, Effort = 0, Supercompression = KtxSupercompression.Zlib });
        yield return ("w-uastc-rdo-zstd", () => [Input(KtxTextureFormat.Uastc)], new Ktx2WriterOptions { Format = KtxTextureFormat.Uastc, Effort = 0, UastcRdo = 4, Supercompression = KtxSupercompression.Zstandard });
        yield return ("w-rgba8-premultiplied", () => [Input(KtxTextureFormat.Rgba8)], new Ktx2WriterOptions { PremultipliedAlpha = true });
        yield return ("w-rgba8-mip0", () => [Input(KtxTextureFormat.Rgba8)], new Ktx2WriterOptions { MipLevels = 1 });
        yield return ("w-rgb8-array", () => [.. Enumerable.Range(0, 3).Select(i => Square(i))], new Ktx2WriterOptions { Format = KtxTextureFormat.Rgb8 });
        yield return ("w-rgba8-cube", () => [.. Enumerable.Range(0, 6).Select(i => Square(i))], new Ktx2WriterOptions { Cube = true });
        yield return ("w-bc1-cube-array", () => [.. Enumerable.Range(0, 12).Select(i => Square(i))], new Ktx2WriterOptions { Cube = true, Format = KtxTextureFormat.Bc1, Effort = 0 });
        yield return ("w-etc1s-array", () => [.. Enumerable.Range(0, 3).Select(i => Square(i))], new Ktx2WriterOptions { Format = KtxTextureFormat.Etc1s, Effort = 0 });
        yield return ("w-uastc-cube", () => [.. Enumerable.Range(0, 6).Select(i => Square(i))], new Ktx2WriterOptions { Cube = true, Format = KtxTextureFormat.Uastc, Effort = 0 });
        yield return ("w-rgbaf16-pq", () => [Tagged(KtxTextureFormat.RgbaF16, ColorCodePoints.Rec2100Pq)], new Ktx2WriterOptions { MipLevels = 1 });
        yield return ("w-rgba8-p3", () => [Tagged(KtxTextureFormat.Rgba8, new ColorCodePoints(12, 13, 0, true))], new Ktx2WriterOptions { MipLevels = 1 });

        static Raster Tagged(KtxTextureFormat format, ColorCodePoints points)
        {
            Raster raster = Input(format);
            raster.Metadata.CodePoints = points;
            return raster;
        }

        static Raster Square(int i)
        {
            using Raster<Rgba8> photo = Photo(opaque: true);
            Raster<Rgba8> square = photo.Derive(x => x.Crop(new Rect(i * 3, i, 16, 16)), TestContext.Current.CancellationToken);
            return square;
        }
    }

    /// <summary>A 37 × 23 photo in the pixel type a format is made from: half floats for HDR, opaque where alpha is not stored.</summary>
    private static Raster Input(KtxTextureFormat format)
    {
        using Raster<Rgba8> photo = Photo(opaque: format is KtxTextureFormat.Rgb8 or KtxTextureFormat.Rgb16 or KtxTextureFormat.RgbF32
            or KtxTextureFormat.R11G11B10F or KtxTextureFormat.Rgb9E5 or KtxTextureFormat.Bgr565 or KtxTextureFormat.Etc1 or KtxTextureFormat.Etc2Rgb);
        Raster<Rgba8> crop = photo.Derive(x => x.Crop(new Rect(5, 9, 37, 23)), TestContext.Current.CancellationToken);
        if (format is KtxTextureFormat.RgbaF16 or KtxTextureFormat.RgbaF32 or KtxTextureFormat.RgbF32 or KtxTextureFormat.R32F
            or KtxTextureFormat.R11G11B10F or KtxTextureFormat.Rgb9E5 or KtxTextureFormat.Bc6h or KtxTextureFormat.Bc6hSigned)
        {
            using (crop)
            {
                Raster<RgbaF16> hdr = crop.As<RgbaF16>();
                hdr.Metadata.ColorSpace = ColorSpaceHint.Linear;
                return hdr;
            }
        }

        return crop;
    }

    private static byte[] Save(Raster raster, Ktx2WriterOptions? options)
    {
        using MemoryStream output = new();
        raster.Save(output, Ktx2Format.Info, options);
        return output.ToArray();
    }

    private static byte[] SaveTexture(Texture texture, Ktx2WriterOptions options)
    {
        using MemoryStream output = new();
        texture.Save(output, Ktx2Format.Info, options);
        return output.ToArray();
    }

    private static byte[] SaveAll(IReadOnlyList<Raster> rasters, Ktx2WriterOptions options)
    {
        using MemoryStream output = new();
        Raster.SaveAll(rasters, output, Ktx2Format.Info, options);
        return output.ToArray();
    }

    /// <summary>A 96 × 64 stand-in for a photo: smooth waves, hard edges and a little noise in every channel.</summary>
    private static Raster<Rgba8> Photo(EgamiContext? context = null, bool opaque = false)
    {
        Random random = new(3);
        Raster<Rgba8> raster = Raster.Create<Rgba8>(96, 64, context ?? Context);
        for (int y = 0; y < raster.Height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                double wave = Math.Sin(x * 0.11) * Math.Cos(y * 0.07);
                int edge = (x / 24 + y / 16) % 2 == 0 ? 40 : 0;
                row[x] = new Rgba8(
                    (byte)Math.Clamp(120 + (90 * wave) + edge + random.Next(-6, 7), 0, 255),
                    (byte)Math.Clamp(100 + (60 * Math.Sin((x + y) * 0.05)) + random.Next(-6, 7), 0, 255),
                    (byte)Math.Clamp(80 + (2 * y) - edge + random.Next(-6, 7), 0, 255),
                    opaque ? (byte)255 : (byte)Math.Clamp(160 + (80 * wave) + random.Next(-4, 5), 0, 255));
            }
        }

        return raster;
    }

    private static KtxDetails Details(byte[] file)
    {
        using Raster raster = Raster.Load(file, Context);
        return Assert.IsType<KtxDetails>(raster.Metadata.FormatDetails);
    }

    /// <summary>The colours as the format keeps them: through its pixel type, with the channels it lacks filled as the reader fills them.</summary>
    private static Raster<Rgba8> Expected(Raster<Rgba8> colors, KtxTextureFormat format)
    {
        Raster<Rgba8> expected = format switch
        {
            KtxTextureFormat.R8 or KtxTextureFormat.R16 or KtxTextureFormat.R32F or KtxTextureFormat.R8UInt or KtxTextureFormat.R8SInt
                or KtxTextureFormat.R16UInt or KtxTextureFormat.R16SInt or KtxTextureFormat.D16 or KtxTextureFormat.D32F => colors.As<Gray8>().As<Rgba8>(),
            KtxTextureFormat.Rgb10A2 => colors.As<Rgb10A2>().As<Rgba8>(),
            KtxTextureFormat.Bgr565 => colors.As<Bgr565>().As<Rgba8>(),
            KtxTextureFormat.Bgra5551 => colors.As<Bgra5551>().As<Rgba8>(),
            KtxTextureFormat.Bgra4444 => colors.As<Bgra4444>().As<Rgba8>(),
            _ => colors.As<Rgba8>(),
        };
        for (int y = 0; y < expected.Height; y++)
        {
            Span<Rgba8> row = expected.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = format switch
                {
                    KtxTextureFormat.Rgb8 or KtxTextureFormat.Rgb16 or KtxTextureFormat.RgbF32 or KtxTextureFormat.R11G11B10F or KtxTextureFormat.Rgb9E5 => row[x] with { A = 255 },
                    KtxTextureFormat.Rg8 or KtxTextureFormat.Rg16 or KtxTextureFormat.Rg8UInt or KtxTextureFormat.Rg8SInt or KtxTextureFormat.Rg16UInt
                        or KtxTextureFormat.Rg16SInt => row[x] with { B = 0, A = 255 },
                    _ => row[x],
                };
            }
        }

        return expected;
    }

    private static double Psnr(Raster<Rgba8> a, Raster<Rgba8> b, KtxTextureFormat format)
    {
        bool gray = format is KtxTextureFormat.Bc4 or KtxTextureFormat.EacR11 or KtxTextureFormat.EacR11Signed;
        int[] channels = format switch
        {
            _ when gray => [0],
            KtxTextureFormat.Bc5 or KtxTextureFormat.EacRg11 or KtxTextureFormat.EacRg11Signed => [0, 1],
            KtxTextureFormat.Bc1 or KtxTextureFormat.Etc1 or KtxTextureFormat.Etc2Rgb or KtxTextureFormat.Etc2RgbA1 => [0, 1, 2],
            _ => [0, 1, 2, 3],
        };
        using Raster<Gray8> luma = gray ? a.As<Gray8>() : Raster.Create<Gray8>(1, 1);
        double sum = 0;
        long count = 0;
        for (int y = 0; y < a.Height; y++)
        {
            ReadOnlySpan<byte> pa = MemoryMarshal.AsBytes(a.Row(y));
            ReadOnlySpan<byte> pb = MemoryMarshal.AsBytes(b.Row(y));
            for (int x = 0; x < a.Width; x++)
            {
                foreach (int c in channels)
                {
                    int expected = gray ? luma.Row(y)[x].L : pa[(x * 4) + c];
                    double d = expected - pb[(x * 4) + c];
                    sum += d * d;
                    count++;
                }
            }
        }

        return 10 * Math.Log10(255.0 * 255.0 / Math.Max(sum / count, 1e-9));
    }
}
