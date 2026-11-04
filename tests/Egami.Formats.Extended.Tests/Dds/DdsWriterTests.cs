using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Formats.Extended.Dds;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Dds;

/// <summary>The DDS writer: formats, headers, mips, arrays and cube maps, and the BC1 to BC5 encoders.</summary>
public class DdsWriterTests
{
    private static EgamiContext Context => TestImages.Context;

    public static TheoryData<DdsTextureFormat> Uncompressed =>
    [
        DdsTextureFormat.Rgba8, DdsTextureFormat.Bgra8, DdsTextureFormat.Bgrx8, DdsTextureFormat.R8, DdsTextureFormat.Rg8, DdsTextureFormat.A8,
        DdsTextureFormat.R16, DdsTextureFormat.Rg16, DdsTextureFormat.Rgba16, DdsTextureFormat.RgbaF16, DdsTextureFormat.R32F,
        DdsTextureFormat.RgbF32, DdsTextureFormat.RgbaF32, DdsTextureFormat.Rgb10A2, DdsTextureFormat.R11G11B10F, DdsTextureFormat.Rgb9E5,
        DdsTextureFormat.Bgr565, DdsTextureFormat.Bgra5551, DdsTextureFormat.Bgra4444,
    ];

    [Theory]
    [MemberData(nameof(Uncompressed))]
    public void RoundTripsUncompressedFormats(DdsTextureFormat format)
    {
        using Raster<Rgba8> colors = TestImages.Colors();
        byte[] file = Save(colors, new DdsWriterOptions { Format = format, MipLevels = 1 });
        using Raster loaded = Raster.Load(file, Context);
        using Raster<Rgba8> back = loaded.As<Rgba8>();

        // What a direct conversion to the format's pixel type keeps is what the file keeps; the small floats round their
        // 5- and 6-bit mantissas.
        using Raster<Rgba8> expected = Expected(colors, format);
        int tolerance = format is DdsTextureFormat.R11G11B10F or DdsTextureFormat.Rgb9E5 ? 2 : 0;
        for (int y = 0; y < colors.Height; y++)
        {
            for (int x = 0; x < colors.Width; x++)
            {
                Rgba8 e = expected.Row(y)[x], a = back.Row(y)[x];
                Assert.True(Math.Abs(e.R - a.R) <= tolerance && Math.Abs(e.G - a.G) <= tolerance && Math.Abs(e.B - a.B) <= tolerance && e.A == a.A, $"({x}, {y}): {e} became {a}");
            }
        }

        Assert.Empty(loaded.Metadata.Warnings);
    }

    [Fact]
    public void PicksTheHeader()
    {
        using Raster<Rgba8> colors = TestImages.Colors();
        Assert.False(Details(Save(colors, new DdsWriterOptions { MipLevels = 1 })).Dx10Header);
        Assert.True(Details(Save(colors, new DdsWriterOptions { MipLevels = 1, Dx10Header = true })).Dx10Header);
        Assert.True(Details(Save(colors, new DdsWriterOptions { MipLevels = 1, Format = DdsTextureFormat.RgbaF16 })).Dx10Header);
        DdsDetails srgb = Details(Save(colors, new DdsWriterOptions { MipLevels = 1, Srgb = true }));
        Assert.Equal((true, true, "R8G8B8A8_UNORM_SRGB"), (srgb.Dx10Header, srgb.IsSrgb, srgb.FormatName));
        DdsDetails bc1 = Details(Save(colors, new DdsWriterOptions { MipLevels = 1, Format = DdsTextureFormat.Bc1 }));
        Assert.Equal(("DXT1", false), (bc1.FourCC, bc1.Dx10Header));
        Assert.Throws<ArgumentException>(() => Save(colors, new DdsWriterOptions { Format = DdsTextureFormat.R8, Srgb = true }));

        // The format follows the pixel type, or a DDS source's own format.
        using Raster<GrayF32> gray = Raster.Create<GrayF32>(4, 4, new GrayF32(0.25f));
        Assert.Equal("R32_FLOAT", Details(Save(gray, new DdsWriterOptions { MipLevels = 1 })).FormatName);
        using Raster bc3 = Raster.Load(Save(colors, new DdsWriterOptions { Format = DdsTextureFormat.Bc3 }), Context);
        Assert.Equal("BC3_UNORM", Details(Save(bc3, null)).FormatName);
    }

    [Fact]
    public void WritesMipChains()
    {
        using Raster<Rgba8> colors = TestImages.Colors();
        byte[] full = Save(colors, null);
        Assert.Equal(4, Details(full).MipLevels);
        using RasterList mips = Raster.LoadAll(full, Context, new DdsReaderOptions { IncludeMipLevels = true });
        Assert.Equal([(13, 7), (6, 3), (3, 1), (1, 1)], mips.Select(m => (m.Width, m.Height)));
        Assert.Equal(2, Details(Save(colors, new DdsWriterOptions { MipLevels = 2 })).MipLevels);
        Assert.Equal(4, Details(Save(colors, new DdsWriterOptions { MipLevels = 40 })).MipLevels);

        // A flat image stays flat at every level.
        using Raster<Rgba8> flat = Raster.Create<Rgba8>(16, 16, new Rgba8(10, 200, 30, 255));
        using RasterList flatMips = Raster.LoadAll(Save(flat, null), Context, new DdsReaderOptions { IncludeMipLevels = true });
        Assert.Equal(5, flatMips.Count);
        Assert.All(flatMips, m => Assert.Equal(new Rgba8(10, 200, 30, 255), ((Raster<Rgba8>)m).Row(0)[0]));
    }

    [Fact]
    public void RewritesItsOwnMipsWhileUnchanged()
    {
        // A file whose mip 1 is not the average of mip 0: a resave keeps it, an edited raster makes a new one.
        byte[] data = [.. Enumerable.Repeat((byte)200, 16), .. Enumerable.Repeat((byte)7, 4), 9];
        byte[] file = DdsFiles.Dx10(4, 4, 61, data, mips: 3);
        using Raster loaded = Raster.Load(file, Context);
        byte[] resaved = Save(loaded, null);
        Assert.Equal(data[16..], resaved[^5..]);
        using Raster edited = loaded.Derive(x => x.Invert(), TestContext.Current.CancellationToken);
        byte[] remade = Save(edited, null);
        Assert.Equal(Enumerable.Repeat((byte)55, 5), remade[^5..]);
    }

    [Fact]
    public void WritesArraysAndCubes()
    {
        Raster<Rgba8>[] faces = [.. Enumerable.Range(0, 6).Select(i => Raster.Create<Rgba8>(8, 8, new Rgba8((byte)(i * 40), 0, 0, 255)))];
        try
        {
            using MemoryStream cube = new();
            Raster.SaveAll(faces, cube, DdsFormat.Info, new DdsWriterOptions { Cube = true, Format = DdsTextureFormat.Bc1 });
            using RasterList loaded = Raster.LoadAll(cube.ToArray(), Context);
            Assert.Equal(6, loaded.Count);
            DdsDetails details = Details(cube.ToArray());
            Assert.Equal((true, false, 4), (details.IsCube, details.Dx10Header, details.MipLevels));
            for (int i = 0; i < 6; i++)
            {
                Assert.InRange(((Raster<Rgba8>)loaded[i]).Row(0)[0].R, (i * 40) - 3, (i * 40) + 3);
            }

            using MemoryStream array = new();
            Raster.SaveAll(faces[..3], array, DdsFormat.Info, new DdsWriterOptions { MipLevels = 1 });
            DdsDetails arrayDetails = Details(array.ToArray());
            Assert.Equal((3, true, false), (arrayDetails.ArraySize, arrayDetails.Dx10Header, arrayDetails.IsCube));
            Assert.Equal(3, Raster.Probe(array.ToArray(), Context).ImageCount);

            Assert.Throws<ArgumentException>(() => Raster.SaveAll(faces[..5], new MemoryStream(), DdsFormat.Info, new DdsWriterOptions { Cube = true }));
            using Raster<Rgba8> other = Raster.Create<Rgba8>(4, 4);
            Assert.Throws<ArgumentException>(() => Raster.SaveAll([faces[0], other], new MemoryStream(), DdsFormat.Info));
        }
        finally
        {
            foreach (Raster<Rgba8> face in faces)
            {
                face.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(DdsTextureFormat.Bc1, 35.5)]
    [InlineData(DdsTextureFormat.Bc2, 35)]
    [InlineData(DdsTextureFormat.Bc3, 36.5)]
    [InlineData(DdsTextureFormat.Bc4, 52)]
    [InlineData(DdsTextureFormat.Bc5, 48)]
    [InlineData(DdsTextureFormat.Bc7, 38)]
    public void EncodesBlocks(DdsTextureFormat format, double minimumPsnr)
    {
        // BC1's alpha is 1 bit: the photo is opaque for it, so its colour error is what is measured.
        using Raster<Rgba8> photo = Photo(opaque: format == DdsTextureFormat.Bc1);
        List<(int Effort, double Psnr)> results = [];
        foreach (int effort in (int[])[0, 1, 2, 3, 4, 5, 6])
        {
            byte[] file = Save(photo, new DdsWriterOptions { Format = format, MipLevels = 1, Effort = effort });
            using Raster<Rgba8> back = Raster.Load<Rgba8>(file, Context);
            results.Add((effort, Psnr(photo, back, format)));
        }

        string summary = string.Join(", ", results.Select(r => $"{r.Effort}: {r.Psnr:F2} dB"));
        Assert.All(results, r => Assert.True(r.Psnr >= minimumPsnr - (r.Effort == 0 ? 3 : 0), $"{format}: {summary}"));

        // The same bytes on one thread and on many.
        using Raster<Rgba8> serial = Photo(Context with { MaxParallelism = 1 });
        using Raster<Rgba8> parallel = Photo(Context with { MaxParallelism = 8 });
        byte[] one = Save(serial, new DdsWriterOptions { Format = format });
        byte[] many = Save(parallel, new DdsWriterOptions { Format = format });
        Assert.Equal(one, many);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EncodesBc6h(bool negatives)
    {
        // HDR waves from 0.001 to about 40 (and their negatives on the left for the signed format), in half floats.
        using Raster<RgbaF16> hdr = Raster.Create<RgbaF16>(64, 48);
        for (int y = 0; y < hdr.Height; y++)
        {
            Span<RgbaF16> row = hdr.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                float v = MathF.Exp((float)(Math.Sin(x * 0.2) * Math.Cos(y * 0.15) * 5)) * 0.3f;
                float sign = negatives && x < 16 ? -1 : 1;
                row[x] = new RgbaF16((Half)(sign * v), (Half)(v * 0.5f), (Half)(v * 0.25f + 0.001f), Half.One);
            }
        }

        DdsTextureFormat format = negatives ? DdsTextureFormat.Bc6hSigned : DdsTextureFormat.Bc6h;
        foreach (int effort in (int[])[0, 4, 6])
        {
            byte[] file = Save(hdr, new DdsWriterOptions { Format = format, MipLevels = 1, Effort = effort });
            Assert.Equal(negatives ? "BC6H_SF16" : "BC6H_UF16", Details(file).FormatName);
            using Raster<RgbaF16> back = Raster.Load<RgbaF16>(file, Context);
            double relative = 0;
            for (int y = 0; y < hdr.Height; y++)
            {
                for (int x = 0; x < hdr.Width; x++)
                {
                    RgbaF16 a = hdr.Row(y)[x], b = back.Row(y)[x];
                    relative += Math.Abs((float)a.R - (float)b.R) / (Math.Abs((float)a.R) + 0.01);
                    relative += Math.Abs((float)a.G - (float)b.G) / (Math.Abs((float)a.G) + 0.01);
                    relative += Math.Abs((float)a.B - (float)b.B) / (Math.Abs((float)a.B) + 0.01);
                }
            }

            relative /= hdr.Width * hdr.Height * 3;
            Assert.True(relative < (effort == 0 ? 0.08 : 0.05), $"effort {effort}: mean relative error {relative:F4}");
        }
    }

    [Theory]
    [InlineData(DdsTextureFormat.Bc2)]
    [InlineData(DdsTextureFormat.Bc3)]
    public void KeepsBc2AndBc3ColourInFourColours(DdsTextureFormat format)
    {
        // Black, white and their midpoint: BC1's three-colour mode codes it exactly, but BC2 and BC3 always decode four
        // colours, so their encoder must not choose it. At any effort the decoded error is at most the four-colour fit's.
        using Raster<Rgba8> block = Raster.Create<Rgba8>(4, 4);
        for (int i = 0; i < 16; i++)
        {
            byte v = (i % 3) switch { 0 => 0, 1 => 255, _ => 128 };
            block.Row(i / 4)[i % 4] = new Rgba8(v, v, v, 255);
        }

        long Error(int effort)
        {
            byte[] file = Save(block, new DdsWriterOptions { Format = format, MipLevels = 1, Effort = effort });

            // The colour block's endpoints are in four-colour order (c0 above c1, or equal).
            ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(file.Length - 8));
            ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(file.Length - 6));
            Assert.True(c0 >= c1, $"{format} at effort {effort}: endpoints {c0:X4} and {c1:X4} select the three-colour mode");
            using Raster<Rgba8> back = Raster.Load<Rgba8>(file, Context);
            long sum = 0;
            for (int i = 0; i < 16; i++)
            {
                int d = back.Row(i / 4)[i % 4].R - block.Row(i / 4)[i % 4].R;
                sum += d * d;
            }

            return sum;
        }

        long baseline = Error(0);
        foreach (int effort in (int[])[1, 2, 3, 4, 5, 6])
        {
            Assert.True(Error(effort) <= baseline, $"{format} at effort {effort} decodes worse than at effort 0");
        }
    }

    [Fact]
    public void KeepsBc1Transparency()
    {
        using Raster<Rgba8> colors = TestImages.Colors(8, 8);
        using Raster<Rgba8> back = Raster.Load<Rgba8>(Save(colors, new DdsWriterOptions { Format = DdsTextureFormat.Bc1, MipLevels = 1 }), Context);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(0, back.Row(i)[i].A);
            Assert.Equal(255, back.Row(i)[(i + 1) % 8].A);
        }
    }

    [Fact]
    public void WritesPremultipliedAlpha()
    {
        using Raster<Rgba8> half = Raster.Create<Rgba8>(8, 8, new Rgba8(200, 100, 50, 128));
        byte[] dxt4 = Save(half, new DdsWriterOptions { Format = DdsTextureFormat.Bc3, PremultipliedAlpha = true, MipLevels = 1 });
        DdsDetails details = Details(dxt4);
        Assert.Equal(("DXT4", DdsAlphaMode.Premultiplied), (details.FourCC, details.AlphaMode));
        using Raster<Rgba8> back = Raster.Load<Rgba8>(dxt4, Context);
        Rgba8 p = back.Row(3)[3];
        Assert.InRange(p.R, 196, 204);
        Assert.InRange(p.G, 96, 104);
        Assert.Equal(128, p.A);

        DdsDetails dx10 = Details(Save(half, new DdsWriterOptions { PremultipliedAlpha = true, MipLevels = 1 }));
        Assert.Equal((true, DdsAlphaMode.Premultiplied), (dx10.Dx10Header, dx10.AlphaMode));
    }

    [Fact]
    public void PacksSmallFloats()
    {
        Assert.Equal((15u << 6) | ((14u << 6) << 11) | ((15u << 5) << 22), DdsWriter.PackR11G11B10(1f, 0.5f, 1f));
        Assert.Equal(0u, DdsWriter.PackR11G11B10(-1f, float.NaN, 0f));
        uint e5 = DdsWriter.PackRgb9E5(1f, 0.5f, 0f);
        using Raster<RgbaF16> back = Raster.Load<RgbaF16>(DdsFiles.Dx10(1, 1, 67, BitConverter.GetBytes(e5)), Context);
        Assert.Equal((Half.One, (Half)0.5f, Half.Zero), (back.Row(0)[0].R, back.Row(0)[0].G, back.Row(0)[0].B));
    }

    public static TheoryData<DdsTextureFormat, string> IntegerFormats => new()
    {
        { DdsTextureFormat.R8UInt, "R8_UINT" }, { DdsTextureFormat.R8SInt, "R8_SINT" },
        { DdsTextureFormat.Rg8UInt, "R8G8_UINT" }, { DdsTextureFormat.Rg8SInt, "R8G8_SINT" },
        { DdsTextureFormat.Rgba8UInt, "R8G8B8A8_UINT" }, { DdsTextureFormat.Rgba8SInt, "R8G8B8A8_SINT" },
        { DdsTextureFormat.R16UInt, "R16_UINT" }, { DdsTextureFormat.R16SInt, "R16_SINT" },
        { DdsTextureFormat.Rg16UInt, "R16G16_UINT" }, { DdsTextureFormat.Rg16SInt, "R16G16_SINT" },
        { DdsTextureFormat.Rgba16UInt, "R16G16B16A16_UINT" }, { DdsTextureFormat.Rgba16SInt, "R16G16B16A16_SINT" },
        { DdsTextureFormat.R32UInt, "R32_UINT" }, { DdsTextureFormat.R32SInt, "R32_SINT" },
        { DdsTextureFormat.Rg32UInt, "R32G32_UINT" }, { DdsTextureFormat.Rg32SInt, "R32G32_SINT" },
        { DdsTextureFormat.Rgb32UInt, "R32G32B32_UINT" }, { DdsTextureFormat.Rgb32SInt, "R32G32B32_SINT" },
        { DdsTextureFormat.Rgba32UInt, "R32G32B32A32_UINT" }, { DdsTextureFormat.Rgba32SInt, "R32G32B32A32_SINT" },
        { DdsTextureFormat.D16, "D16_UNORM" }, { DdsTextureFormat.D32F, "D32_FLOAT" }, { DdsTextureFormat.D32FS8, "D32_FLOAT_S8X24_UINT" },
    };

    [Theory]
    [MemberData(nameof(IntegerFormats))]
    public void RoundTripsIntegerAndDepthFormatsExactly(DdsTextureFormat format, string name)
    {
        // Every value the format holds comes back as it was written, in the pixel type the reader loads the format as.
        switch (format)
        {
            case DdsTextureFormat.R8UInt or DdsTextureFormat.R8SInt:
                Check<Gray8>(0, false);
                break;
            case DdsTextureFormat.Rg8UInt or DdsTextureFormat.Rg8SInt:
                Check<Rgb8>(3, false);
                break;
            case DdsTextureFormat.Rgba8UInt or DdsTextureFormat.Rgba8SInt:
                Check<Rgba8>(0, false);
                break;
            case DdsTextureFormat.R16UInt or DdsTextureFormat.R16SInt or DdsTextureFormat.D16:
                Check<Gray16>(0, false);
                break;
            case DdsTextureFormat.Rg16UInt or DdsTextureFormat.Rg16SInt:
                Check<Rgb16>(3, false);
                break;
            case DdsTextureFormat.Rgba16UInt or DdsTextureFormat.Rgba16SInt:
                Check<Rgba16>(0, false);
                break;
            case DdsTextureFormat.R32UInt or DdsTextureFormat.R32SInt or DdsTextureFormat.D32F or DdsTextureFormat.D32FS8:
                Check<GrayF32>(0, true);
                break;
            case DdsTextureFormat.Rg32UInt or DdsTextureFormat.Rg32SInt:
                Check<RgbF32>(3, true);
                break;
            case DdsTextureFormat.Rgb32UInt or DdsTextureFormat.Rgb32SInt:
                Check<RgbF32>(0, true);
                break;
            default:
                Check<RgbaF32>(0, true);
                break;
        }

        void Check<TPixel>(int channels, bool floats)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            Random random = new(7);
            bool signed = name.EndsWith("SINT", StringComparison.Ordinal);
            using Raster<TPixel> source = Raster.Create<TPixel>(9, 5, Context);
            for (int y = 0; y < source.Height; y++)
            {
                if (floats)
                {
                    Span<float> row = MemoryMarshal.Cast<TPixel, float>(source.Row(y));
                    for (int i = 0; i < row.Length; i++)
                    {
                        // Integers a float holds exactly, the 32-bit extremes among them.
                        row[i] = format is DdsTextureFormat.D32F or DdsTextureFormat.D32FS8 ? random.NextSingle()
                            : (i + y) % 7 == 0 ? (signed ? (y % 2 == 0 ? int.MinValue : int.MaxValue) : uint.MaxValue)
                            : signed ? random.Next(-(1 << 24), 1 << 24) : random.Next(0, 1 << 24);
                    }
                }
                else
                {
                    random.NextBytes(MemoryMarshal.AsBytes(source.Row(y)));
                }

                if (channels > 0)
                {
                    // Two-channel formats keep no blue; the reader fills it with 0.
                    Span<byte> bytes = MemoryMarshal.AsBytes(source.Row(y));
                    int sample = bytes.Length / (source.Width * channels);
                    for (int x = 0; x < source.Width; x++)
                    {
                        bytes.Slice(((x * channels) + 2) * sample, sample).Clear();
                    }
                }
            }

            byte[] file = Save(source, new DdsWriterOptions { Format = format, MipLevels = 1 });
            Assert.Equal(name, Details(file).FormatName);
            using Raster loaded = Raster.Load(file, Context);
            using Raster<TPixel> back = loaded.As<TPixel>();
            for (int y = 0; y < source.Height; y++)
            {
                Assert.True(MemoryMarshal.AsBytes(source.Row(y)).SequenceEqual(MemoryMarshal.AsBytes(back.Row(y))), $"{format}: row {y} differs.");
            }

            Assert.Empty(loaded.Metadata.Warnings);
            if (format == DdsTextureFormat.D32FS8)
            {
                // The stencil and unused bits after each depth are 0.
                for (int p = 0; p < source.Width * source.Height; p++)
                {
                    Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(Dx10HeaderBytes + (p * 8) + 4)));
                }
            }
        }
    }

    [Fact]
    public void WritesSignedIntegersAsTheReaderMapsThem()
    {
        // The reader loads a signed integer v as v + 128 (v + 32768 for 16 bits), so 0 stores −128 and 128 stores 0.
        using Raster<Gray8> gray8 = Raster.Create<Gray8>(1, 1, new Gray8(0));
        Assert.Equal(0x80, Save(gray8, new DdsWriterOptions { Format = DdsTextureFormat.R8SInt, MipLevels = 1 })[Dx10HeaderBytes]);
        using Raster<Gray16> gray16 = Raster.Create<Gray16>(1, 1, new Gray16(32768));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(Save(gray16, new DdsWriterOptions { Format = DdsTextureFormat.R16SInt, MipLevels = 1 }).AsSpan(Dx10HeaderBytes)));

        // 32-bit integers round to nearest and clamp to the type's range; NaN stores 0.
        using Raster<GrayF32> floats = Raster.Create<GrayF32>(4, 1, Context);
        floats.Row(0)[0] = new GrayF32(2.5f);
        floats.Row(0)[1] = new GrayF32(-3.7f);
        floats.Row(0)[2] = new GrayF32(1e12f);
        floats.Row(0)[3] = new GrayF32(float.NaN);
        byte[] unsigned = Save(floats, new DdsWriterOptions { Format = DdsTextureFormat.R32UInt, MipLevels = 1 });
        Assert.Equal([2u, 0u, uint.MaxValue, 0u], Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(unsigned.AsSpan(Dx10HeaderBytes + (i * 4)))));
        byte[] signed = Save(floats, new DdsWriterOptions { Format = DdsTextureFormat.R32SInt, MipLevels = 1 });
        Assert.Equal([2, -4, int.MaxValue, 0], Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadInt32LittleEndian(signed.AsSpan(Dx10HeaderBytes + (i * 4)))));
    }

    [Fact]
    public void WritesD24S8Depth()
    {
        // Depth k / (2²⁴ − 1) comes back as written, clamped to 0 to 1; stencil, the top byte, is 0.
        using Raster<GrayF32> depth = Raster.Create<GrayF32>(8, 2, Context);
        int[] codes = [0, 1, 2, 8388607, 8388608, 16777213, 16777214, 16777215];
        for (int x = 0; x < 8; x++)
        {
            depth.Row(0)[x] = new GrayF32(codes[x] / 16777215f);
            depth.Row(1)[x] = new GrayF32((x - 4) * 0.5f);
        }

        byte[] file = Save(depth, new DdsWriterOptions { Format = DdsTextureFormat.D24S8, MipLevels = 1 });
        Assert.Equal("D24_UNORM_S8_UINT", Details(file).FormatName);
        Assert.Equal(codes, Enumerable.Range(0, 8).Select(i => (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(Dx10HeaderBytes + (i * 4)))));
        Assert.Equal([0, 0, 0, 0, 0, 8388608, 16777215, 16777215], Enumerable.Range(8, 8).Select(i => (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(Dx10HeaderBytes + (i * 4)))));
        using Raster<GrayF32> back = Raster.Load<GrayF32>(file, Context);
        for (int x = 0; x < 8; x++)
        {
            Assert.Equal(depth.Row(0)[x].L, back.Row(0)[x].L);
        }
    }

    [Theory]
    [InlineData(DdsTextureFormat.Nv12, "NV12", 1, 257)]
    [InlineData(DdsTextureFormat.P010, "P010", 2, 128)]
    [InlineData(DdsTextureFormat.P016, "P016", 2, 4)]
    public void RoundTripsPlanarVideo(DdsTextureFormat format, string name, int sample, int tolerance)
    {
        // Colour flat over each 2 × 2 block loses nothing to subsampling, so only the Y′CbCr rounding is left: one 8-bit step
        // for NV12, two 10-bit steps for P010 and a few 16-bit ones for P016 (measured 257, 109 and 4).
        Random random = new(11);
        using Raster<Rgb16> blocks = Raster.Create<Rgb16>(16, 10, Context);
        for (int y = 0; y < blocks.Height; y += 2)
        {
            for (int x = 0; x < blocks.Width; x += 2)
            {
                Rgb16 c = new((ushort)(random.Next(256) * 257), (ushort)(random.Next(256) * 257), (ushort)(random.Next(256) * 257));
                blocks.Row(y)[x] = blocks.Row(y)[x + 1] = blocks.Row(y + 1)[x] = blocks.Row(y + 1)[x + 1] = c;
            }
        }

        byte[] file = Save(blocks, new DdsWriterOptions { Format = format });
        DdsDetails details = Details(file);
        Assert.Equal((name, 1), (details.FormatName, details.MipLevels));
        Assert.Equal(Dx10HeaderBytes + (16 * 10 * sample * 3 / 2), file.Length);
        Assert.InRange(MaxError(blocks, file), 0, tolerance);

        // A photo's chroma averages over 2 × 2 blocks; the worst error, at its hard edges, is that of 4:2:0 (measured 14 8-bit
        // steps for each format).
        using Raster<Rgba8> photo = Photo(Context, opaque: true);
        using Raster<Rgb16> wide = photo.As<Rgb16>();
        Assert.InRange(MaxError(wide, Save(photo, new DdsWriterOptions { Format = format })), 0, 15 * 257);

        // 4:2:0 needs an even size.
        using Raster<Rgb8> odd = Raster.Create<Rgb8>(5, 4, Context);
        Assert.Throws<ArgumentException>(() => Save(odd, new DdsWriterOptions { Format = format }));

        static int MaxError(Raster<Rgb16> expected, byte[] file)
        {
            using Raster<Rgb16> back = Raster.Load<Rgb16>(file, Context);
            int worst = 0;
            for (int y = 0; y < expected.Height; y++)
            {
                for (int x = 0; x < expected.Width; x++)
                {
                    Rgb16 e = expected.Row(y)[x], a = back.Row(y)[x];
                    worst = Math.Max(worst, Math.Max(Math.Abs(e.R - a.R), Math.Max(Math.Abs(e.G - a.G), Math.Abs(e.B - a.B))));
                }
            }

            return worst;
        }
    }

    /// <summary>The length of a DX10 file's headers: magic, header and DX10 extension.</summary>
    private const int Dx10HeaderBytes = 4 + 124 + 20;

    private static byte[] Save(Raster raster, DdsWriterOptions? options)
    {
        using MemoryStream output = new();
        raster.Save(output, DdsFormat.Info, options);
        return output.ToArray();
    }

    /// <summary>A 96 × 64 stand-in for a photo: smooth waves, hard edges and a little noise in every channel.</summary>
    private static Raster<Rgba8> Photo(EgamiContext? context = null, bool opaque = false)
    {
        Random random = new(3);
        Raster<Rgba8> raster = Raster.Create<Rgba8>(96, 64, context);
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

    private static DdsDetails Details(byte[] file)
    {
        using Raster raster = Raster.Load(file, Context, new DdsReaderOptions { MipLevel = 0 });
        return Assert.IsType<DdsDetails>(raster.Metadata.FormatDetails);
    }

    /// <summary>The colours as the format keeps them: through its pixel type, with the channels it lacks filled as the reader fills them.</summary>
    private static Raster<Rgba8> Expected(Raster<Rgba8> colors, DdsTextureFormat format)
    {
        Raster<Rgba8> expected = format switch
        {
            DdsTextureFormat.R8 or DdsTextureFormat.R16 or DdsTextureFormat.R32F => colors.As<Gray8>().As<Rgba8>(),
            DdsTextureFormat.A8 => colors.As<Alpha8>().As<Rgba8>(),
            DdsTextureFormat.Rgb10A2 => colors.As<Rgb10A2>().As<Rgba8>(),
            DdsTextureFormat.Bgr565 => colors.As<Bgr565>().As<Rgba8>(),
            DdsTextureFormat.Bgra5551 => colors.As<Bgra5551>().As<Rgba8>(),
            DdsTextureFormat.Bgra4444 => colors.As<Bgra4444>().As<Rgba8>(),
            _ => colors.As<Rgba8>(),
        };
        for (int y = 0; y < expected.Height; y++)
        {
            Span<Rgba8> row = expected.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = format switch
                {
                    DdsTextureFormat.Bgrx8 or DdsTextureFormat.RgbF32 or DdsTextureFormat.R11G11B10F or DdsTextureFormat.Rgb9E5 => row[x] with { A = 255 },
                    DdsTextureFormat.Rg8 or DdsTextureFormat.Rg16 => row[x] with { B = 0, A = 255 },
                    _ => row[x],
                };
            }
        }

        return expected;
    }

    private static double Psnr(Raster<Rgba8> a, Raster<Rgba8> b, DdsTextureFormat format)
    {
        int[] channels = format switch
        {
            DdsTextureFormat.Bc4 => [0],
            DdsTextureFormat.Bc5 => [0, 1],
            DdsTextureFormat.Bc1 => [0, 1, 2],
            _ => [0, 1, 2, 3],
        };
        using Raster<Gray8> gray = format == DdsTextureFormat.Bc4 ? a.As<Gray8>() : Raster.Create<Gray8>(1, 1);
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
                    int expected = format == DdsTextureFormat.Bc4 ? gray.Row(y)[x].L : pa[(x * 4) + c];
                    double d = expected - pb[(x * 4) + c];
                    sum += d * d;
                    count++;
                }
            }
        }

        return 10 * Math.Log10(255.0 * 255.0 / Math.Max(sum / count, 1e-9));
    }
}
