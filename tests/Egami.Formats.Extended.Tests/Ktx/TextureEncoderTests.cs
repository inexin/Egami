using Egami.Formats.Extended.Textures.Blocks;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Ktx;

/// <summary>
/// The ETC, EAC and ASTC block encoders the KTX writer will use: each encodes a 120 × 120 crop of the duck photo, the matching
/// decoder decodes it, and the PSNR must reach what the encoder reached when it was written (a little below, so harmless changes
/// pass). Higher efforts must not do worse, and the blocks must not depend on the number of threads.
/// </summary>
public class TextureEncoderTests
{
    private const int Size = 120;

    private static readonly int[] Efforts = [0, 2, 4, 6];

    private static readonly Lazy<byte[]> Duck = new(() =>
    {
        using Raster<Rgba8> photo = Raster.Load<Rgba8>(Path.Combine(AppContext.BaseDirectory, "images", "ops", "duck.png"));
        byte[] rgba = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                Rgba8 p = photo.Row(160 + y)[240 + x];
                int i = ((y * Size) + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2]) = (p.R, p.G, p.B);

                // Alpha: a ramp across, with a band of fully transparent pixels.
                rgba[i + 3] = (byte)(x is >= 50 and < 58 ? 0 : 40 + (x * 215 / (Size - 1)));
            }
        }

        return rgba;
    });

    public static TheoryData<int, int, int, bool, double> AstcCases => new()
    {
        // Footprint width and height, effort, sRGB, minimum PSNR (RGBA).
        { 4, 4, 0, false, 33.9 },
        { 4, 4, 4, false, 38.0 },
        { 4, 4, 6, false, 38.2 },
        { 5, 4, 4, false, 36.6 },
        { 5, 5, 4, false, 34.5 },
        { 6, 5, 4, false, 32.9 },
        { 6, 6, 0, false, 29.2 },
        { 6, 6, 4, false, 31.4 },
        { 6, 6, 6, false, 31.6 },
        { 6, 6, 4, true, 31.4 },
        { 8, 5, 4, false, 30.4 },
        { 8, 6, 4, false, 29.3 },
        { 8, 8, 4, false, 27.5 },
        { 10, 5, 4, false, 29.8 },
        { 10, 6, 4, false, 28.8 },
        { 10, 8, 4, false, 26.8 },
        { 10, 10, 4, false, 25.8 },
        { 12, 10, 4, false, 24.8 },
        { 12, 12, 0, false, 22.5 },
        { 12, 12, 4, false, 23.9 },
        { 12, 12, 6, false, 24.0 },
    };

    public static TheoryData<string, int, double> EtcCases => new()
    {
        // Format, effort, minimum PSNR (RGB, or RGBA for the formats with alpha, or the EAC channel).
        { "etc1", 0, 31.5 },
        { "etc1", 4, 32.5 },
        { "etc2", 0, 31.5 },
        { "etc2", 4, 32.6 },
        { "a1", 4, 33.0 },
        { "rgba", 0, 32.8 },
        { "rgba", 4, 33.9 },
        { "r11", 0, 38.9 },
        { "r11", 4, 39.6 },
        { "r11-signed", 4, 39.6 },
    };

    [Theory]
    [MemberData(nameof(AstcCases))]
    public void AstcReachesItsQuality(int width, int height, int effort, bool srgb, double minimum)
    {
        Astc astc = Astc.For(width, height)!;
        byte[] blocks = EncodeAstc(astc, effort, srgb, parallel: false);
        double psnr = Psnr(Duck.Value, DecodeAstc(astc, blocks, srgb), 4, 255);
        Assert.True(psnr >= minimum, $"CASE {width}x{height} e{effort} {srgb}: PSNR {psnr:F2} dB, below {minimum}");
    }

    [Theory]
    [MemberData(nameof(EtcCases))]
    public void EtcReachesItsQuality(string format, int effort, double minimum)
    {
        double psnr = EtcPsnr(format, effort);
        Assert.True(psnr >= minimum, $"CASE {format} e{effort}: PSNR {psnr:F2} dB, below {minimum}");
    }

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData("etc1", 6, 32.5)]
    [InlineData("etc2", 6, 32.7)]
    public void EtcReachesItsQualityAtEffortSix(string format, int effort, double minimum) => EtcReachesItsQuality(format, effort, minimum);

    [Fact]
    public void HigherEffortsDoNoWorse()
    {
        Astc astc = Astc.For(6, 6)!;
        double[] astcPsnr = [.. Efforts.Select(e => Psnr(Duck.Value, DecodeAstc(astc, EncodeAstc(astc, e, false, false), false), 4, 255))];
        double[] etcPsnr = [.. Efforts.SkipLast(1).Select(e => EtcPsnr("etc2", e))];
        for (int i = 1; i < astcPsnr.Length; i++)
        {
            Assert.True(astcPsnr[i] >= astcPsnr[i - 1] - 0.05, string.Join(", ", astcPsnr));
        }

        // Effort 6 searches ETC exhaustively and is slow: EtcReachesItsQualityAtEffortSix checks it.
        for (int i = 1; i < etcPsnr.Length; i++)
        {
            Assert.True(etcPsnr[i] >= etcPsnr[i - 1] - 0.05, string.Join(", ", etcPsnr));
        }
    }

    [Fact]
    public void BlocksDoNotDependOnThreads()
    {
        foreach ((int w, int h) in new[] { (4, 4), (8, 5), (12, 12) })
        {
            Astc astc = Astc.For(w, h)!;
            Assert.Equal(EncodeAstc(astc, 4, false, parallel: false), EncodeAstc(astc, 4, false, parallel: true));
        }

        Assert.Equal(EncodeEtc("rgba", 4, parallel: false), EncodeEtc("rgba", 4, parallel: true));
        Assert.Equal(EncodeEtc("etc2", 4, parallel: false), EncodeEtc("etc2", 4, parallel: true));
    }

    [Fact]
    public void AstcWritesConstantBlocksAsVoidExtent()
    {
        Astc astc = Astc.For(8, 6)!;
        byte[] texels = new byte[astc.Texels * 4];
        for (int i = 0; i < texels.Length; i += 4)
        {
            (texels[i], texels[i + 1], texels[i + 2], texels[i + 3]) = (12, 200, 99, 180);
        }

        byte[] block = new byte[16];
        AstcEncoder.Encode(astc, texels, block, 4, srgb: false);
        Assert.Equal(0x1FC, block[0] | ((block[1] & 1) << 8));
        byte[] decoded = new byte[texels.Length];
        astc.DecodeLdr(block, decoded, srgb: false);
        Assert.Equal(texels, decoded);
    }

    [Fact]
    public void PunchThroughKeepsTransparency()
    {
        byte[] decoded = DecodeEtc("a1", EncodeEtc("a1", 4, parallel: false));
        for (int i = 3; i < decoded.Length; i += 4)
        {
            Assert.Equal(Duck.Value[i] < 128 ? 0 : 255, decoded[i]);
        }
    }

    private static byte[] EncodeAstc(Astc astc, int effort, bool srgb, bool parallel)
    {
        int columns = (Size + astc.Width - 1) / astc.Width;
        int rows = (Size + astc.Height - 1) / astc.Height;
        byte[] blocks = new byte[columns * rows * 16];
        void Row(int by)
        {
            byte[] texels = new byte[astc.Texels * 4];
            for (int bx = 0; bx < columns; bx++)
            {
                Gather(bx * astc.Width, by * astc.Height, astc.Width, astc.Height, texels);
                AstcEncoder.Encode(astc, texels, blocks.AsSpan(((by * columns) + bx) * 16, 16), effort, srgb);
            }
        }

        Run(rows, parallel, Row);
        return blocks;
    }

    private static byte[] DecodeAstc(Astc astc, byte[] blocks, bool srgb)
    {
        int columns = (Size + astc.Width - 1) / astc.Width;
        byte[] image = new byte[Size * Size * 4];
        byte[] texels = new byte[astc.Texels * 4];
        for (int b = 0; b < blocks.Length / 16; b++)
        {
            astc.DecodeLdr(blocks.AsSpan(b * 16, 16), texels, srgb);
            Scatter(texels, (b % columns) * astc.Width, (b / columns) * astc.Height, astc.Width, astc.Height, image);
        }

        return image;
    }

    private static double EtcPsnr(string format, int effort)
    {
        byte[] decoded = DecodeEtc(format, EncodeEtc(format, effort, parallel: false));
        return format switch
        {
            "etc1" or "etc2" => Psnr(Duck.Value, decoded, 3, 255),
            "a1" => Psnr(Duck.Value, decoded, 3, 255, skipTransparent: true),
            "rgba" => Psnr(Duck.Value, decoded, 4, 255),
            _ => Psnr(Duck.Value, decoded, 1, 255),
        };
    }

    private static int BlockBytes(string format) => format == "rgba" ? 16 : 8;

    private static byte[] EncodeEtc(string format, int effort, bool parallel)
    {
        int columns = Size / 4;
        int unit = BlockBytes(format);
        byte[] blocks = new byte[columns * columns * unit];
        void Row(int by)
        {
            byte[] texels = new byte[64];
            ushort[] red = new ushort[16];
            for (int bx = 0; bx < columns; bx++)
            {
                Gather(bx * 4, by * 4, 4, 4, texels);
                Span<byte> block = blocks.AsSpan(((by * columns) + bx) * unit, unit);
                switch (format)
                {
                    case "etc1":
                    case "etc2":
                        EtcEncoder.EncodeRgb(texels, block, effort, etc2: format == "etc2", punchThrough: false);
                        break;
                    case "a1":
                        EtcEncoder.EncodeRgb(texels, block, effort, etc2: true, punchThrough: true);
                        break;
                    case "rgba":
                        EtcEncoder.EncodeRgba(texels, block, effort);
                        break;
                    default:
                        for (int i = 0; i < 16; i++)
                        {
                            red[i] = (ushort)(texels[i * 4] * 257);
                        }

                        EtcEncoder.EncodeEac11(red, 1, block, effort, signed: format == "r11-signed");
                        break;
                }
            }
        }

        Run(columns, parallel, Row);
        return blocks;
    }

    /// <summary>Decodes to RGBA; the EAC formats put their channel, scaled to 8 bits, in red.</summary>
    private static byte[] DecodeEtc(string format, byte[] blocks)
    {
        int columns = Size / 4;
        int unit = BlockBytes(format);
        byte[] image = new byte[Size * Size * 4];
        byte[] texels = new byte[64];
        ushort[] red = new ushort[16];
        for (int b = 0; b < blocks.Length / unit; b++)
        {
            ReadOnlySpan<byte> block = blocks.AsSpan(b * unit, unit);
            texels.AsSpan().Fill(255);
            switch (format)
            {
                case "etc1":
                case "etc2":
                    Etc.DecodeRgb(block, texels, 4, punchThrough: false);
                    break;
                case "a1":
                    Etc.DecodeRgb(block, texels, 4, punchThrough: true);
                    break;
                case "rgba":
                    Etc.DecodeRgb(block[8..], texels, 4, punchThrough: false);
                    Etc.DecodeEac8(block, texels.AsSpan(3), 4);
                    break;
                default:
                    Etc.DecodeEac11(block, red, 1, signed: format == "r11-signed");
                    for (int i = 0; i < 16; i++)
                    {
                        texels[i * 4] = (byte)((red[i] + 128) / 257);
                    }

                    break;
            }

            Scatter(texels, (b % columns) * 4, (b / columns) * 4, 4, 4, image);
        }

        return image;
    }

    /// <summary>Copies a block of texels, repeating the last row and column past the image's edge.</summary>
    private static void Gather(int x0, int y0, int width, int height, byte[] texels)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int source = ((Math.Min(y0 + y, Size - 1) * Size) + Math.Min(x0 + x, Size - 1)) * 4;
                Duck.Value.AsSpan(source, 4).CopyTo(texels.AsSpan(((y * width) + x) * 4));
            }
        }
    }

    private static void Scatter(byte[] texels, int x0, int y0, int width, int height, byte[] image)
    {
        for (int y = 0; y < height && y0 + y < Size; y++)
        {
            for (int x = 0; x < width && x0 + x < Size; x++)
            {
                texels.AsSpan(((y * width) + x) * 4, 4).CopyTo(image.AsSpan((((y0 + y) * Size) + x0 + x) * 4));
            }
        }
    }

    private static void Run(int count, bool parallel, Action<int> body)
    {
        if (parallel)
        {
            // Backwards and spread over threads, so any state one block leaves for the next would show.
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => body(count - 1 - i));
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                body(i);
            }
        }
    }

    private static double Psnr(byte[] expected, byte[] actual, int channels, int peak, bool skipTransparent = false)
    {
        double sum = 0;
        long count = 0;
        for (int i = 0; i < expected.Length; i += 4)
        {
            if (skipTransparent && expected[i + 3] < 128)
            {
                continue;
            }

            for (int c = 0; c < channels; c++)
            {
                double d = expected[i + c] - actual[i + c];
                sum += d * d;
                count++;
            }
        }

        return sum == 0 ? 99 : 10 * Math.Log10(peak * (double)peak * count / sum);
    }
}
