using Egami.Formats.Av1;
using Egami.Pixels;

namespace Egami.Tests.Formats.Av1;

/// <summary>
/// Writes the AV1 stream encoder's test streams for tools/check-av1-encoder.py, which checks that dav1d and libaom decode each
/// exactly as Egami does: into EGAMI_INTEROP_DIR/av1, each stream as &lt;name&gt;.ivf, the frames Egami's decoder shows from it as
/// &lt;name&gt;.yuv (planar, 16-bit little-endian above 8 bits) and its shape as &lt;name&gt;.txt. Runs only when EGAMI_INTEROP_DIR is
/// set. Each stream is also written at one thread and checked to be the same bytes.
/// </summary>
public class Av1EncoderInteropFiles
{
    public static TheoryData<string> Streams() => [.. Cases.Keys];

    private static readonly Dictionary<string, (int Width, int Height, int BitDepth, ChromaFormat Chroma, int Effort, string Pattern)> Cases = new()
    {
        ["e0-420"] = (160, 96, 8, ChromaFormat.Yuv420, 0, "ipp"),
        ["e2-420"] = (160, 96, 8, ChromaFormat.Yuv420, 2, "ipp"),
        ["e4-420"] = (176, 112, 8, ChromaFormat.Yuv420, 4, "ipp"),
        ["e6-420"] = (150, 70, 8, ChromaFormat.Yuv420, 6, "ipp"),
        ["e8-420"] = (130, 72, 8, ChromaFormat.Yuv420, 8, "ipp"),
        ["e4-arf"] = (160, 96, 8, ChromaFormat.Yuv420, 4, "arf"),
        ["e7-arf"] = (144, 80, 8, ChromaFormat.Yuv420, 7, "arf"),
        ["e5-10bit-444"] = (96, 64, 10, ChromaFormat.Yuv444, 5, "ipp"),
        ["e6-12bit-422"] = (70, 50, 12, ChromaFormat.Yuv422, 6, "arf"),
        ["e3-400"] = (64, 48, 8, ChromaFormat.Yuv400, 3, "ipp"),
        ["e4-resilient"] = (96, 64, 8, ChromaFormat.Yuv420, 4, "resilient"),
        ["e4-intraonly"] = (96, 64, 8, ChromaFormat.Yuv420, 4, "intraonly"),
    };

    [Theory]
    [MemberData(nameof(Streams))]
    public void WriteStream(string name)
    {
        string? directory = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        Assert.SkipUnless(directory is not null, "EGAMI_INTEROP_DIR is not set.");
        (int width, int height, int bitDepth, ChromaFormat chroma, int effort, string pattern) = Cases[name];
        List<byte[]> units = Encode(width, height, bitDepth, chroma, effort, pattern, EgamiContext.Default);
        List<byte[]> single = Encode(width, height, bitDepth, chroma, effort, pattern, new EgamiContext { MaxParallelism = 1 });
        Assert.Equal(units.Count, single.Count);
        for (int i = 0; i < units.Count; i++)
        {
            Assert.True(units[i].AsSpan().SequenceEqual(single[i]), $"Temporal unit {i} differs at one thread.");
        }

        string folder = Path.Combine(directory, "av1");
        Directory.CreateDirectory(folder);
        string basePath = Path.Combine(folder, name);
        File.WriteAllBytes(basePath + ".ivf", Ivf(units, width, height));
        using FileStream yuv = File.Create(basePath + ".yuv");
        using Av1StreamDecoder decoder = new();
        foreach (byte[] unit in units)
        {
            decoder.Send(unit);
            using YuvRaster frame = bitDepth == 8 ? YuvRaster.Create(width, height, chroma, YuvColor.Bt601Limited)
                : YuvRaster.Create(width, height, chroma, bitDepth, YuvColor.Bt601Limited);
            Assert.True(decoder.TryReceiveFrame(frame, out _));
            WritePlanes(yuv, frame);
        }

        File.WriteAllText(basePath + ".txt", $"{width} {height} {bitDepth} {chroma}");
    }

    /// <summary>Encodes a stream in one of the test patterns and returns its temporal units.</summary>
    internal static List<byte[]> Encode(int width, int height, int bitDepth, ChromaFormat chroma, int effort, string pattern, EgamiContext context)
    {
        List<byte[]> units = [];
        using Av1StreamEncoder encoder = new(new Av1SequenceOptions { Width = width, Height = height, BitDepth = bitDepth, Chroma = chroma }, context);
        void Add(Av1EncodedFrame encoded)
        {
            if (!encoded.Data.IsEmpty)
            {
                units.Add(encoded.Data.ToArray());
            }
        }

        YuvRaster Frame(int i) => Av1StreamEncoderTests.Moving(width, height, bitDepth, chroma, i);
        Av1FramePlan plan = new() { Effort = effort, Quality = 55 };
        switch (pattern)
        {
            case "arf":
                // A key frame, a hidden alternate reference four frames ahead, three frames between that read both, then the
                // alternate reference shown again.
                for (int group = 0; group < 2; group++)
                {
                    int first = group * 4;
                    if (group == 0)
                    {
                        using YuvRaster key = Frame(0);
                        Add(encoder.Encode(key, plan with { Kind = Av1FrameKind.Key }));
                    }

                    using (YuvRaster arf = Frame(first + 4))
                    {
                        Add(encoder.Encode(arf, plan with { Show = false, OrderHint = first + 4, RefreshSlots = 2 }));
                    }

                    for (int i = first + 1; i < first + 4; i++)
                    {
                        using YuvRaster frame = Frame(i);
                        Add(encoder.Encode(frame, plan with { ReferenceSlots = [0, 2, 0, 3, 1, 1, 1], RefreshSlots = i == first + 2 ? 1 | 4 : 1 }));
                    }

                    Add(encoder.ShowExisting(1));
                }

                break;
            case "resilient":
                for (int i = 0; i < 5; i++)
                {
                    using YuvRaster frame = Frame(i);
                    Add(encoder.Encode(frame, plan with { ErrorResilient = i % 2 == 1 }));
                }

                break;
            case "intraonly":
                for (int i = 0; i < 5; i++)
                {
                    using YuvRaster frame = Frame(i);
                    Add(encoder.Encode(frame, plan with { Kind = i == 2 ? Av1FrameKind.IntraOnly : Av1FrameKind.Inter, RefreshSlots = i == 2 ? 2 : 1, ReferenceSlots = i == 3 ? [1, 0, 0, 0, 0, 0, 0] : [0, 1, 2, 3, 4, 5, 6] }));
                }

                break;
            default:
                for (int i = 0; i < 6; i++)
                {
                    using YuvRaster frame = Frame(i);
                    Add(encoder.Encode(frame, plan with { RefreshSlots = i % 3 == 0 ? 1 | 8 : 1, ReferenceSlots = [0, 1, 2, 3, 4, 5, 6] }));
                }

                break;
        }

        return units;
    }

    /// <summary>Wraps temporal units in an IVF file, each after a temporal delimiter.</summary>
    internal static byte[] Ivf(List<byte[]> units, int width, int height)
    {
        using MemoryStream ivf = new();
        using BinaryWriter w = new(ivf);
        w.Write("DKIF"u8);
        w.Write((ushort)0);
        w.Write((ushort)32);
        w.Write("AV01"u8);
        w.Write((ushort)width);
        w.Write((ushort)height);
        w.Write(30);
        w.Write(1);
        w.Write(units.Count);
        w.Write(0);
        for (int i = 0; i < units.Count; i++)
        {
            w.Write(units[i].Length + 2);
            w.Write((long)i);
            w.Write((byte)0x12);
            w.Write((byte)0);
            w.Write(units[i]);
        }

        w.Flush();
        return ivf.ToArray();
    }

    private static void WritePlanes(Stream output, YuvRaster frame)
    {
        if (frame is YuvRaster<byte> bytes)
        {
            foreach (YuvPlane<byte>? plane in new[] { bytes.Y, bytes.U, bytes.V })
            {
                for (int y = 0; plane is not null && y < plane.Height; y++)
                {
                    output.Write(plane.Row(y)[..plane.Width]);
                }
            }

            return;
        }

        YuvRaster<ushort> words = (YuvRaster<ushort>)frame;
        foreach (YuvPlane<ushort>? plane in new[] { words.Y, words.U, words.V })
        {
            for (int y = 0; plane is not null && y < plane.Height; y++)
            {
                foreach (ushort v in plane.Row(y)[..plane.Width])
                {
                    output.WriteByte((byte)v);
                    output.WriteByte((byte)(v >> 8));
                }
            }
        }
    }
}
