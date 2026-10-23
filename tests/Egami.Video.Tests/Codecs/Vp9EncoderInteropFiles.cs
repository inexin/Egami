using System.Buffers.Binary;

using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Codecs.Vp9;

namespace Egami.Video.Tests.Codecs;

/// <summary>
/// Writes IVF files from the VP9 encoder, each with the frames Egami's decoder makes of it, for tools/video/check-vp9-encoder.py
/// to decode with libvpx. Runs only when EGAMI_INTEROP_DIR is set.
/// </summary>
public class Vp9EncoderInteropFiles
{
    /// <summary>The streams the encoder core codes directly: a size, a frame count and each frame's plan.</summary>
    internal static IEnumerable<(string Name, int Width, int Height, int Frames, Func<int, Vp9FramePlan> Plan)> CoreStreams()
    {
        foreach (int effort in new[] { 0, 2, 4, 6, 8, 10 })
        {
            yield return ($"pan.e{effort}", 96, 64, 10, i => i == 0
                ? new Vp9FramePlan { Key = true, Effort = effort, Quantizer = 90 }
                : new Vp9FramePlan { Effort = effort, Quantizer = 90, RefreshFlags = i % 4 == 0 ? 3 : 1 });
        }

        // A hidden frame into ALTREF every sixth frame, the frames after it predicting from it with compound prediction.
        yield return ("hidden", 80, 48, 12, i => i == 0
            ? new Vp9FramePlan { Key = true, Effort = 6 }
            : i % 6 == 1
                ? new Vp9FramePlan { Show = false, RefreshFlags = 4, AltRefAhead = true, FrameContext = 1, Effort = 6 }
                : new Vp9FramePlan { RefreshFlags = 1, AltRefAhead = true, Effort = 6 });
        yield return ("keyframes", 72, 40, 10, i => new Vp9FramePlan { Key = i % 4 == 0, Effort = 4, RefreshFlags = 1 });
        yield return ("resilient", 72, 40, 8, i => new Vp9FramePlan { Key = i == 0, ErrorResilient = true, RefreshContext = false, Effort = 4 });
        yield return ("contexts", 72, 40, 8, i => new Vp9FramePlan { Key = i == 0, FrameContext = i % 4, Effort = 5, RefreshFlags = 1 << (i % 3) });
        // All eight slots: each frame refreshes the next slot and predicts from three older ones.
        yield return ("slots", 64, 48, 12, i => i == 0
            ? new Vp9FramePlan { Key = true, Effort = 4 }
            : new Vp9FramePlan { RefreshFlags = 1 << (i % 8), LastSlot = (i - 1) % 8, GoldenSlot = (i + 5) % 8, AltRefSlot = (i + 3) % 8, Effort = 4 });
        yield return ("q0", 48, 32, 4, i => new Vp9FramePlan { Key = i == 0, Quantizer = 0, Effort = 4 });
        yield return ("q255", 64, 48, 6, i => new Vp9FramePlan { Key = i == 0, Quantizer = 255, Effort = 4 });
        yield return ("q1", 64, 48, 4, i => new Vp9FramePlan { Key = i == 0, Quantizer = 1, Effort = 4 });
        yield return ("aq", 128, 80, 6, i => new Vp9FramePlan { Key = i == 0, Quantizer = 120, AqMode = 1, Effort = 5 });
        yield return ("filter", 96, 64, 6, i => new Vp9FramePlan { Key = i == 0, Quantizer = 200, LoopFilterLevel = 63, Effort = 3 });
        yield return ("odd-97x61", 97, 61, 6, i => new Vp9FramePlan { Key = i == 0, Effort = 8 });
        yield return ("odd-17x9", 17, 9, 6, i => new Vp9FramePlan { Key = i == 0, Effort = 10 });
        yield return ("odd-1x1", 1, 1, 3, i => new Vp9FramePlan { Key = i == 0, Effort = 4 });
        yield return ("tiles2", 520, 72, 4, i => new Vp9FramePlan { Key = i == 0, TileColumnsLog2 = 1, Effort = 2 });
        yield return ("tiles4", 1040, 40, 3, i => new Vp9FramePlan { Key = i == 0, TileColumnsLog2 = 2, Effort = 1 });
    }

    /// <summary>The streams of other formats: a bit depth and subsampling, a size, a frame count and the effort.</summary>
    internal static IEnumerable<(string Name, int BitDepth, int SubX, int SubY, int Width, int Height, int Frames, int Effort)> FormatStreams()
    {
        yield return ("p0.444-8", 8, 0, 0, 72, 40, 5, 4);
        yield return ("p1.422-8", 8, 1, 0, 72, 40, 5, 6);
        yield return ("p1.440-like-odd", 8, 0, 0, 33, 17, 4, 8);
        yield return ("p2.420-10", 10, 1, 1, 72, 40, 6, 4);
        yield return ("p2.420-12", 12, 1, 1, 72, 40, 6, 6);
        yield return ("p3.444-10", 10, 0, 0, 64, 48, 5, 4);
        yield return ("p3.422-12", 12, 1, 0, 61, 35, 5, 8);
    }

    /// <summary>The streams the public encoder codes: superframes from the encoder core's alternate references, alpha and rate control.</summary>
    internal static IEnumerable<(string Name, int Width, int Height, int Frames, VideoEncoderOptions Options)> EncoderStreams()
    {
        yield return ("encoder.altref", 96, 64, 30, new VideoEncoderOptions { Width = 96, Height = 64, Quality = 70 });
        yield return ("encoder.realtime", 96, 64, 12, new VideoEncoderOptions { Width = 96, Height = 64, LatencyMode = LatencyMode.Realtime });
        yield return ("encoder.cbr", 96, 64, 20, new VideoEncoderOptions { Width = 96, Height = 64, RateControl = RateControlMode.ConstantBitrate, Bitrate = 100_000 });
        yield return ("encoder.effort9", 80, 48, 20, new VideoEncoderOptions { Width = 80, Height = 48, Effort = 9, Quality = 40 });
    }

    [Fact]
    public void WriteFiles()
    {
        string? directory = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        Assert.SkipUnless(directory is not null, "EGAMI_INTEROP_DIR is not set.");
        directory = Path.Combine(directory!, "vp9");
        Directory.CreateDirectory(directory);
        foreach ((string name, int w, int h, int count, Func<int, Vp9FramePlan> plan) in CoreStreams())
        {
            using Vp8EncoderTests.Clip clip = Vp8EncoderTests.Clip.Moving(w, h, count);
            List<byte[]> packets = [];
            using (Vp9Encoder<byte> encoder = new(w, h, 8, 1, 1, 1, 0, EgamiContext.Default.Memory))
            {
                for (int i = 0; i < count; i++)
                {
                    Vp9EncoderCoreTests.Load(encoder, clip.Frames[i]);
                    packets.Add(encoder.Encode(plan(i)).ToArray());
                }
            }

            Write(directory, name, w, h, packets);
        }

        foreach ((string name, int depth, int subX, int subY, int w, int h, int count, int effort) in FormatStreams())
        {
            List<byte[]> packets = [];
            if (depth == 8)
            {
                using Vp9Encoder<byte> encoder = new(w, h, 8, subX, subY, 2, 0, EgamiContext.Default.Memory);
                for (int i = 0; i < count; i++)
                {
                    LoadSynthetic(encoder, i);
                    packets.Add(encoder.Encode(FormatPlan(i, effort)).ToArray());
                }
            }
            else
            {
                using Vp9Encoder<ushort> encoder = new(w, h, depth, subX, subY, 5, 1, EgamiContext.Default.Memory);
                for (int i = 0; i < count; i++)
                {
                    LoadSynthetic(encoder, i);
                    packets.Add(encoder.Encode(FormatPlan(i, effort)).ToArray());
                }
            }

            Write(directory, name, w, h, packets);
        }

        // An alpha stream from the public encoder, written on its own.
        using (Vp8EncoderTests.Clip clip = Vp8EncoderTests.Clip.Moving(64, 48, 12, alpha: true))
        {
            List<byte[]> alphaPackets = [];
            using IVideoEncoder encoder = VideoCodecs.Vp9.CreateEncoder(new VideoEncoderOptions { Width = 64, Height = 48 }, MediaContext.Default);
            for (int i = 0; i <= clip.Frames.Count; i++)
            {
                if (i < clip.Frames.Count)
                {
                    using YuvVideoFrame frame = clip.Frame(i);
                    encoder.Send(frame);
                }
                else
                {
                    encoder.Flush();
                }

                while (encoder.TryReceive(out EncodedPacket? packet))
                {
                    using (packet)
                    {
                        if (packet.Additions is { Count: > 0 } additions)
                        {
                            alphaPackets.Add(additions[0].Data.ToArray());
                        }
                    }
                }
            }

            Write(directory, "encoder.alpha-stream", 64, 48, alphaPackets);
        }

        foreach ((string name, int w, int h, int count, VideoEncoderOptions options) in EncoderStreams())
        {
            using Vp8EncoderTests.Clip clip = Vp8EncoderTests.Clip.Moving(w, h, count);
            List<byte[]> packets = [];
            using (IVideoEncoder encoder = VideoCodecs.Vp9.CreateEncoder(options, MediaContext.Default))
            {
                for (int i = 0; i < count; i++)
                {
                    using YuvVideoFrame frame = clip.Frame(i);
                    encoder.Send(frame);
                    Drain(encoder, packets);
                }

                encoder.Flush();
                Drain(encoder, packets);
            }

            Write(directory, name, w, h, packets);
        }
    }

    private static Vp9FramePlan FormatPlan(int i, int effort) => i == 0
        ? new Vp9FramePlan { Key = true, Effort = effort, Quantizer = 80 }
        : i % 3 == 2
            ? new Vp9FramePlan { Show = false, RefreshFlags = 4, AltRefAhead = true, Effort = effort, Quantizer = 60 }
            : new Vp9FramePlan { RefreshFlags = 1, AltRefAhead = true, Effort = effort, Quantizer = 100 };

    /// <summary>A moving synthetic picture at the encoder's depth and subsampling.</summary>
    internal static void LoadSynthetic<T>(Vp9Encoder<T> encoder, int frame)
        where T : unmanaged, System.Numerics.IBinaryInteger<T>
    {
        int max = (1 << encoder.BitDepth) - 1;
        uint seed = (uint)(frame * 7919) + 17;
        for (int p = 0; p < 3; p++)
        {
            int w = p == 0 ? encoder.Width : (encoder.Width + encoder.SubsamplingX) >> encoder.SubsamplingX;
            int h = p == 0 ? encoder.Height : (encoder.Height + encoder.SubsamplingY) >> encoder.SubsamplingY;
            T[] row = new T[w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    seed = (seed * 1664525) + 1013904223;
                    double v = p == 0
                        ? 0.5 + (0.35 * Math.Sin((x + (2 * frame)) * 0.21) * Math.Cos((y + frame) * 0.13))
                        : 0.5 + (0.2 * Math.Sin(((x * (p + 1)) + frame) * 0.17));
                    int value = (int)(v * max) + (int)((seed >> 28) & 7) - 3;
                    row[x] = T.CreateTruncating(Math.Clamp(value, 0, max));
                }

                encoder.SetSourceRow(p, y, row);
            }
        }

        encoder.FinishSource();
    }

    private static void Drain(IVideoEncoder encoder, List<byte[]> packets)
    {
        while (encoder.TryReceive(out EncodedPacket? packet))
        {
            using (packet)
            {
                packets.Add(packet.Data.ToArray());
            }
        }
    }

    /// <summary>Writes the packets as IVF and Egami's decode of them as packed planes.</summary>
    internal static void Write(string directory, string name, int w, int h, List<byte[]> packets)
    {
        using (FileStream ivf = File.Create(Path.Combine(directory, name + ".ivf")))
        {
            Span<byte> header = stackalloc byte[32];
            header.Clear();
            "DKIF"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 32);
            "VP90"u8.CopyTo(header[8..]);
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)w);
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)h);
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 30);
            BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)packets.Count);
            ivf.Write(header);
            Span<byte> frameHeader = stackalloc byte[12];
            for (int i = 0; i < packets.Count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(frameHeader, (uint)packets[i].Length);
                BinaryPrimitives.WriteUInt64LittleEndian(frameHeader[4..], (ulong)i);
                ivf.Write(frameHeader);
                ivf.Write(packets[i]);
            }
        }

        using FileStream yuv = File.Create(Path.Combine(directory, name + ".yuv"));
        using Vp9StreamDecoder decoder = new(EgamiContext.Default.Memory);
        foreach (byte[] packet in packets)
        {
            decoder.Decode(packet);
            if (decoder.Shown8 is { } f8)
            {
                for (int p = 0; p < 3; p++)
                {
                    for (int y = 0; y < f8.CropHeight(p); y++)
                    {
                        yuv.Write(f8.Plane(p).Slice(y * f8.Strides[p], f8.CropWidth(p)));
                    }
                }
            }
            else if (decoder.Shown16 is { } f16)
            {
                for (int p = 0; p < 3; p++)
                {
                    for (int y = 0; y < f16.CropHeight(p); y++)
                    {
                        yuv.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(f16.Plane(p).Slice(y * f16.Strides[p], f16.CropWidth(p))));
                    }
                }
            }
        }
    }
}
