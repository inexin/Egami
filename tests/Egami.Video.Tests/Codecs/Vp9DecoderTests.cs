using System.Globalization;

using Egami.Video.Codecs;
using Egami.Video.Codecs.Vp9;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Codecs;

/// <summary>The VP9 decoder core against libvpx: every decoded frame, its probabilities and its blocks.</summary>
public class Vp9DecoderTests
{
    public static TheoryData<string> ClipNames => [.. Vp9Corpus.Clips().Where(c => c != "p1-440")];

    [Theory]
    [MemberData(nameof(ClipNames))]
    public void ClipFramesEqualLibvpx(string clip)
    {
        string path = Vp9Corpus.PathOf(clip + ".ivf");
        Vp9Reference reference = Vp9Corpus.ReadReference(path);
        AssertDecodedFramesMatchDump(path);
        List<string> ours = Vp9Corpus.ShownMd5s(path);
        Assert.True(reference.Md5.SequenceEqual(ours), $"{clip}: {ours.Count} frames, {reference.Md5.Count} expected, first difference at {Vp9Corpus.FirstDifference(reference.Md5, ours)}");
    }

    public static TheoryData<string, int> ClipsAndThreads
    {
        get
        {
            TheoryData<string, int> data = [];
            foreach (string clip in Vp9Corpus.Clips().Where(c => c != "p1-440"))
            {
                data.Add(clip, 2);
                data.Add(clip, 5);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ClipsAndThreads))]
    public void ClipFramesAreTheSameOnSeveralThreads(string clip, int threads)
    {
        string path = Vp9Corpus.PathOf(clip + ".ivf");
        List<string> ours = Vp9Corpus.ShownMd5s(path, threads: threads);
        Assert.Equal(Vp9Corpus.ReadReference(path).Md5, ours);
    }

    [Theory]
    [InlineData("720p.ivf", 4)]
    [InlineData("1080p-10bit.ivf", 24)]
    public void LargeClipsAreTheSameOnSeveralThreads(string file, int threads)
    {
        string? folder = Corpus.FindCache(Path.Combine("vp9", "large"));
        string path = Path.Combine(folder ?? "", file);
        Assert.SkipWhen(folder is null || !File.Exists(path), "The large VP9 clips are not made (python tools/video/make-vp9-corpus.py --large).");
        Assert.Equal(Vp9Corpus.ReadReference(path).Md5, Vp9Corpus.ShownMd5s(path, threads: threads));
    }

    [Fact]
    public void TiledTestVectorsAreTheSameOnSeveralThreads()
    {
        string[] vectors = [.. Vp9Corpus.Vectors.Where(v => Path.GetFileName(v).Contains("tile", StringComparison.Ordinal)
            || Path.GetFileName(v).Contains("resize", StringComparison.Ordinal))];
        Assert.SkipWhen(vectors.Length == 0, "The VP9 test vectors are not fetched (python tools/video/make-vp9-corpus.py --vectors).");
        foreach (string vector in vectors)
        {
            Vp9Reference reference = Vp9Corpus.ReadReference(vector);
            if (reference.Errors.Count > 0)
            {
                continue;
            }

            List<string> ours = Vp9Corpus.ShownMd5s(vector, threads: 6);
            Assert.True(reference.Md5.SequenceEqual(ours), $"{Path.GetFileName(vector)}: first difference at {Vp9Corpus.FirstDifference(reference.Md5, ours)}");
        }
    }

    [Fact]
    public void TestVectorsEqualLibvpx()
    {
        IReadOnlyList<string> vectors = Vp9Corpus.Vectors;
        Assert.SkipWhen(vectors.Count == 0, "The VP9 test vectors are not fetched (python tools/video/make-vp9-corpus.py --vectors).");
        string? only = Environment.GetEnvironmentVariable("VP9_VECTOR");
        List<string> failures = [];
        foreach (string vector in vectors)
        {
            string name = Path.GetFileName(vector);
            if ((only is not null && !name.Contains(only, StringComparison.Ordinal)) || name.Contains("yuv440", StringComparison.Ordinal))
            {
                continue;
            }

            Vp9Reference reference = Vp9Corpus.ReadReference(vector);
            List<string> ours;
            try
            {
                ours = Vp9Corpus.ShownMd5s(vector);
            }
            catch (Exception e) when (e is not CorruptDataException)
            {
                failures.Add($"{name}: {e.GetType().Name}: {e.Message} {e.StackTrace?.Split(Environment.NewLine).FirstOrDefault()}");
                continue;
            }

            if (!reference.Md5.SequenceEqual(ours))
            {
                failures.Add($"{name}: {ours.Count} frames, {reference.Md5.Count} expected, first difference at {Vp9Corpus.FirstDifference(reference.Md5, ours)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Theory]
    [MemberData(nameof(ClipNames))]
    public void ClipHeadersEqualLibvpx(string clip) => AssertHeadersMatchDump(Vp9Corpus.PathOf(clip + ".ivf"));

    [Fact]
    public void TestVectorHeadersEqualLibvpx()
    {
        IReadOnlyList<string> vectors = Vp9Corpus.Vectors;
        Assert.SkipWhen(vectors.Count == 0, "The VP9 test vectors are not fetched (python tools/video/make-vp9-corpus.py --vectors).");
        foreach (string vector in vectors)
        {
            if (!vector.Contains("yuv440", StringComparison.Ordinal))
            {
                AssertHeadersMatchDump(vector);
            }
        }
    }

    [Theory]
    [MemberData(nameof(ClipNames))]
    public void DescribeListsTheHeaderFields(string clip)
    {
        string path = Vp9Corpus.PathOf(clip + ".ivf");
        List<Dictionary<string, string>> dump = Vp9Corpus.ReadDump(path);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        TrackInfo track = demuxer.Info.VideoTrack!;
        Assert.True(CodecHeaders.CanDescribe(track));
        int frame = 0;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                IReadOnlyList<CodecHeaderField> fields = CodecHeaders.Describe(track, packet);
                foreach (List<CodecHeaderField> one in SplitFrames(fields))
                {
                    Dictionary<string, string> expected = dump[frame++];
                    Assert.Equal(long.Parse(expected["show_existing_frame"], CultureInfo.InvariantCulture), Field(one, "show_existing_frame"));
                    if (expected["show_existing_frame"] == "1")
                    {
                        continue;
                    }

                    foreach (string name in (string[])["frame_type", "show_frame", "error_resilient_mode", "base_q_idx", "loop_filter_level",
                        "loop_filter_sharpness", "segmentation_enabled", "tile_cols_log2", "tx_mode"])
                    {
                        Assert.True(long.Parse(expected[name], CultureInfo.InvariantCulture) == Field(one, name), $"{clip} frame {frame - 1}: {name}");
                    }
                }
            }
        }

        Assert.Equal(dump.Count, frame);
    }

    private static IEnumerable<List<CodecHeaderField>> SplitFrames(IReadOnlyList<CodecHeaderField> fields)
    {
        List<CodecHeaderField> current = [];
        foreach (CodecHeaderField field in fields)
        {
            if (field.Name == "frame" && current.Any(f => f.Name == "frame_marker"))
            {
                yield return current;
                current = [];
            }

            current.Add(field);
        }

        yield return current;
    }

    private static long Field(List<CodecHeaderField> fields, string name) => fields.First(f => f.Name == name).Value;

    /// <summary>Compares the header fields libvpx read from every decoded frame with ours.</summary>
    internal static void AssertHeadersMatchDump(string path)
    {
        List<Dictionary<string, string>> dump = Vp9Corpus.ReadDump(path);
        List<Vp9FrameHeader> headers = [];
        using (Vp9StreamDecoder decoder = new(EgamiContext.Default.Memory))
        {
            foreach (byte[] packet in Vp9Corpus.Packets(path))
            {
                try
                {
                    decoder.Decode(packet);
                }
                catch (CorruptDataException)
                {
                }

                headers.AddRange(decoder.Headers);
            }
        }

        string name = Path.GetFileName(path);
        Assert.True(dump.Count == headers.Count, $"{name}: {headers.Count} frames decoded, libvpx {dump.Count}");
        for (int i = 0; i < dump.Count; i++)
        {
            Dictionary<string, string> e = dump[i];
            Vp9FrameHeader h = headers[i];
            void Check(string field, long value) =>
                Assert.True(long.Parse(e[field], CultureInfo.InvariantCulture) == value, $"{name} frame {i}: {field} is {value}, libvpx {e[field]}");

            Check("show_existing_frame", h.ShowExistingFrame ? 1 : 0);
            if (h.ShowExistingFrame)
            {
                continue;
            }

            Check("profile", h.Profile);
            Check("frame_type", h.KeyFrame ? 0 : 1);
            Check("show_frame", h.ShowFrame ? 1 : 0);
            Check("error_resilient_mode", h.ErrorResilientMode ? 1 : 0);
            Check("intra_only", h.IntraOnly ? 1 : 0);
            Check("refresh_frame_flags", h.RefreshFrameFlags);
            Check("frame_width", h.Width);
            Check("frame_height", h.Height);
            Check("render_width", h.RenderWidth);
            Check("render_height", h.RenderHeight);
            Check("refresh_frame_context", h.RefreshFrameContext ? 1 : 0);
            Check("frame_parallel_decoding_mode", h.FrameParallelDecodingMode ? 1 : 0);
            Check("frame_context_idx", h.FrameIsIntraOnly || h.ErrorResilientMode ? 0 : h.FrameContextIdx);
            Check("loop_filter_level", h.LoopFilterLevel);
            Check("loop_filter_sharpness", h.LoopFilterSharpness);
            Check("loop_filter_delta_enabled", h.LoopFilterDeltaEnabled ? 1 : 0);
            Check("base_q_idx", h.BaseQIdx);
            Check("delta_q_y_dc", h.DeltaQYDc);
            Check("delta_q_uv_dc", h.DeltaQUVDc);
            Check("delta_q_uv_ac", h.DeltaQUVAc);
            Check("segmentation_enabled", h.SegmentationEnabled ? 1 : 0);
            Check("tile_cols_log2", h.TileColsLog2);
            Check("tile_rows_log2", h.TileRowsLog2);
            Check("tx_mode", h.TxMode);
            if (h.HasColorConfig)
            {
                Check("bit_depth", h.BitDepth);
                Check("color_space", h.ColorSpace);
                Check("color_range", h.ColorRange);
                Check("subsampling_x", h.SubsamplingX);
                Check("subsampling_y", h.SubsamplingY);
            }

            if (!h.FrameIsIntraOnly)
            {
                Check("allow_high_precision_mv", h.AllowHighPrecisionMv ? 1 : 0);
                Check("interp_filter", h.InterpFilter);
                Check("reference_select", h.ReferenceMode);
            }
        }
    }

    /// <summary>Compares every decoded frame (hidden ones too) and its adapted probabilities with vp9dump's, for a clear first failure.</summary>
    internal static void AssertDecodedFramesMatchDump(string path)
    {
        List<Dictionary<string, string>> dump = [.. Vp9Corpus.ReadDump(path).Where(f => f["show_existing_frame"] == "0")];
        int index = 0;
        string? failure = null;
        Vp9Corpus.ShownMd5s(path, (frame, probs) =>
        {
            if (failure is not null || index >= dump.Count)
            {
                index++;
                return;
            }

            Dictionary<string, string> expected = dump[index];
            string probsHex = Convert.ToHexStringLower(probs);
            if (probsHex != expected["probs"][..probsHex.Length])
            {
                int at = 0;
                while (at < probsHex.Length && probsHex[at] == expected["probs"][at])
                {
                    at++;
                }

                failure = $"decoded frame {index}: probabilities differ first at byte {at / 2}";
            }
            else if (Vp9Corpus.Md5(frame) != expected["md5"])
            {
                failure = $"decoded frame {index} (frame_type {expected["frame_type"]}, show_frame {expected["show_frame"]}): pixels differ";
            }

            index++;
        });
        Assert.True(failure is null, $"{Path.GetFileName(path)}: {failure}");
    }
}
