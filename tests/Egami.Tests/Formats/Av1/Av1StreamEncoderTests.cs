using Egami.Formats.Av1;
using Egami.Pixels;

namespace Egami.Tests.Formats.Av1;

/// <summary>
/// The AV1 stream encoder's round trip: every temporal unit decodes in Egami's decoder to exactly the encoder's reconstruction, inter
/// frames cost less than key frames of moving pictures, and hidden frames show later with show_existing_frame.
/// </summary>
public class Av1StreamEncoderTests
{
    private static readonly string Photo = Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "duck-q100.jpg");

    [Theory]
    [InlineData(160, 96, 8, ChromaFormat.Yuv420, 2)]
    [InlineData(160, 96, 8, ChromaFormat.Yuv420, 4)]
    [InlineData(150, 70, 8, ChromaFormat.Yuv420, 6)]
    [InlineData(96, 64, 10, ChromaFormat.Yuv444, 4)]
    [InlineData(70, 50, 8, ChromaFormat.Yuv422, 3)]
    [InlineData(64, 48, 8, ChromaFormat.Yuv400, 2)]
    public void InterFramesDecodeToTheReconstruction(int width, int height, int bitDepth, ChromaFormat chroma, int effort)
    {
        Av1SequenceOptions options = new() { Width = width, Height = height, BitDepth = bitDepth, Chroma = chroma };
        using Av1StreamEncoder encoder = new(options);
        using Av1StreamDecoder decoder = new();
        long keyBits = 0, interBits = 0;
        for (int i = 0; i < 5; i++)
        {
            using YuvRaster frame = Moving(width, height, bitDepth, chroma, i);
            Av1EncodedFrame encoded = encoder.Encode(frame, new Av1FramePlan { Effort = effort, Quality = 60 });
            if (i == 0)
            {
                keyBits = encoded.Bits;
            }
            else
            {
                interBits += encoded.Bits;
            }

            decoder.Send(encoded.Data.Span);
            AssertDecodesToReconstruction(decoder, encoder.Reconstruction!, width, height, bitDepth, chroma);
        }

        Assert.True(interBits / 4 < keyBits / 2, $"Inter frames took {interBits / 4} bits each, the key frame {keyBits}.");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void FramesNothingPredictsFromDecodeToTheReconstruction(int effort)
    {
        Av1SequenceOptions options = new() { Width = 160, Height = 96 };
        using Av1StreamEncoder encoder = new(options);
        using Av1StreamDecoder decoder = new();
        for (int i = 0; i < 4; i++)
        {
            using YuvRaster frame = Moving(160, 96, 8, ChromaFormat.Yuv420, i);
            // Frames 2 and 3 predict from frame 1 and are left out of the slots that matter.
            Av1FramePlan plan = new() { Effort = effort, Quality = 60, Referenced = i < 2, RefreshSlots = i < 2 ? 1 : 2 };
            decoder.Send(encoder.Encode(frame, plan).Data.Span);
            AssertDecodesToReconstruction(decoder, encoder.Reconstruction!, 160, 96, 8, ChromaFormat.Yuv420);
        }
    }

    [Theory]
    [InlineData(4, 8, ChromaFormat.Yuv420)]
    [InlineData(7, 8, ChromaFormat.Yuv420)]
    [InlineData(2, 10, ChromaFormat.Yuv444)]
    public void FramesAreTheSameAtAnyThreadCount(int effort, int bitDepth, ChromaFormat chroma)
    {
        // Four superblock rows in one tile: the rows are decided as a wavefront when there are threads.
        Av1SequenceOptions options = new() { Width = 240, Height = 200, BitDepth = bitDepth, Chroma = chroma };
        using Av1StreamEncoder one = new(options, EgamiContext.Default with { MaxParallelism = 1 });
        using Av1StreamEncoder many = new(options, EgamiContext.Default with { MaxParallelism = 8 });
        for (int i = 0; i < 4; i++)
        {
            using YuvRaster frame = Moving(240, 200, bitDepth, chroma, i);
            Av1FramePlan plan = new() { Effort = effort, Quality = 60 };
            Assert.Equal(one.Encode(frame, plan).Data.ToArray(), many.Encode(frame, plan).Data.ToArray());
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public void ScreenContentKeyFramesAreTheSameAtAnyThreadCount(int effort)
    {
        // Intra block copy may read rows above and to the right; the wavefront must have finished what it reads.
        Av1SequenceOptions options = new() { Width = 512, Height = 320 };
        using Av1StreamEncoder one = new(options, EgamiContext.Default with { MaxParallelism = 1 });
        using Av1StreamEncoder many = new(options, EgamiContext.Default with { MaxParallelism = 8 });
        using Av1StreamDecoder decoder = new();
        for (int i = 0; i < 2; i++)
        {
            using YuvRaster frame = Screen(512, 320, i);
            Av1FramePlan plan = new() { Kind = Av1FrameKind.Key, Effort = effort, Quality = 60, ScreenContent = true };
            byte[] single = one.Encode(frame, plan).Data.ToArray();
            Assert.True(one.Reconstruction!.Header.AllowIntrabc);
            Av1EncodedFrame threaded = many.Encode(frame, plan);
            Assert.Equal(single, threaded.Data.ToArray());
            decoder.Send(threaded.Data.Span);
            AssertDecodesToReconstruction(decoder, many.Reconstruction!, 512, 320, 8, ChromaFormat.Yuv420);
        }
    }

    [Fact]
    public void AnalysisIsTheSameAtAnyThreadCountAndFindsWhatIsReused()
    {
        // A key frame, a hidden frame three ahead and the two between, which predict from both.
        Av1SequenceOptions options = new() { Width = 240, Height = 200 };
        using Av1StreamEncoder one = new(options, EgamiContext.Default with { MaxParallelism = 1 });
        using Av1StreamEncoder many = new(options, EgamiContext.Default with { MaxParallelism = 8 });
        YuvRaster[] pictures = [.. Enumerable.Range(0, 4).Select(i => Moving(240, 200, 8, ChromaFormat.Yuv420, i))];
        Av1AnalysisFrame[] frames =
        [
            new() { Picture = pictures[0] },
            new() { Picture = pictures[3], References = [0] },
            new() { Picture = pictures[1], References = [0, 1] },
            new() { Picture = pictures[2], References = [0, 1] },
        ];
        IReadOnlyList<Av1FrameAnalysis> a = one.Analyze(frames), b = many.Analyze(frames);
        Assert.Equal(a.Select(f => f.Importance), b.Select(f => f.Importance));
        Assert.True(a[0].Importance > 1.5, $"The key frame's importance is {a[0].Importance}.");
        Assert.True(a[1].Importance > 1, $"The hidden frame's importance is {a[1].Importance}.");
        Assert.Equal(1, a[2].Importance);
        Assert.Equal(1, a[3].Importance);
        Assert.Throws<ArgumentException>(() => one.Analyze([new Av1AnalysisFrame { Picture = pictures[0], References = [0] }]));
        foreach (YuvRaster p in pictures)
        {
            p.Dispose();
        }
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(4, 8)]
    public void FramesWithAnAnalysisDecodeToTheReconstruction(int effort, int threads)
    {
        // The key frame codes superblock quantizers from its analysis; the frames after it lower their blocks' multipliers.
        Av1SequenceOptions options = new() { Width = 240, Height = 200 };
        using Av1StreamEncoder encoder = new(options, EgamiContext.Default with { MaxParallelism = threads });
        using Av1StreamDecoder decoder = new();
        YuvRaster[] pictures = [.. Enumerable.Range(0, 4).Select(i => Moving(240, 200, 8, ChromaFormat.Yuv420, i))];
        IReadOnlyList<Av1FrameAnalysis> analysis = encoder.Analyze(
        [
            new() { Picture = pictures[0] },
            new() { Picture = pictures[1], References = [0] },
            new() { Picture = pictures[2], References = [1] },
            new() { Picture = pictures[3], References = [2] },
        ]);
        for (int i = 0; i < 4; i++)
        {
            Av1FramePlan plan = new() { Kind = i == 0 ? Av1FrameKind.Key : Av1FrameKind.Inter, Effort = effort, QIndex = 120, Layer = i == 0 ? 0 : 1, Analysis = analysis[i] };
            decoder.Send(encoder.Encode(pictures[i], plan).Data.Span);
            Assert.Equal(i == 0, encoder.Reconstruction!.Header.DeltaQPresent);
            AssertDecodesToReconstruction(decoder, encoder.Reconstruction!, 240, 200, 8, ChromaFormat.Yuv420);
        }

        foreach (YuvRaster p in pictures)
        {
            p.Dispose();
        }
    }

    [Fact]
    public void ImageTuneKeepsThePerceptualTools()
    {
        Av1SequenceOptions options = new() { Width = 160, Height = 96 };
        using Av1StreamEncoder encoder = new(options);
        using YuvRaster frame = Moving(160, 96, 8, ChromaFormat.Yuv420, 0);
        encoder.Encode(frame, new Av1FramePlan { Kind = Av1FrameKind.Key, Quality = 60, Tune = Av1Tune.Image });
        Assert.True(encoder.Reconstruction!.Header.UsingQmatrix);
        encoder.Encode(frame, new Av1FramePlan { Kind = Av1FrameKind.Key, Quality = 60 });
        Assert.False(encoder.Reconstruction!.Header.UsingQmatrix);
        Assert.False(encoder.Reconstruction!.Header.DeltaQPresent);
    }

    /// <summary>Gets a screen-like picture: a few glyph tiles repeated across the frame, so copies come from far up and right.</summary>
    internal static YuvRaster Screen(int width, int height, int index)
    {
        Random random = new(7 + index);
        byte[][] glyphs = new byte[6][];
        for (int g = 0; g < glyphs.Length; g++)
        {
            glyphs[g] = new byte[64];
            for (int k = 0; k < 64; k++)
            {
                glyphs[g][k] = (byte)(random.Next(2) == 0 ? 30 : 220);
            }
        }

        YuvRaster<byte> raster = YuvRaster.Create(width, height, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = raster.Y.Row(y);
            for (int x = 0; x < width; x++)
            {
                byte[] glyph = glyphs[((x / 8) + (3 * (y / 8)) + index) % glyphs.Length];
                row[x] = glyph[((y % 8) * 8) + (x % 8)];
            }
        }

        foreach (YuvPlane<byte> plane in (YuvPlane<byte>[])[raster.U!, raster.V!])
        {
            for (int y = 0; y < plane.Height; y++)
            {
                plane.Row(y).Fill((byte)(128 + ((y / 16) % 3)));
            }
        }

        return raster;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlobalMotionFollowsPansAndZooms(bool zoom)
    {
        using Av1StreamEncoder encoder = new(new Av1SequenceOptions { Width = 192, Height = 128 });
        using Av1StreamDecoder decoder = new();
        int global = 0;
        for (int i = 0; i < 4; i++)
        {
            using YuvRaster frame = zoom ? Zooming(192, 128, i) : Moving(192, 128, 8, ChromaFormat.Yuv420, i);
            Av1EncodedFrame encoded = encoder.Encode(frame, new Av1FramePlan { Effort = 7, Quality = 50 });
            global += i > 0 && encoder.Reconstruction!.Header.GmType[1] != 0 ? 1 : 0;
            decoder.Send(encoded.Data.Span);
            AssertDecodesToReconstruction(decoder, encoder.Reconstruction!, 192, 128, 8, ChromaFormat.Yuv420);
        }

        Assert.True(global >= 2, $"Only {global} of 3 inter frames found global motion.");
    }

    /// <summary>Gets frame <paramref name="index"/> of a picture zooming into the photo, 3% a frame.</summary>
    internal static YuvRaster Zooming(int width, int height, int index)
    {
        using Raster<Rgba8> photo = Raster.Load<Rgba8>(Photo);
        double scale = Math.Pow(0.97, index);
        int w = (int)Math.Round(width * 1.2 * scale), h = (int)Math.Round(height * 1.2 * scale);
        int x0 = 20 + (((int)(width * 1.2) - w) / 2), y0 = 20 + (((int)(height * 1.2) - h) / 2);
        using Raster<Rgba8> crop = photo.Derive(r => r.Crop(new Rect(x0, y0, w, h)).Resize(width, height));
        return YuvRaster.FromRaster(crop, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
    }

    /// <summary>Gets frame <paramref name="index"/> of a picture panning across the photo, two samples right and one down per frame.</summary>
    internal static YuvRaster Moving(int width, int height, int bitDepth, ChromaFormat chroma, int index)
    {
        using Raster<Rgba8> photo = Raster.Load<Rgba8>(Photo);
        int x0 = 40 + (2 * index), y0 = 30 + index;
        using Raster<Rgba8> crop = photo.Derive(r => r.Crop(new Rect(x0, y0, width, height)));
        return bitDepth == 8
            ? YuvRaster.FromRaster(crop, chroma, YuvColor.Bt601Limited)
            : YuvRaster.FromRaster(crop, chroma, bitDepth, YuvColor.Bt601Limited);
    }

    internal static void AssertDecodesToReconstruction(Av1StreamDecoder decoder, Av1Frame reconstruction, int width, int height, int bitDepth, ChromaFormat chroma)
    {
        using YuvRaster decoded = bitDepth == 8
            ? YuvRaster.Create(width, height, chroma, YuvColor.Bt601Limited)
            : YuvRaster.Create(width, height, chroma, bitDepth, YuvColor.Bt601Limited);
        Assert.True(decoder.TryReceiveFrame(decoded, out _));
        int ssx = reconstruction.Sequence.SubsamplingX, ssy = reconstruction.Sequence.SubsamplingY;
        for (int plane = 0; plane < reconstruction.Header.NumPlanes; plane++)
        {
            int w = plane == 0 ? width : (width + ssx) >> ssx, h = plane == 0 ? height : (height + ssy) >> ssy;
            ushort[] expected = reconstruction.Plane<ushort>(plane);
            int stride = reconstruction.Strides[plane];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int actual = decoded is YuvRaster<byte> b ? (plane == 0 ? b.Y : plane == 1 ? b.U! : b.V!).Row(y)[x]
                        : (plane == 0 ? ((YuvRaster<ushort>)decoded).Y : plane == 1 ? ((YuvRaster<ushort>)decoded).U! : ((YuvRaster<ushort>)decoded).V!).Row(y)[x];
                    if (actual != expected[(y * stride) + x])
                    {
                        Assert.Fail($"Plane {plane} differs at ({x}, {y}): decoded {actual}, reconstructed {expected[(y * stride) + x]}.");
                    }
                }
            }
        }
    }
}
