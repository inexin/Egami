using System.Security.Cryptography;
using System.Text.Json;

using Egami.Formats.Av1;
using Egami.Formats.Heif;
using Egami.Tests.Formats.Avif;

namespace Egami.Tests.Formats.Av1;

/// <summary>
/// Phase 2.9.2 task 5's gate: the entropy decoder and block syntax read every intra frame's symbols as libaom 3.15.2 does. Each
/// AV1 item's first frame is parsed and its mode info compared with libaom's inspect example's (tools/avifref, aominspect),
/// layer by layer: block and transform sizes, modes, skip, CDEF, chroma from luma, quantizer index, segment, intra block copy and
/// its vector, palette sizes, and the bits each superblock's symbols took, which only agree when every symbol in between does.
/// The committed corpus is reference/avif/av1-modeinfo.json; with EGAMI_CORPUS=avif, also the AVIF files that stay in .cache.
/// </summary>
public class Av1ModeInfoTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif");
    private static readonly Lazy<JsonElement> Reference = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "reference", "avif", "av1-modeinfo.json"))).RootElement.GetProperty("files").Clone());

    public static TheoryData<string> CorpusFiles() => [.. Reference.Value.EnumerateObject().Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void CorpusModeInfoEqualsLibaoms(string file)
    {
        List<string> failures = [];
        CompareFile(Path.Combine(Corpus, file), Reference.Value.GetProperty(file), file, failures);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>The corpus reaches the coding tools whose syntax the gate checks.</summary>
    [Fact]
    public void CorpusCoversTheCodingTools()
    {
        HashSet<string> seen = [];
        foreach (JsonProperty file in Reference.Value.EnumerateObject())
        {
            HeifFile heif = HeifFile.Parse(File.ReadAllBytes(Path.Combine(Corpus, file.Name)), "AVIF");
            foreach (JsonProperty item in file.Value.EnumerateObject())
            {
                if (item.Value.TryGetProperty("error", out _))
                {
                    continue;
                }

                Av1Frame frame;
                try
                {
                    frame = Av1Decoder.DecodeFirstFrame(heif.ItemData(heif.Item(uint.Parse(item.Name, System.Globalization.CultureInfo.InvariantCulture))!).ToArray(), "AVIF", reconstruct: false);
                }
                catch (UnsupportedFormatException)
                {
                    continue;
                }

                Av1FrameHeader h = frame.Header;
                Mark(frame.SbSize == Bs.B128x128, "128x128 superblocks");
                Mark(h.TileCols * h.TileRows > 1, "tiles");
                Mark(h.DeltaQPresent, "delta q");
                Mark(h.SegmentationEnabled, "segmentation");
                Mark(h.UsingQmatrix, "quantizer matrices");
                Mark(h.CodedLossless, "lossless");
                Mark(h.ReducedTxSet, "reduced transform set");
                Mark(frame.Sequence.Monochrome, "monochrome");
                Mark(frame.Sequence.SubsamplingX == 1 && frame.Sequence.SubsamplingY == 0, "4:2:2");
                Mark(frame.Sequence.SubsamplingX == 0, "4:4:4");
                Mark(frame.Sequence.BitDepth == 12, "12 bits");
                foreach (Av1RestorationPlane plane in frame.Restoration)
                {
                    foreach (Av1RestorationUnit unit in plane.Units)
                    {
                        Mark(unit.Type == 1, "Wiener units");
                        Mark(unit.Type == 2, "self-guided units");
                    }
                }

                foreach (Av1BlockInfo? b in frame.Grid)
                {
                    Mark(b!.UseIntrabc, "intra block copy");
                    Mark(b.PaletteSize[0] > 0, "luma palettes");
                    Mark(b.PaletteSize[1] > 0, "chroma palettes");
                    Mark(b.UseFilterIntra, "filter intra");
                    Mark(b.UvMode == Pred.UvCfl, "chroma from luma");
                    Mark(b.Partition >= Partition.HorzA && b.Partition <= Partition.VertB, "AB partitions");
                    Mark(b.Partition >= Partition.Horz4, "4-way partitions");
                }
            }
        }

        string[] tools = ["128x128 superblocks", "tiles", "delta q", "segmentation", "quantizer matrices", "lossless", "reduced transform set",
            "monochrome", "4:2:2", "4:4:4", "12 bits", "Wiener units", "self-guided units", "intra block copy", "luma palettes",
            "chroma palettes", "filter intra", "chroma from luma", "AB partitions", "4-way partitions"];
        Assert.Empty(tools.Except(seen));

        void Mark(bool condition, string tool)
        {
            if (condition)
            {
                seen.Add(tool);
            }
        }
    }

    [Fact]
    public void ExtraModeInfoEqualsLibaoms()
    {
        if (AvifReferenceTests.Cache() is not string cache || !File.Exists(Path.Combine(cache, "reference", "extra", "av1-modeinfo.json")))
        {
            throw LocalCorpus.Skip("avif");
        }

        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(cache, "reference", "extra", "av1-modeinfo.json")));
        List<string> failures = [];
        foreach (JsonProperty file in json.RootElement.GetProperty("files").EnumerateObject())
        {
            CompareFile(Path.Combine(cache, file.Name), file.Value, file.Name, failures);
        }

        Assert.True(failures.Count == 0, $"{failures.Count} items differ:\n" + string.Join("\n", failures.Take(40)));
    }

    private static void CompareFile(string path, JsonElement items, string name, List<string> failures)
    {
        HeifFile heif = HeifFile.Parse(File.ReadAllBytes(path), "AVIF");
        foreach (JsonProperty item in items.EnumerateObject())
        {
            if (item.Value.TryGetProperty("error", out _))
            {
                continue;
            }

            JsonElement expected = item.Value.GetProperty("frames")[0];
            byte[] data = heif.ItemData(heif.Item(uint.Parse(item.Name, System.Globalization.CultureInfo.InvariantCulture))!).ToArray();
            Av1Frame frame;
            try
            {
                frame = Av1Decoder.DecodeFirstFrame(data, "AVIF", reconstruct: false);
            }
            catch (UnsupportedFormatException)
            {
                // Not an intra frame (inter frames come with 2.9.5).
                continue;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failures.Add($"{name} item {item.Name}: {e.GetType().Name}: {e.Message}");
                continue;
            }

            string where = $"{name} item {item.Name}";
            if (frame.TileDataDamaged)
            {
                failures.Add($"{where}: a tile's symbols overran it or its trailing bits are wrong, which libaom rejects");
            }

            if (frame.MiRows != expected.GetProperty("miRows").GetInt32() || frame.MiCols != expected.GetProperty("miCols").GetInt32())
            {
                failures.Add($"{where}: {frame.MiRows}x{frame.MiCols} 4x4 units, libaom {expected.GetProperty("miRows")}x{expected.GetProperty("miCols")}");
                continue;
            }

            Dictionary<string, short[]> layers = Layers(frame);
            List<string> differing = [];
            foreach (JsonProperty layer in expected.GetProperty("layers").EnumerateObject())
            {
                if (Md5(layers[layer.Name]) != layer.Value.GetString())
                {
                    differing.Add(layer.Name);
                }
            }

            if (differing.Count > 0)
            {
                failures.Add($"{where}: {string.Join(", ", differing)} differ");
            }
        }
    }

    private static string Md5(short[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
#pragma warning disable CA5351 // the references' layer hashes are MD5; nothing here is security
        return Convert.ToHexStringLower(MD5.HashData(bytes));
#pragma warning restore CA5351
    }

    /// <summary>The mode info layers as libaom's inspection.c reports them, one value per 4x4 unit row by row ("dv" two).</summary>
    internal static Dictionary<string, short[]> Layers(Av1Frame frame)
    {
        int count = frame.MiRows * frame.MiCols;
        string[] names = ["blockSize", "transformSize", "mode", "uv_mode", "skip", "cdef_level", "cdef_strength", "cfl_alpha_idx",
            "cfl_alpha_sign", "delta_q", "seg_id", "intrabc", "palette", "uv_palette", "sb_bits"];
        Dictionary<string, short[]> layers = names.ToDictionary(n => n, _ => new short[count]);
        short[] dv = new short[count * 2];
        layers["dv"] = dv;
        Av1FrameHeader header = frame.Header;
        for (int row = 0; row < frame.MiRows; row++)
        {
            for (int col = 0; col < frame.MiCols; col++)
            {
                int i = (row * frame.MiCols) + col;
                Av1BlockInfo b = frame.Grid[i]!;
                layers["blockSize"][i] = (short)b.Size;
                layers["transformSize"][i] = frame.TxSizeMap[i];
                layers["mode"][i] = (short)b.YMode;
                layers["uv_mode"][i] = (short)b.UvMode;
                layers["skip"][i] = (short)(b.Skip ? 1 : 0);

                // libaom keeps a 64x64 unit's CDEF index in the block at the unit's corner; other blocks keep 0.
                int cdef = (b.MiRow & 15) == 0 && (b.MiCol & 15) == 0 ? Math.Max(0, (int)frame.CdefIndex[((b.MiRow >> 4) * frame.CdefStride) + (b.MiCol >> 4)]) : 0;
                layers["cdef_level"][i] = (short)header.CdefYPriStrength[cdef];
                layers["cdef_strength"][i] = (short)header.CdefYSecStrength[cdef];
                bool cfl = b.UvMode == Pred.UvCfl;
                layers["cfl_alpha_idx"][i] = (short)(cfl ? b.CflAlphaIdx : 0);
                layers["cfl_alpha_sign"][i] = (short)(cfl ? b.CflAlphaSigns : 0);
                layers["delta_q"][i] = (short)b.QIndex;
                layers["seg_id"][i] = (short)b.SegmentId;
                layers["intrabc"][i] = (short)(b.UseIntrabc ? 1 : 0);
                layers["palette"][i] = (short)b.PaletteSize[0];
                layers["uv_palette"][i] = (short)b.PaletteSize[1];
                layers["sb_bits"][i] = (short)frame.SuperblockBits[((row >> frame.SbMiLog2) * frame.SbCols) + (col >> frame.SbMiLog2)];
                if (b.UseIntrabc)
                {
                    dv[2 * i] = (short)b.MvRow;
                    dv[(2 * i) + 1] = (short)b.MvCol;
                }
            }
        }

        return layers;
    }
}
