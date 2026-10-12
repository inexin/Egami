using System.Buffers.Binary;

using Egami.Formats.Webp;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Vp8
{
    [Fact]
    public void Stream()
    {
        using SampleFiles files = new();
        int shown = 0;

        #region vp8-stream
        // VP8 frames in stream order, here from an IVF file (Egami.Video reads WebM and IVF for you).
        using Vp8StreamDecoder decoder = new() { ConcealErrors = false };
        YuvRaster<byte>? picture = null;
        foreach (byte[] packet in IvfFrames("clip.ivf"))
        {
            Vp8FrameInfo info = decoder.Decode(packet);
            if (!info.IsShown)
            {
                continue; // a hidden frame, typically an alternate reference: it only updates the references
            }

            picture ??= YuvRaster.Create(info.Width, info.Height, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
            decoder.CopyFrameTo(picture); // the picture is the caller's: change it or keep it
            shown++;
        }

        picture?.Dispose();
        #endregion

        Assert.Equal(40, shown);
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
