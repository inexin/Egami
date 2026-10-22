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
        byte[] data = File.ReadAllBytes(Path.Combine(cache, "aom-test-data", vector));
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
        string path = Path.Combine(cache, "aom-test-data", vector);
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
        byte[] data = File.ReadAllBytes(Path.Combine(cache, "aom-test-data", vector + ".ivf"));
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
