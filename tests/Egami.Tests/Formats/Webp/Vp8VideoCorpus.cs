using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Egami.Formats.Webp;

namespace Egami.Tests.Formats.Webp;

/// <summary>
/// The VP8 video references made by tools/make-vp8-references.py: the committed clips in images/vp8 with libvpx's frame hashes
/// and dumps in reference/vp8, and the libvpx test vectors with theirs under .cache/vp8 when that folder exists.
/// </summary>
internal static class Vp8VideoCorpus
{
    internal static readonly string Clips = Path.Combine(AppContext.BaseDirectory, "images", "vp8");
    internal static readonly string ClipReferences = Path.Combine(AppContext.BaseDirectory, "reference", "vp8");

    /// <summary>Gets the folder of the fetched test vectors and their references, or null.</summary>
    internal static string? Cache()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".cache", "vp8");
            if (Directory.Exists(Path.Combine(candidate, "vectors")))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Gets every clip and vector, as "clip/name" or "vector/name".</summary>
    internal static TheoryData<string> All()
    {
        TheoryData<string> data = [];
        foreach (string file in Directory.GetFiles(Clips, "*.ivf").Order(StringComparer.Ordinal))
        {
            data.Add("clip/" + Path.GetFileNameWithoutExtension(file));
        }

        if (Cache() is string cache)
        {
            foreach (string file in Directory.GetFiles(Path.Combine(cache, "vectors"), "*.ivf").Order(StringComparer.Ordinal))
            {
                data.Add("vector/" + Path.GetFileNameWithoutExtension(file));
            }
        }

        return data;
    }

    internal static string IvfPath(string id) => id.StartsWith("clip/", StringComparison.Ordinal)
        ? Path.Combine(Clips, id[5..] + ".ivf")
        : Path.Combine(Cache()!, "vectors", id[7..] + ".ivf");

    private static string ReferencePath(string id, string extension) => id.StartsWith("clip/", StringComparison.Ordinal)
        ? Path.Combine(ClipReferences, id[5..] + extension)
        : Path.Combine(Cache()!, "reference", id[7..] + extension);

    internal static JsonElement Expected(string id)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(ReferencePath(id, ".json")));
        return json.RootElement.Clone();
    }

    /// <summary>Returns libvpx's dump, one list of lines per packet (the "frame" line first).</summary>
    internal static List<List<string>> Dump(string id)
    {
        using GZipStream gz = new(File.OpenRead(ReferencePath(id, ".dump.gz")), CompressionMode.Decompress);
        using StreamReader reader = new(gz);
        List<List<string>> packets = [];
        while (reader.ReadLine() is string line)
        {
            if (line.StartsWith("frame ", StringComparison.Ordinal))
            {
                packets.Add([line]);
            }
            else if (!line.StartsWith("shown ", StringComparison.Ordinal))
            {
                packets[^1].Add(line);
            }
        }

        return packets;
    }

    /// <summary>Reads the packets of an IVF file.</summary>
    internal static List<byte[]> ReadIvf(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        int at = BitConverter.ToUInt16(file, 6);
        List<byte[]> packets = [];
        while (at + 12 <= file.Length)
        {
            int size = BitConverter.ToInt32(file, at);
            packets.Add(file.AsSpan(at + 12, size).ToArray());
            at += 12 + size;
        }

        return packets;
    }

    /// <summary>Returns the MD5 of a frame as vpxdec writes it: packed I420 at the display size.</summary>
    internal static string Md5(Vp8FrameBuffer frame)
    {
        int w = frame.Width, h = frame.Height, cw = (w + 1) / 2, ch = (h + 1) / 2;
        byte[] packed = new byte[(w * h) + (2 * cw * ch)];
        for (int y = 0; y < h; y++)
        {
            frame.Y.Slice(frame.OriginY + (y * frame.StrideY), w).CopyTo(packed.AsSpan(y * w));
        }

        for (int y = 0; y < ch; y++)
        {
            frame.U.Slice(frame.OriginC + (y * frame.StrideC), cw).CopyTo(packed.AsSpan((w * h) + (y * cw)));
            frame.V.Slice(frame.OriginC + (y * frame.StrideC), cw).CopyTo(packed.AsSpan((w * h) + (cw * ch) + (y * cw)));
        }

#pragma warning disable CA5351 // vpxdec's frame hashes are MD5; nothing here is security
        return Convert.ToHexStringLower(MD5.HashData(packed));
#pragma warning restore CA5351
    }

    /// <summary>Writes Egami's reading of the frame just decoded in vp8dump's format, line for line.</summary>
    internal static List<string> Describe(Vp8VideoDecoder video, int index, int size)
    {
        Vp8Decoder f = video.LastFrame!;
        Vp8StreamState s = video.State;
        Vp8FrameHeader h = f.Header;
        bool key = h.KeyFrame;
        List<string> lines =
        [
            $"frame {index} size {size}",
            $"key_frame = {B(key)}",
            $"version = {h.Version}",
            $"show_frame = {B(h.Show)}",
            $"width = {h.Width}",
            $"height = {h.Height}",
            $"horizontal_scale = {h.HorizontalScale}",
            $"vertical_scale = {h.VerticalScale}",
            $"clamping_type = {s.ClampingType}",
            $"segmentation_enabled = {B(f.SegmentationEnabled)}",
            $"update_mb_segmentation_map = {B(f.UpdateSegmentMap)}",
            $"update_segment_feature_data = {B(f.UpdateSegmentData)}",
            $"segment_feature_mode = {B(s.AbsoluteSegmentValues)}",
            $"quantizer_update_value = {string.Join(' ', s.SegmentQuantizer)}",
            $"loop_filter_update_value = {string.Join(' ', s.SegmentFilterLevel)}",
            $"segment_prob = {string.Join(' ', s.SegmentProbabilities)}",
            $"filter_type = {B(f.FilterSimple)}",
            $"loop_filter_level = {f.FilterLevel}",
            $"sharpness_level = {f.Sharpness}",
            $"loop_filter_adj_enable = {B(f.FilterDeltas)}",
            $"mode_ref_lf_delta_update = {B(f.UpdateFilterDeltas)}",
            $"ref_frame_delta_magnitude = {string.Join(' ', s.ReferenceFilterDeltas)}",
            $"mb_mode_delta_magnitude = {string.Join(' ', s.ModeFilterDeltas)}",
            $"log2_nbr_of_dct_partitions = {int.Log2(f.Partitions)}",
            $"y_ac_qi = {f.Quantizers[0]}",
            $"y_dc_delta = {f.Quantizers[1]}",
            $"y2_dc_delta = {f.Quantizers[2]}",
            $"y2_ac_delta = {f.Quantizers[3]}",
            $"uv_dc_delta = {f.Quantizers[4]}",
            $"uv_ac_delta = {f.Quantizers[5]}",
            $"refresh_golden_frame = {B(key || f.RefreshGolden)}",
            $"refresh_alternate_frame = {B(key || f.RefreshAlternate)}",
            $"copy_buffer_to_golden = {f.CopyToGolden}",
            $"copy_buffer_to_alternate = {f.CopyToAlternate}",
            $"sign_bias_golden = {B(s.SignBias[2])}",
            $"sign_bias_alternate = {B(s.SignBias[3])}",
            $"refresh_entropy_probs = {B(f.RefreshEntropy)}",
            $"refresh_last = {B(f.RefreshLast)}",
            $"mb_no_coeff_skip = {B(f.UseSkip)}",
            $"prob_skip_false = {f.SkipProbability}",
        ];
        if (!key)
        {
            lines.Add($"prob_intra = {f.ProbIntra}");
            lines.Add($"prob_last = {f.ProbLast}");
            lines.Add($"prob_gf = {f.ProbGolden}");
        }

        lines.Add($"next_intra_16x16_prob = {string.Join(' ', s.YModeProbabilities)}");
        lines.Add($"next_intra_chroma_prob = {string.Join(' ', s.UvModeProbabilities)}");
        lines.Add($"next_mv_prob_row = {string.Join(' ', s.MvProbabilities.Take(19))}");
        lines.Add($"next_mv_prob_column = {string.Join(' ', s.MvProbabilities.Skip(19))}");
        uint hash = 2166136261;
        foreach (byte b in s.CoefficientProbabilities)
        {
            hash = (hash ^ b) * 16777619;
        }

        lines.Add($"next_coeff_prob_hash = {hash}");
        Vp8ModeInfo m = video.Modes;
        for (int r = 0; r < m.MbHeight; r++)
        {
            for (int c = 0; c < m.MbWidth; c++)
            {
                int i = m.Index(c, r);
                bool intra = m.Reference[i] == 0;
                StringBuilder line = new($"mb {r} {c} {m.Reference[i]} {m.Mode[i]} {(intra ? m.UvMode[i] : 0)} {s.SegmentMap![(r * m.MbWidth) + c]} {m.Skip[i]} {(intra ? 0 : m.NeedsClamp[i])} {Vp8ModeInfo.Row(m.Mv[i])} {Vp8ModeInfo.Col(m.Mv[i])}");
                if (m.Mode[i] == Vp8ModeInfo.BPred)
                {
                    line.Append(" b");
                    for (int k = 0; k < 16; k++)
                    {
                        line.Append(' ').Append(m.SubModes[(i * 16) + k]);
                    }
                }
                else if (m.Mode[i] == Vp8ModeInfo.Split)
                {
                    line.Append(" s ").Append(m.Partitioning[i]);
                    for (int k = 0; k < 16; k++)
                    {
                        int mv = m.SubMv[(i * 16) + k];
                        line.Append(' ').Append(Vp8ModeInfo.Row(mv)).Append(',').Append(Vp8ModeInfo.Col(mv));
                    }
                }

                lines.Add(line.ToString());
            }
        }

        return lines;
    }

    private static int B(bool b) => b ? 1 : 0;
}
