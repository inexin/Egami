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
