using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

using Egami.Formats.Av1;

namespace Egami.Tests.Formats.Av1;

/// <summary>
/// The AV1 stream decoder against libaom 3.15.2, frame by frame, on libaom's test vectors (av1-1-b8-*, av1-1-b10-*) with
/// EGAMI_CORPUS=av1: every output frame's planes, with and without film grain, must hash as libaom's do
/// (.cache/avif/reference/av1, from tools/make-av1-references.py). Phase 2.9.5's exact decode gate.
/// </summary>
public class Av1StreamTests
{
    public static TheoryData<string> Vectors()
    {
        string? cache = Cache();
        TheoryData<string> data = [];
        if (cache is null)
        {
            data.Add("(none)");
            return data;
        }

        foreach (string path in Directory.EnumerateFiles(Path.Combine(cache, "reference", "av1"), "*.json").Order())
        {
            data.Add(Path.GetFileNameWithoutExtension(path));
        }

        return data;
    }

    /// <summary>The mode info and coefficients parse to each tile's end (a parse error would leave the symbol decoder elsewhere).</summary>
    [Theory]
    [MemberData(nameof(Vectors))]
    public void TilesParseToTheirEnd(string vector)
    {
        string cache = Cache() ?? throw LocalCorpus.Skip("av1");
        Av1Stream stream = new("AV1") { Reconstruct = false };
        byte[] data = File.ReadAllBytes(VectorPath(cache, vector));
        int index = 0;
        foreach ((int offset, int length) in IvfFrames(data))
        {
            foreach (Av1Frame frame in stream.Decode(data, offset, length, TestContext.Current.CancellationToken))
            {
                Assert.False(frame.TileDataDamaged, $"frame {index}: tile data did not end where it should");
                index++;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void FramesEqualLibaoms(string vector)
    {
        string cache = Cache() ?? throw LocalCorpus.Skip("av1");
        string path = VectorPath(cache, vector);
        JsonElement[] expected = [.. JsonDocument.Parse(File.ReadAllBytes(Path.Combine(cache, "reference", "av1", vector + ".json")))
            .RootElement.GetProperty("frames").EnumerateArray()];
        List<string> got = [];
        List<string> gotNoGrain = [];
        Av1Stream stream = new("AV1");
        byte[] data = File.ReadAllBytes(path);
        foreach ((int offset, int length) in IvfFrames(data))
        {
            foreach (Av1Frame frame in stream.Decode(data, offset, length, TestContext.Current.CancellationToken))
            {
                Assert.False(frame.TileDataDamaged, $"frame {got.Count}: tile data did not end where it should");
                gotNoGrain.Add(Md5(frame, frame.Planes!));
                got.Add(Md5(frame, Av1FilmGrainSynthesis.Apply(frame)));
            }
        }

        Assert.Equal(expected.Length, got.Count);
        for (int i = 0; i < got.Count; i++)
        {
            Assert.True(expected[i][5].GetString() == gotNoGrain[i], $"frame {i} before film grain differs from libaom's");
            Assert.True(expected[i][4].GetString() == got[i], $"frame {i} with film grain differs from libaom's");
        }
    }

    /// <summary>
    /// Every inter tool appears in at least one stream the exact gate checks. Translation-only global motion is left out: libaom's
    /// encoder never chooses it (the Argon streams cover it, locally).
    /// </summary>
    [Fact]
    public void StreamsCoverEveryInterTool()
    {
        string cache = Cache() ?? throw LocalCorpus.Skip("av1");
        Dictionary<string, int> uses = [];
        foreach (string vector in Vectors().Select(row => (string)row.Data))
        {
            Av1Stream stream = new("AV1") { Reconstruct = false };
            byte[] data = File.ReadAllBytes(VectorPath(cache, vector));
            foreach ((int offset, int length) in IvfFrames(data))
            {
                foreach (Av1Frame frame in stream.Decode(data, offset, length, TestContext.Current.CancellationToken))
                {
                    Count(frame);
                }
            }
        }

        string[] tools =
        [
            "obmc", "local warp", "global warp", "average compound", "distance compound", "wedge compound",
            "difference compound", "inter-intra", "wedge inter-intra", "skip mode", "scaled reference", "switch frame", "intra in inter frame",
            "dual filter", "near mode", "new mode", "compound new", "temporal vectors", "segmentation map from reference",
        ];
        Assert.True(tools.All(uses.ContainsKey), "unused: " + string.Join(", ", tools.Where(t => !uses.ContainsKey(t))));

        void Add(string tool) => uses[tool] = uses.GetValueOrDefault(tool) + 1;

        void Count(Av1Frame frame)
        {
            Av1FrameHeader h = frame.Header;
            if (h.FrameType == Av1FrameType.Switch)
            {
                Add("switch frame");
            }

            if (h.UseRefFrameMvs)
            {
                Add("temporal vectors");
            }

            if (h.SegmentationEnabled && !h.SegmentationUpdateMap)
            {
                Add("segmentation map from reference");
            }

            for (int rf = Ref.Last; rf <= Ref.Alt && !h.FrameIsIntra; rf++)
            {
                if (h.RefFrames[rf]!.Header.UpscaledWidth != h.FrameWidth || h.RefFrames[rf]!.Header.FrameHeight != h.FrameHeight)
                {
                    Add("scaled reference");
                }

                if (h.GmType[rf] == GlobalMotion.Translation)
                {
                    Add("global translation");
                }
            }

            foreach (Av1BlockInfo? b in frame.Grid.Distinct())
            {
                if (b is null || h.FrameIsIntra)
                {
                    continue;
                }

                if (!b.IsInter)
                {
                    Add("intra in inter frame");
                    continue;
                }

                Add(b.MotionMode == 1 ? "obmc" : b.MotionMode == 2 ? "local warp" : "translation");
                if (b.YMode is InterMode.Global or InterMode.GlobalGlobal && h.GmType[b.RefFrame0] > GlobalMotion.Translation)
                {
                    Add("global warp");
                }

                if (b.HasSecondRef)
                {
                    Add(b.CompoundType switch
                    {
                        Compound.Average => "average compound",
                        Compound.Distance => "distance compound",
                        Compound.Wedge => "wedge compound",
                        _ => "difference compound",
                    });
                }

                if (b.RefFrame1 == Ref.Intra)
                {
                    Add(b.UseWedgeInterintra ? "wedge inter-intra" : "inter-intra");
                }

                Add(b.SkipMode ? "skip mode" : b.FilterX != b.FilterY ? "dual filter" : "single filter");
                Add(b.YMode is InterMode.Near ? "near mode" : b.YMode is InterMode.New ? "new mode" : InterMode.HasNew(b.YMode) ? "compound new" : "other mode");
            }
        }
    }

    /// <summary>
    /// For debugging: with AV1_DEBUG naming a vector whose raw libaom frames (av1frames.c --no-grain) are in
    /// .cache/avif/reference/av1raw, reports each frame's first differing sample and the block there.
    /// </summary>
    [Fact]
    public void FirstDifference()
    {
        string? vector = Environment.GetEnvironmentVariable("AV1_DEBUG");
        string? cache = Cache();
        if (vector is null || cache is null)
        {
            return;
        }

        byte[] raw = File.ReadAllBytes(Path.Combine(cache, "reference", "av1raw", vector + ".bin"));
        byte[] data = File.ReadAllBytes(VectorPath(cache, vector + ".ivf"));
        Av1Stream stream = new("AV1");
        List<string> report = [];
        int at = 0, index = 0;
        foreach ((int offset, int length) in IvfFrames(data))
        {
            foreach (Av1Frame frame in stream.Decode(data, offset, length, TestContext.Current.CancellationToken))
            {
                int w = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at)), h = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at + 4));
                int sx = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at + 16)), sy = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at + 20));
                at += 24;
                Av1FrameHeader fh = frame.Header;
                report.Add($"frame {index}: type {fh.FrameType} show {fh.ShowFrame} {w}x{h} filter {fh.InterpolationFilter} ref_mvs {fh.UseRefFrameMvs} skipmode {fh.SkipModePresent} refsel {fh.ReferenceSelect} warp {fh.AllowWarpedMotion}");
                for (int plane = 0; plane < frame.Planes!.Length; plane++)
                {
                    int pw = plane == 0 ? w : (w + sx) >> sx, ph = plane == 0 ? h : (h + sy) >> sy;
                    bool found = false;
                    for (int y = 0; y < ph && !found; y++)
                    {
                        for (int x = 0; x < pw && !found; x++)
                        {
                            int want = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at + (((y * pw) + x) * 2)));
                            int got = frame.Planes[plane][(y * frame.Strides[plane]) + x];
                            if (want != got)
                            {
                                found = true;
                                int miRow = (y << (plane > 0 ? sy : 0)) >> 2, miCol = (x << (plane > 0 ? sx : 0)) >> 2;
                                Av1BlockInfo b = frame.Grid[(miRow * frame.MiCols) + miCol]!;
                                report.Add($"  plane {plane} ({x},{y}) want {want} got {got}: block {b.MiCol},{b.MiRow} size {b.Size} mode {b.YMode} uv {b.UvMode} refs {b.RefFrame0},{b.RefFrame1} "
                                    + $"mv {b.MvRow},{b.MvCol} / {b.Mv1Row},{b.Mv1Col} motion {b.MotionMode} comp {b.CompoundType} filt {b.FilterY},{b.FilterX} skip {b.Skip} ii {b.InterintraMode} wii {b.UseWedgeInterintra} tx {b.TxSize}");
                            }
                        }
                    }

                    at += pw * ph * 2;
                }

                index++;
            }
        }

        Assert.Fail(string.Join(Environment.NewLine, report));
    }

    /// <summary>Gets a stream's path: libaom's test vectors in .cache, our own clips in tests/video/corpus/av1.</summary>
    private static string VectorPath(string cache, string vector)
    {
        string aom = Path.Combine(cache, "aom-test-data", vector);
        return File.Exists(aom) ? aom : Path.Combine(cache, "..", "..", "tests", "video", "corpus", "av1", vector);
    }

    /// <summary>Gets the fetched .cache/avif folder with the AV1 references, or null.</summary>
    internal static string? Cache()
    {
        if (!LocalCorpus.Enabled("av1"))
        {
            return null;
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".cache", "avif");
            if (Directory.Exists(Path.Combine(candidate, "reference", "av1")))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The frames of an IVF file: offset and length of each.</summary>
    internal static IEnumerable<(int Offset, int Length)> IvfFrames(byte[] data)
    {
        int at = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        while (at + 12 <= data.Length)
        {
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
            at += 12;
            yield return (at, Math.Min(size, data.Length - at));
            at += size;
        }
    }

    /// <summary>The MD5 of a frame's planes as av1frames.c writes them: cropped, 16-bit little-endian samples.</summary>
    internal static string Md5(Av1Frame frame, ushort[][] planes)
    {
        int width = frame.Header.UpscaledWidth, height = frame.Header.FrameHeight;
        int ssx = frame.Sequence.SubsamplingX, ssy = frame.Sequence.SubsamplingY;
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        byte[] row = new byte[width * 2];
        for (int plane = 0; plane < planes.Length; plane++)
        {
            int w = plane == 0 ? width : (width + ssx) >> ssx, h = plane == 0 ? height : (height + ssy) >> ssy;
            int stride = frame.Strides[plane];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(x * 2), planes[plane][(y * stride) + x]);
                }

                md5.AppendData(row, 0, w * 2);
            }
        }

        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }
}
