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

    [Fact]
    public void Encode()
    {
        using SampleFiles files = new();
        List<YuvRaster> pictures = [];
        using (Av1StreamDecoder source = new())
        {
            foreach (byte[] unit in IvfFrames("clip-av1.ivf"))
            {
                source.Send(unit);
                while (source.NextFrame is Av1FrameInfo next)
                {
                    YuvRaster picture = YuvRaster.Create(next.Width, next.Height, next.Chroma, YuvColor.Bt601Limited);
                    source.TryReceiveFrame(picture, out _);
                    pictures.Add(picture);
                }
            }
        }

        List<byte[]> units = [];

        #region av1-encode
        // Pictures of one size, here decoded from a clip; Egami.Video writes WebM, MP4 and IVF and plans the frames for you.
        using Av1StreamEncoder encoder = new(new Av1SequenceOptions { Width = pictures[0].Width, Height = pictures[0].Height });
        units.Add(encoder.Encode(pictures[0], new Av1FramePlan { Kind = Av1FrameKind.Key, Quality = 60 }).Data.ToArray());

        // A hidden frame from four pictures ahead goes into slot 1; the three pictures between read it as ALTREF (from the future)
        // and the frame before as LAST, then the hidden frame is shown when its time comes.
        Av1FramePlan between = new() { Quality = 60, ReferenceSlots = [0, 0, 0, 0, 1, 1, 1] };
        encoder.Encode(pictures[4], new Av1FramePlan { Quality = 55, Show = false, OrderHint = 4, RefreshSlots = 0b10 });
        for (int i = 1; i < 4; i++)
        {
            Av1EncodedFrame frame = encoder.Encode(pictures[i], between); // the first unit also holds the hidden frame
            units.Add(frame.Data.ToArray()); // an MP4 sample or a Matroska block; frame.LumaPsnr says how close it came
        }

        units.Add(encoder.ShowExisting(1).Data.ToArray());
        #endregion

        using Av1StreamDecoder decoder = new();
        foreach (byte[] unit in units)
        {
            decoder.Send(unit);
            Assert.NotNull(decoder.NextFrame);
        }

        Assert.Equal(5, units.Count);
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
