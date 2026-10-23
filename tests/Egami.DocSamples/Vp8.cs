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

    [Fact]
    public void Encode()
    {
        using SampleFiles files = new();
        List<YuvRaster<byte>> pictures = [];
        using (Vp8StreamDecoder source = new())
        {
            foreach (byte[] packet in IvfFrames("clip.ivf"))
            {
                Vp8FrameInfo shown = source.Decode(packet);
                if (shown.IsShown)
                {
                    YuvRaster<byte> picture = YuvRaster.Create(shown.Width, shown.Height, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
                    source.CopyFrameTo(picture);
                    pictures.Add(picture);
                }
            }
        }

        List<byte[]> packets = [];

        #region vp8-encode
        // 4:2:0 pictures of one size, here decoded from a clip; Egami.Video writes WebM and IVF and plans the frames for you.
        using Vp8StreamEncoder encoder = new(pictures[0].Width, pictures[0].Height);
        for (int i = 0; i < pictures.Count; i++)
        {
            Vp8FramePlan plan = new()
            {
                KeyFrame = i % 30 == 0,     // a key frame a second at 30 frames a second
                RefreshGolden = i % 10 == 0, // and a golden frame to fall back on every tenth
                Quality = 70,
            };
            Vp8EncodedFrame frame = encoder.Encode(pictures[i], plan);
            packets.Add(frame.Data.ToArray()); // a WebM block or an IVF frame; frame.LumaPsnr says how close it came
        }
        #endregion

        using Vp8StreamDecoder decoder = new();
        Assert.All(packets, p => Assert.True(decoder.Decode(p).IsShown));
        Assert.Equal(pictures.Count, packets.Count);
        pictures.ForEach(p => p.Dispose());
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
