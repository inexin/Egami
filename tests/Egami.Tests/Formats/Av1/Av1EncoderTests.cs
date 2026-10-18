using Egami.Formats.Av1;
using Egami.Pixels;

namespace Egami.Tests.Formats.Av1;

/// <summary>
/// The AV1 intra encoder's round trip: every stream decodes in Egami's decoder to exactly the encoder's reconstruction, and a
/// lossless stream to the source.
/// </summary>
public class Av1EncoderTests
{
    public static TheoryData<int, int, int, ChromaFormat> Shapes() => new()
    {
        { 1, 1, 8, ChromaFormat.Yuv444 },
        { 7, 5, 8, ChromaFormat.Yuv420 },
        { 64, 64, 8, ChromaFormat.Yuv420 },
        { 129, 67, 8, ChromaFormat.Yuv420 },
        { 100, 37, 10, ChromaFormat.Yuv444 },
        { 33, 70, 12, ChromaFormat.Yuv422 },
        { 90, 41, 10, ChromaFormat.Yuv400 },
        { 65, 65, 8, ChromaFormat.Yuv420 },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void LosslessFramesDecodeToTheSource(int width, int height, int bitDepth, ChromaFormat chroma)
    {
        Av1EncoderInput input = Picture(width, height, bitDepth, chroma, seed: width + height);
        Av1Encoder encoded = Av1Encoder.Encode(input, new Av1EncoderSettings { Lossless = true });
        Av1Frame decoded = Av1Decoder.DecodeFirstFrame(encoded.Stream, "AV1", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(decoded.TileDataDamaged);
        AssertPlanes(input, decoded, input.Planes, "source");
        AssertSame(encoded.Reconstruction, decoded);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void LossyFramesDecodeToTheReconstruction(int width, int height, int bitDepth, ChromaFormat chroma)
    {
        Av1EncoderInput input = Picture(width, height, bitDepth, chroma, seed: (width * 3) + height);
        foreach (int q in new[] { 1, 40, 120, 255 })
        {
            Av1Encoder encoded = Av1Encoder.Encode(input, new Av1EncoderSettings { QIndex = q });
            Av1Frame decoded = Av1Decoder.DecodeFirstFrame(encoded.Stream, "AV1", cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(decoded.TileDataDamaged);
            AssertSame(encoded.Reconstruction, decoded);
        }
    }

    [Theory]
    [InlineData(40, 8, ChromaFormat.Yuv420)]
    [InlineData(120, 8, ChromaFormat.Yuv420)]
    [InlineData(200, 10, ChromaFormat.Yuv444)]
    [InlineData(0, 8, ChromaFormat.Yuv444)]
    public void EstimatedRatesMatchTheCodedBits(int q, int bitDepth, ChromaFormat chroma)
    {
        Av1EncoderInput input = Picture(320, 192, bitDepth, chroma, seed: q);
        Av1Encoder encoded = Av1Encoder.Encode(input, new Av1EncoderSettings { QIndex = q, Lossless = q == 0 });
        double error = Math.Abs(encoded.EstimatedBits - encoded.TileBits) / encoded.TileBits;
        Assert.True(error < 0.03, $"Estimated {encoded.EstimatedBits:F0} bits, coded {encoded.TileBits:F0}.");
    }

    [Fact]
    public void LossyFramesShrinkAsTheQuantizerGrows()
    {
        Av1EncoderInput input = Picture(160, 120, 8, ChromaFormat.Yuv420, seed: 3);
        int previous = int.MaxValue;
        foreach (int q in new[] { 20, 80, 160, 240 })
        {
            int size = Av1Encoder.Encode(input, new Av1EncoderSettings { QIndex = q }).Stream.Length;
            Assert.True(size < previous, $"q {q}: {size} bytes, not below {previous}.");
            previous = size;
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void PaletteFramesDecodeToTheReconstruction(int width, int height, int bitDepth, ChromaFormat chroma)
    {
        Av1EncoderInput input = ScreenPicture(width, height, bitDepth, chroma, seed: width + (height * 5));
        foreach (int q in new[] { 0, 60, 200 })
        {
            Av1Encoder encoded = Av1Encoder.Encode(input, new Av1EncoderSettings { QIndex = q, Lossless = q == 0, ScreenContent = true, Effort = 6 });
            Av1Frame decoded = Av1Decoder.DecodeFirstFrame(encoded.Stream, "AV1", cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(decoded.TileDataDamaged);
            AssertSame(encoded.Reconstruction, decoded);
            if (q == 0)
            {
                AssertPlanes(input, decoded, input.Planes, "source");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void IntraBlockCopyFramesDecodeToTheReconstruction(int width, int height, int bitDepth, ChromaFormat chroma)
    {
        Av1EncoderInput input = TiledPicture(width, height, bitDepth, chroma, seed: width + (height * 7));
        foreach (int q in new[] { 0, 60, 200 })
        {
            Av1Encoder encoded = Av1Encoder.Encode(input, new Av1EncoderSettings { QIndex = q, Lossless = q == 0, ScreenContent = true, IntraBlockCopy = true, Effort = 6 });
            Av1Frame decoded = Av1Decoder.DecodeFirstFrame(encoded.Stream, "AV1", cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(decoded.TileDataDamaged);
            AssertSame(encoded.Reconstruction, decoded);
            if (q == 0)
            {
                AssertPlanes(input, decoded, input.Planes, "source");
            }
        }
    }

    [Fact]
    public void RepeatedContentIsCopied()
    {
        Av1EncoderInput input = TiledPicture(512, 256, 8, ChromaFormat.Yuv444, seed: 2);
        Av1Encoder copies = Av1Encoder.Encode(input, new Av1EncoderSettings { QIndex = 60, ScreenContent = true, IntraBlockCopy = true });
        Av1Encoder without = Av1Encoder.Encode(input, new Av1EncoderSettings { QIndex = 60, ScreenContent = true, IntraBlockCopy = false });
        Assert.Contains(copies.Reconstruction.Grid, b => b is { UseIntrabc: true });
        Assert.True(copies.Stream.Length < without.Stream.Length, $"{copies.Stream.Length} bytes with intra block copy, {without.Stream.Length} without.");
    }

    /// <summary>A screen-like picture of one 24x16 tile of strokes repeated, at shifting offsets.</summary>
    internal static Av1EncoderInput TiledPicture(int width, int height, int bitDepth, ChromaFormat chroma, int seed)
    {
        Av1EncoderInput tile = ScreenPicture(24, 16, bitDepth, ChromaFormat.Yuv444, seed);
        Av1EncoderInput noise = Picture(width, height, bitDepth, ChromaFormat.Yuv444, seed);
        int planes = chroma == ChromaFormat.Yuv400 ? 1 : 3;
        int ssx = chroma is ChromaFormat.Yuv420 or ChromaFormat.Yuv422 ? 1 : 0, ssy = chroma == ChromaFormat.Yuv420 ? 1 : 0;
        ushort[][] data = new ushort[planes][];
        for (int plane = 0; plane < planes; plane++)
        {
            int w = plane > 0 ? (width + ssx) >> ssx : width, h = plane > 0 ? (height + ssy) >> ssy : height;
            int sx = plane > 0 ? ssx : 0, sy = plane > 0 ? ssy : 0;
            data[plane] = new ushort[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int fx = x << sx, fy = y << sy;
                    int row = fy / 16, shift = (row * 5) % 24;
                    data[plane][(y * w) + x] = (fx / 24) % 7 == 6
                        ? noise.Planes[plane][(fy * width) + fx]
                        : tile.Planes[plane][((fy % 16) * 24) + ((fx + shift) % 24)];
                }
            }
        }

        return new Av1EncoderInput { Width = width, Height = height, BitDepth = bitDepth, Chroma = chroma, Planes = data };
    }

    [Fact]
    public void ScreenContentIsDetectedAndCodedWithPalettes()
    {
        Av1EncoderInput screen = ScreenPicture(256, 128, 8, ChromaFormat.Yuv444, seed: 1);
        Av1EncoderInput photo = Picture(256, 128, 8, ChromaFormat.Yuv444, seed: 1);
        Assert.True(Av1ScreenContent.Detect(screen.Planes[0], 256, 128, 8).Tools);
        Assert.False(Av1ScreenContent.Detect(photo.Planes[0], 256, 128, 8).Tools);

        Av1Encoder palettes = Av1Encoder.Encode(screen, new Av1EncoderSettings { QIndex = 60 });
        Av1Encoder without = Av1Encoder.Encode(screen, new Av1EncoderSettings { QIndex = 60, ScreenContent = false });
        Assert.Contains(palettes.Reconstruction.Grid, b => b is { PaletteSize: [> 0, _] });
        Assert.Contains(palettes.Reconstruction.Grid, b => b is { PaletteSize: [_, > 0] });
        Assert.True(palettes.Stream.Length < without.Stream.Length, $"{palettes.Stream.Length} bytes with palettes, {without.Stream.Length} without.");
    }

    /// <summary>A picture like a screen: flat rectangles and thin strokes in a few colors per area.</summary>
    internal static Av1EncoderInput ScreenPicture(int width, int height, int bitDepth, ChromaFormat chroma, int seed)
    {
        Random random = new(seed);
        int max = (1 << bitDepth) - 1;
        int planes = chroma == ChromaFormat.Yuv400 ? 1 : 3;
        int ssx = chroma is ChromaFormat.Yuv420 or ChromaFormat.Yuv422 ? 1 : 0, ssy = chroma == ChromaFormat.Yuv420 ? 1 : 0;
        int[] canvas = new int[width * height];
        for (int i = 0; i < (width * height / 64) + 4; i++)
        {
            int x0 = random.Next(width), y0 = random.Next(height);
            int w = random.Next(1, 24), h = random.Next(1, 4) == 1 ? random.Next(1, 24) : random.Next(1, 3);
            int color = random.Next(5);
            for (int y = y0; y < Math.Min(height, y0 + h); y++)
            {
                canvas.AsSpan((y * width) + x0, Math.Min(width - x0, w)).Fill(color);
            }
        }

        ushort[][] data = new ushort[planes][];
        for (int plane = 0; plane < planes; plane++)
        {
            int[] palette = [.. Enumerable.Range(0, 5).Select(_ => random.Next(max + 1))];
            int w = plane > 0 ? (width + ssx) >> ssx : width, h = plane > 0 ? (height + ssy) >> ssy : height;
            data[plane] = new ushort[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    data[plane][(y * w) + x] = (ushort)palette[canvas[((y << (plane > 0 ? ssy : 0)) * width) + (x << (plane > 0 ? ssx : 0))]];
                }
            }
        }

        return new Av1EncoderInput { Width = width, Height = height, BitDepth = bitDepth, Chroma = chroma, Planes = data };
    }

    /// <summary>A picture with smooth gradients, edges and noise, so every predictor and coefficient path is exercised.</summary>
    internal static Av1EncoderInput Picture(int width, int height, int bitDepth, ChromaFormat chroma, int seed)
    {
        Random random = new(seed);
        int max = (1 << bitDepth) - 1;
        int planes = chroma == ChromaFormat.Yuv400 ? 1 : 3;
        int ssx = chroma is ChromaFormat.Yuv420 or ChromaFormat.Yuv422 ? 1 : 0, ssy = chroma == ChromaFormat.Yuv420 ? 1 : 0;
        ushort[][] data = new ushort[planes][];
        for (int plane = 0; plane < planes; plane++)
        {
            int w = plane > 0 ? (width + ssx) >> ssx : width, h = plane > 0 ? (height + ssy) >> ssy : height;
            data[plane] = new ushort[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double v = (0.5 + (0.3 * Math.Sin((x + (plane * 7)) * 0.11)) + (0.2 * Math.Cos(y * 0.07))) * max;
                    if (((x / 9) + (y / 13)) % 4 == 0)
                    {
                        v = max - v;
                    }

                    v += random.Next(-max / 32, (max / 32) + 1);
                    data[plane][(y * w) + x] = (ushort)Math.Clamp((int)v, 0, max);
                }
            }
        }

        return new Av1EncoderInput { Width = width, Height = height, BitDepth = bitDepth, Chroma = chroma, Planes = data };
    }

    private static void AssertPlanes(Av1EncoderInput input, Av1Frame decoded, ushort[][] expected, string what)
    {
        for (int plane = 0; plane < expected.Length; plane++)
        {
            int ssx = plane > 0 ? decoded.Sequence.SubsamplingX : 0, ssy = plane > 0 ? decoded.Sequence.SubsamplingY : 0;
            int w = (input.Width + ssx) >> ssx, h = (input.Height + ssy) >> ssy;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int want = expected[plane][(y * w) + x], got = decoded.Planes![plane][(y * decoded.Strides[plane]) + x];
                    if (want != got)
                    {
                        Assert.Fail($"Plane {plane} at {x}, {y} decodes to {got}, not the {what}'s {want}.");
                    }
                }
            }
        }
    }

    internal static void AssertSame(Av1Frame expected, Av1Frame actual)
    {
        int width = expected.Header.UpscaledWidth, height = expected.Header.FrameHeight;
        for (int plane = 0; plane < expected.Planes!.Length; plane++)
        {
            int ssx = plane > 0 ? expected.Sequence.SubsamplingX : 0, ssy = plane > 0 ? expected.Sequence.SubsamplingY : 0;
            int w = (width + ssx) >> ssx, h = (height + ssy) >> ssy;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int want = expected.Planes[plane][(y * expected.Strides[plane]) + x], got = actual.Planes![plane][(y * actual.Strides[plane]) + x];
                    if (want != got)
                    {
                        Assert.Fail($"Plane {plane} at {x}, {y} decodes to {got}, not the encoder's reconstruction {want}.");
                    }
                }
            }
        }
    }
}
