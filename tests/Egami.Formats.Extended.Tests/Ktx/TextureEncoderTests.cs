using Egami.Compression;
using Egami.Formats.Extended.Textures.Blocks;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Ktx;

/// <summary>
/// The ETC, EAC, ASTC and UASTC block encoders the KTX writer will use: each encodes a 120 × 120 crop of the duck photo, the matching
/// decoder decodes it, and the PSNR must reach what the encoder reached when it was written (a little below, so harmless changes
/// pass). Higher efforts must not do worse, and the blocks must not depend on the number of threads.
/// </summary>
public class TextureEncoderTests
{
    private const int Size = 120;

    private static readonly int[] Efforts = [0, 2, 4, 6];

    /// <summary>The endpoint ranges of the opaque UASTC modes other than 1 and 5.</summary>
    private static readonly int[] CoarseRanges = [7, 8, 11, 12, 18, 19];

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
        { 4, 4, 4, false, 38.6 },
        { 4, 4, 6, false, 38.8 },
        { 5, 4, 4, false, 37.2 },
        { 5, 5, 4, false, 35.2 },
        { 6, 5, 4, false, 33.7 },
        { 6, 6, 0, false, 29.3 },
        { 6, 6, 4, false, 32.1 },
        { 6, 6, 6, false, 32.4 },
        { 6, 6, 4, true, 32.1 },
        { 8, 5, 4, false, 31.4 },
        { 8, 6, 4, false, 30.1 },
        { 8, 8, 4, false, 28.3 },
        { 10, 5, 4, false, 30.2 },
        { 10, 6, 4, false, 29.3 },
        { 10, 8, 4, false, 27.7 },
        { 10, 10, 4, false, 26.3 },
        { 12, 10, 4, false, 25.4 },
        { 12, 12, 0, false, 22.6 },
        { 12, 12, 4, false, 24.5 },
        { 12, 12, 6, false, 24.7 },
    };

    public static TheoryData<string, int, double> EtcCases => new()
    {
        // Format, effort, minimum PSNR (RGB, or RGBA for the formats with alpha, or the EAC channel).
        { "etc1", 0, 31.6 },
        { "etc1", 4, 32.65 },
        { "etc2", 0, 31.6 },
        { "etc2", 4, 32.75 },
        { "a1", 4, 33.1 },
        { "rgba", 0, 32.85 },
        { "rgba", 4, 34.0 },
        { "r11", 0, 39.0 },
        { "r11", 4, 39.9 },
        { "r11-signed", 4, 39.9 },
    };

    public static TheoryData<int, double, double> UastcCases => new()
    {
        // Effort, minimum PSNR (RGBA), minimum PSNR (RGB).
        { 0, 33.8, 33.9 },
        { 2, 37.4, 36.7 },
        { 4, 37.7, 37.0 },
        { 6, 37.9, 37.2 },
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
    [InlineData("etc1", 6, 32.8)]
    [InlineData("etc2", 6, 32.95)]
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

    [Theory]
    [MemberData(nameof(UastcCases))]
    public void UastcReachesItsQuality(int effort, double minimumRgba, double minimumRgb)
    {
        byte[] decoded = DecodeUastc(EncodeUastc(Duck.Value, effort, parallel: false));
        double rgba = Psnr(Duck.Value, decoded, 4, 255);
        double rgb = Psnr(Duck.Value, decoded, 3, 255);
        Assert.True(rgba >= minimumRgba && rgb >= minimumRgb, $"CASE e{effort}: PSNR {rgba:F2} dB RGBA, {rgb:F2} dB RGB, below {minimumRgba} / {minimumRgb}");
    }

    [Fact]
    public void UastcHigherEffortsDoNoWorse()
    {
        double[] psnr = [.. Efforts.Select(e => Psnr(Duck.Value, DecodeUastc(EncodeUastc(Duck.Value, e, parallel: false)), 4, 255))];
        for (int i = 1; i < psnr.Length; i++)
        {
            Assert.True(psnr[i] >= psnr[i - 1] - 0.05, string.Join(", ", psnr));
        }
    }

    [Fact]
    public void UastcBlocksDoNotDependOnThreads()
    {
        Assert.Equal(EncodeUastc(Duck.Value, 4, parallel: false), EncodeUastc(Duck.Value, 4, parallel: true));
        byte[] blocks = EncodeUastc(Duck.Value, 2, parallel: false);
        byte[] one = (byte[])blocks.Clone();
        byte[] four = (byte[])blocks.Clone();
        UastcEncoder.Rdo(Duck.Value, Size, Size, one, 4f, 2, maxParallelism: 1);
        UastcEncoder.Rdo(Duck.Value, Size, Size, four, 4f, 2, maxParallelism: 4);
        Assert.Equal(one, four);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    public void UastcScoresWhatTheDecoderDecodes(int effort)
    {
        // Every family of modes: the duck with its alpha, opaque, gray and gray with alpha.
        foreach (byte[] image in Variants())
        {
            byte[] texels = new byte[64];
            byte[] block = new byte[16];
            byte[] decoded = new byte[64];
            for (int b = 0; b < (Size / 4) * (Size / 4); b += 7)
            {
                Gather(image, (b % (Size / 4)) * 4, (b / (Size / 4)) * 4, 4, 4, texels);
                long error = UastcEncoder.Encode(texels, block, effort);
                Assert.True(Uastc.Unpack(block, decoded));
                Assert.Equal(error, SquaredError(texels, decoded));
            }
        }
    }

    [Fact]
    public void UastcReachesEveryMode()
    {
        var used = new HashSet<int>();
        foreach (byte[] image in Variants())
        {
            byte[] blocks = EncodeUastc(image, 6, parallel: true);
            for (int b = 0; b < blocks.Length; b += 16)
            {
                used.Add(Uastc.Mode(blocks[b]));
            }
        }

        // Blocks crafted for the modes a photo rarely picks.
        foreach (byte[] texels in CraftedBlocks())
        {
            byte[] block = new byte[16];
            UastcEncoder.Encode(texels, block, 6);
            used.Add(Uastc.Mode(block[0]));
        }

        Assert.Equal(Enumerable.Range(0, Uastc.Modes), used.Order());
    }

    [Fact]
    public void UastcWritesConstantBlocksAsSolid()
    {
        byte[] texels = new byte[64];
        for (int i = 0; i < 64; i += 4)
        {
            (texels[i], texels[i + 1], texels[i + 2], texels[i + 3]) = (12, 200, 99, 180);
        }

        byte[] block = new byte[16];
        Assert.Equal(0, UastcEncoder.Encode(texels, block, 4));
        Assert.Equal(8, Uastc.Mode(block[0]));
        byte[] decoded = new byte[64];
        Assert.True(Uastc.Unpack(block, decoded));
        Assert.Equal(texels, decoded);
    }

    [Fact]
    public void UastcFillsTheTranscoderHints()
    {
        byte[] opaque = (byte[])Duck.Value.Clone();
        for (int i = 3; i < opaque.Length; i += 4)
        {
            opaque[i] = 255;
        }

        foreach (byte[] image in new[] { opaque, Duck.Value })
        {
            byte[] blocks = EncodeUastc(image, 4, parallel: true);
            int interpolated = 0, hint0 = 0, hint1 = 0, biased = 0;
            byte[] expected = new byte[64];
            byte[] actual = new byte[64];
            for (int b = 0; b < blocks.Length; b += 16)
            {
                byte[] block = blocks[b..(b + 16)];
                int mode = Uastc.Mode(block[0]);
                if (mode == 8)
                {
                    continue;
                }

                // The hints follow the mode: BC1H0, BC1H1 (not in modes 10 to 12), the ETC1 flip, mode and tables, then the bias.
                interpolated++;
                int at = Uastc.ModeLengths[mode];
                bool hasHint1 = mode is not (10 or 11 or 12);
                hint0 += Bit(block, at);
                hint1 += hasHint1 ? Bit(block, at + 1) : 0;
                if (hasHint1)
                {
                    int bias = 0;
                    for (int i = 0; i < 5; i++)
                    {
                        bias |= Bit(block, at + 10 + i) << i;
                    }

                    biased += bias != 13 ? 1 : 0;
                }

                // Hints only guide transcoders: clearing them leaves the decoded texels as they were.
                Assert.True(Uastc.Unpack(block, expected));
                for (int i = at; i < at + Uastc.HintBits[mode]; i++)
                {
                    block[i >> 3] &= (byte)~(1 << (i & 7));
                }

                Assert.True(Uastc.Unpack(block, actual));
                Assert.Equal(expected, actual);
            }

            Assert.True(hint0 > interpolated / 10, $"BC1H0 in {hint0} of {interpolated} blocks");
            Assert.True(image != opaque || hint1 > interpolated / 10, $"BC1H1 in {hint1} of {interpolated} blocks");
            Assert.True(biased > interpolated / 20, $"an ETC1 bias other than 13 in {biased} of {interpolated} blocks");
        }

        static int Bit(byte[] block, int i) => (block[i >> 3] >> (i & 7)) & 1;
    }

    [Fact]
    public void UastcRdoShrinksTheZstandardStream()
    {
        byte[] blocks = EncodeUastc(Duck.Value, 4, parallel: false);
        double psnr = Psnr(Duck.Value, DecodeUastc(blocks), 4, 255);
        int size = ZstandardSize(blocks);
        byte[] untouched = (byte[])blocks.Clone();
        UastcEncoder.Rdo(Duck.Value, Size, Size, untouched, 0f, 4);
        Assert.Equal(blocks, untouched);

        int previous = size;
        foreach ((float lambda, double maxLoss) in new[] { (4f, 0.4), (16f, 2.0) })
        {
            byte[] rdo = (byte[])blocks.Clone();
            UastcEncoder.Rdo(Duck.Value, Size, Size, rdo, lambda, 4);
            byte[] decoded = DecodeUastc(rdo);
            double loss = psnr - Psnr(Duck.Value, decoded, 4, 255);
            int compressed = ZstandardSize(rdo);
            Assert.True(compressed < previous, $"lambda {lambda}: {compressed} bytes, not below {previous}");
            Assert.True(loss <= maxLoss, $"lambda {lambda}: PSNR down {loss:F2} dB, more than {maxLoss}");
            previous = compressed;
        }
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

    private static byte[] EncodeUastc(byte[] image, int effort, bool parallel)
    {
        int columns = Size / 4;
        byte[] blocks = new byte[columns * columns * 16];
        void Row(int by)
        {
            byte[] texels = new byte[64];
            for (int bx = 0; bx < columns; bx++)
            {
                Gather(image, bx * 4, by * 4, 4, 4, texels);
                UastcEncoder.Encode(texels, blocks.AsSpan(((by * columns) + bx) * 16, 16), effort);
            }
        }

        Run(columns, parallel, Row);
        return blocks;
    }

    private static byte[] DecodeUastc(byte[] blocks)
    {
        int columns = Size / 4;
        byte[] image = new byte[Size * Size * 4];
        byte[] texels = new byte[64];
        for (int b = 0; b < blocks.Length / 16; b++)
        {
            Assert.True(Uastc.Unpack(blocks.AsSpan(b * 16, 16), texels));
            Scatter(texels, (b % columns) * 4, (b / columns) * 4, 4, 4, image);
        }

        return image;
    }

    private static int ZstandardSize(byte[] data) => Zstandard.Compress(data, new byte[Zstandard.MaxCompressedLength(data.Length)]);

    private static long SquaredError(byte[] expected, byte[] actual)
    {
        long sum = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            int d = expected[i] - actual[i];
            sum += d * d;
        }

        return sum;
    }

    /// <summary>The duck as it is, opaque, gray and gray with its alpha, so every family of UASTC modes is used.</summary>
    private static IEnumerable<byte[]> Variants()
    {
        yield return Duck.Value;
        for (int variant = 0; variant < 3; variant++)
        {
            byte[] image = (byte[])Duck.Value.Clone();
            for (int i = 0; i < image.Length; i += 4)
            {
                if (variant > 0)
                {
                    byte y = (byte)(((image[i] * 77) + (image[i + 1] * 150) + (image[i + 2] * 29) + 128) >> 8);
                    image[i] = image[i + 1] = image[i + 2] = y;
                }

                if (variant < 2)
                {
                    image[i + 3] = 255;
                }
            }

            yield return image;
        }
    }

    /// <summary>Blocks for the modes a photo rarely picks: a constant block (mode 8), and two colours only mode 1 holds exactly.</summary>
    private static IEnumerable<byte[]> CraftedBlocks()
    {
        byte[] solid = new byte[64];
        solid.AsSpan().Fill(77);
        yield return solid;

        // Values no endpoint range but the 256-level one holds, so of the opaque modes only 1 and 5 (tried after it) are exact.
        byte[] offGrid = [.. Enumerable.Range(0, 256).Select(v => (byte)v)
            .Where(v => CoarseRanges.All(r => Array.IndexOf(AstcIse.ColorUnquantized[r], v) < 0))];
        byte[] two = new byte[64];
        for (int t = 0; t < 16; t++)
        {
            int first = (t * 7) % 3 == 0 ? 0 : offGrid.Length - 3;
            (two[t * 4], two[(t * 4) + 1], two[(t * 4) + 2], two[(t * 4) + 3]) = (offGrid[first], offGrid[first + 1], offGrid[first + 2], 255);
        }

        yield return two;
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
    private static void Gather(int x0, int y0, int width, int height, byte[] texels) => Gather(Duck.Value, x0, y0, width, height, texels);

    private static void Gather(byte[] image, int x0, int y0, int width, int height, byte[] texels)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int source = ((Math.Min(y0 + y, Size - 1) * Size) + Math.Min(x0 + x, Size - 1)) * 4;
                image.AsSpan(source, 4).CopyTo(texels.AsSpan(((y * width) + x) * 4));
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
