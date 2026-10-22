using System.Buffers.Binary;

using Egami.Formats.Webp;
using Egami.Pixels;

namespace Egami.Tests.Formats.Webp;

/// <summary>
/// Writes IVF files from the VP8 stream encoder, each with the frames Egami decodes from it, for tools/check-vp8-encoder.py to
/// decode with libvpx. Runs only when EGAMI_INTEROP_DIR is set.
/// </summary>
public class Vp8EncoderInteropFiles
{
    /// <summary>The streams: a clip to encode and each frame's plan (null to leave the frame out).</summary>
    internal static IEnumerable<(string Name, string Clip, Func<int, Vp8FramePlan?> Plan)> Streams()
    {
        foreach (int effort in new[] { 0, 2, 4, 6, 8, 10 })
        {
            yield return ($"pan.e{effort}", "v0-pan", _ => new Vp8FramePlan { Effort = effort });
        }

        // A hidden alternate frame from seven frames ahead every eighth frame, and a golden frame every fourth.
        yield return ("altref", "v0-altref", i => i % 8 == 1
            ? new Vp8FramePlan { Show = false, RefreshLast = false, RefreshAlternate = true, AlternateSignBias = true, Effort = 6 }
            : new Vp8FramePlan { RefreshGolden = i % 4 == 0, Effort = 6 });
        yield return ("copies", "v0-pan", i => new Vp8FramePlan
        {
            CopyToGolden = i % 3 == 0 ? Vp8ReferenceCopy.Last : i % 3 == 1 ? Vp8ReferenceCopy.Alternate : Vp8ReferenceCopy.None,
            CopyToAlternate = i % 2 == 0 ? Vp8ReferenceCopy.Golden : Vp8ReferenceCopy.Last,
            GoldenSignBias = i % 5 == 0,
            Effort = 5,
        });
        yield return ("golden-only", "v0-pan", i => new Vp8FramePlan { References = Vp8References.Golden, RefreshGolden = i % 6 == 0, Effort = 4 });
        yield return ("intra-only", "v0-pan", _ => new Vp8FramePlan { References = Vp8References.None, Effort = 3 });
        yield return ("resilient.p8", "v0-pan", _ => new Vp8FramePlan { RefreshEntropy = false, Partitions = 8, Effort = 4 });
        yield return ("keyframes", "v0-pan", i => new Vp8FramePlan { KeyFrame = i % 5 == 0, Effort = 4 });
        yield return ("q0", "v0-pan", i => i < 8 ? new Vp8FramePlan { Quantizer = 0, Effort = 4 } : null);
        yield return ("q127", "v0-pan", _ => new Vp8FramePlan { Quantizer = 127, Effort = 4 });
        yield return ("quality", "v0-pan", i => new Vp8FramePlan { Quality = (i * 37) % 101, Effort = 7 });
        yield return ("odd-97x61", "v0-odd-97x61", _ => new Vp8FramePlan { Effort = 8 });
        yield return ("odd-17x9", "v0-odd-17x9", _ => new Vp8FramePlan { Effort = 10 });
    }

    [Fact]
    public void WriteFiles()
    {
        string? directory = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        Assert.SkipUnless(directory is not null, "EGAMI_INTEROP_DIR is not set.");
        directory = Path.Combine(directory!, "vp8");
        Directory.CreateDirectory(directory);
        foreach ((string name, string clip, Func<int, Vp8FramePlan?> plan) in Streams())
        {
            List<YuvRaster<byte>> source = Vp8StreamEncoderTests.Source(clip);
            int w = source[0].Width, h = source[0].Height;
            List<byte[]> packets = [];
            using MemoryStream frames = new();
            using (Vp8StreamEncoder encoder = new(w, h))
            using (Vp8StreamDecoder decoder = new())
            using (YuvRaster<byte> decoded = YuvRaster.Create(w, h, ChromaFormat.Yuv420, YuvColor.Bt601Limited))
            {
                for (int i = 0; i < source.Count; i++)
                {
                    if (plan(i) is not { } p)
                    {
                        continue;
                    }

                    byte[] packet = encoder.Encode(source[i], p).Data.ToArray();
                    packets.Add(packet);
                    if (decoder.Decode(packet).IsShown)
                    {
                        decoder.CopyFrameTo(decoded);
                        foreach (YuvPlane<byte> plane in new[] { decoded.Y, decoded.U!, decoded.V! })
                        {
                            for (int y = 0; y < plane.Height; y++)
                            {
                                frames.Write(plane.Row(y));
                            }
                        }
                    }
                }
            }

            File.WriteAllBytes(Path.Combine(directory, name + ".ivf"), Ivf(packets, w, h));
            File.WriteAllBytes(Path.Combine(directory, name + ".yuv"), frames.ToArray());
            source.ForEach(f => f.Dispose());
        }
    }

    /// <summary>Wraps VP8 frames in an IVF file at 30 frames a second.</summary>
    internal static byte[] Ivf(List<byte[]> packets, int width, int height)
    {
        using MemoryStream file = new();
        Span<byte> header = stackalloc byte[32];
        header.Clear();
        "DKIF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 32);
        "VP80"u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)height);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 30);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)packets.Count);
        file.Write(header);
        Span<byte> frame = stackalloc byte[12];
        for (int i = 0; i < packets.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(frame, packets[i].Length);
            BinaryPrimitives.WriteInt64LittleEndian(frame[4..], i);
            file.Write(frame);
            file.Write(packets[i]);
        }

        return file.ToArray();
    }
}
