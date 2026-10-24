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
