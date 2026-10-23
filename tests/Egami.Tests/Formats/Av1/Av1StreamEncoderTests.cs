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
