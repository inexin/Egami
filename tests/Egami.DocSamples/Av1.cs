using System.Buffers.Binary;

using Egami.Formats.Av1;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Av1
{
    [Fact]
    public void Stream()
    {
        using SampleFiles files = new();
        int shown = 0;

        #region av1-stream
        // AV1 temporal units in stream order, here from an IVF file (Egami.Video reads WebM, Matroska, MP4 and IVF for you).
        using Av1StreamDecoder decoder = new(new Av1DecoderOptions { ApplyFilmGrain = true });
        foreach (byte[] temporalUnit in IvfFrames("clip-av1.ivf"))
        {
            decoder.Send(temporalUnit);
            while (decoder.NextFrame is Av1FrameInfo next)
            {
                // A unit can show no frame (a hidden reference), one, or one per spatial layer.
                using YuvRaster picture = next.BitDepth == 8
                    ? YuvRaster.Create(next.Width, next.Height, next.Chroma, YuvColor.Bt601Limited)
                    : YuvRaster.Create(next.Width, next.Height, next.Chroma, next.BitDepth, YuvColor.Bt601Limited);
                decoder.TryReceiveFrame(picture, out Av1FrameInfo info); // the picture is the caller's: change it or keep it
                shown++;
            }
        }
        #endregion

        Assert.Equal(12, shown);
    }

    /// <summary>The frames of an IVF file: a 32-byte header, then each frame's 4-byte size and 8-byte timestamp before its data.</summary>
    private static IEnumerable<byte[]> IvfFrames(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        for (int at = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6)); at + 12 <= file.Length;)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at));
            yield return file.AsSpan(at + 12, size).ToArray();
            at += 12 + size;
        }
    }
}
