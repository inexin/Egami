using System.Buffers.Binary;
using System.Text.Json;

using Egami.Formats.Av1;
using Egami.Formats.Heif;
using Egami.Tests.Formats.Avif;

namespace Egami.Tests.Formats.Av1;

/// <summary>
/// Phase 2.9.2 task 4's gate: the sequence header and first frame header of every AV1 stream equal dav1d 1.5.4's, field by
/// field under the specification's names (tools/avifref/av1headers.c, which prints dav1d's Dav1dSequenceHeader and
/// Dav1dFrameHeader): every AV1 item of the committed corpus (reference/avif/av1-headers.json) and, with EGAMI_CORPUS=avif,
/// the first frame of libaom's test vectors, dav1d's test data and the Argon streams.
/// </summary>
public class Av1HeaderTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif");
    private static readonly Lazy<JsonElement> Headers = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "reference", "avif", "av1-headers.json"))).RootElement.GetProperty("files").Clone());

    public static TheoryData<string> CorpusFiles() => [.. Headers.Value.EnumerateObject().Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void ItemHeadersEqualDav1ds(string file)
    {
        HeifFile heif = HeifFile.Parse(File.ReadAllBytes(Path.Combine(Corpus, file)), "AVIF");
        foreach (JsonProperty item in Headers.Value.GetProperty(file).EnumerateObject())
        {
            if (item.Value.TryGetProperty("error", out _))
            {
                // dav1d gives no picture (a fuzzer find); Egami's reader must not do worse than fail as corrupt.
                continue;
            }

            ReadOnlyMemory<byte> data = heif.ItemData(heif.Item(uint.Parse(item.Name, System.Globalization.CultureInfo.InvariantCulture))!);
            Compare(data.Span, item.Value, $"{file} item {item.Name}");
        }
    }

    public static TheoryData<string, string> Streams()
    {
        TheoryData<string, string> data = [];
        if (AvifReferenceTests.Cache() is not string cache)
        {
            data.Add("(none)", "");
            return data;
        }

        foreach (string set in new[] { "aom", "dav1d", "argon" })
        {
            string summary = set == "argon" ? Path.Combine(cache, "argon", "reference", "summary.json") : Path.Combine(cache, "reference", set, "summary.json");
            if (File.Exists(summary))
            {
                data.Add(set, summary);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Streams))]
    public void StreamHeadersEqualDav1ds(string set, string summary)
    {
        if (AvifReferenceTests.Cache() is not string cache || summary.Length == 0)
        {
            throw LocalCorpus.Skip("avif");
        }

        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(summary));
        int compared = 0, needInter = 0;
        List<string> failures = [];
        foreach (JsonProperty stream in json.RootElement.GetProperty("streams").EnumerateObject())
        {
            if (!stream.Value.TryGetProperty("headers", out JsonElement headers) || headers.TryGetProperty("error", out _))
            {
                continue;
            }

            string relative = stream.Value.GetProperty("stream").GetString()!;
            try
            {
                // The headers are at the start; a stress stream's first frames can run past the first megabytes, so it is read whole.
                try
                {
                    Compare(Section5(Path.Combine(cache, relative), relative, 8 << 20), headers, stream.Name);
                }
                catch (NoShownFrameException)
                {
                    Compare(Section5(Path.Combine(cache, relative), relative, Array.MaxLength), headers, stream.Name);
                }

                compared++;
            }
            catch (UnsupportedFormatException)
            {
                // An inter frame comes before the first shown one; its header needs the inter decoder's state (2.9.5).
                needInter++;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failures.Add($"{stream.Name}: {e.Message.Split('\n')[0]}");
            }
        }

        Assert.True(failures.Count == 0, $"{set}: {failures.Count} of {compared + failures.Count} differ:\n" + string.Join('\n', failures.Take(40)));
        Assert.True(compared > 0, set);
        Assert.True(needInter <= compared / 20, $"{set}: {needInter} streams need inter frames before their first shown frame");
    }

    [Fact]
    public void DescribesWhatTheSequenceHeaderCodes()
    {
        // avifenc's reduced still picture header: 8-bit 4:2:0 with CICP 1/13/6, full range.
        HeifFile heif = HeifFile.Parse(File.ReadAllBytes(Path.Combine(Corpus, "duck-420-q60.avif")), "AVIF");
        Av1SequenceHeader header = Av1SequenceHeader.Find(heif.ItemData(heif.PrimaryItem!).Span, "AVIF")!;
        Assert.True(header.ReducedStillPictureHeader);
        Assert.Equal((8, Egami.Pixels.ChromaFormat.Yuv420, 1, 13, 6, true), (header.BitDepth, header.Chroma, header.ColorPrimaries, header.TransferCharacteristics, header.MatrixCoefficients, header.FullRange));
        Assert.Contains(("max_frame_width_minus_1", 319L), header.Describe());
    }

    [Theory]
    [InlineData("duck-grain-test1.avif")]
    [InlineData("duck-tiles-2x2.avif")]
    [InlineData("duck-deltaq.avif")]
    [InlineData("duck-restoration.avif")]
    public void HeadersSurviveEveryTruncationAndBitFlip(string file)
    {
        HeifFile heif = HeifFile.Parse(File.ReadAllBytes(Path.Combine(Corpus, file)), "AVIF");
        byte[] item = heif.ItemData(heif.PrimaryItem!).ToArray();
        List<Av1Obu> obus = Av1Obu.Split(item, "AV1");
        Av1Obu sequenceObu = obus.First(o => o.Type == Av1ObuType.SequenceHeader);
        Av1Obu frameObu = obus.First(o => o.Type == Av1ObuType.Frame);
        Av1SequenceHeader sequence = Av1SequenceHeader.Parse(item.AsSpan(sequenceObu.PayloadStart, sequenceObu.PayloadLength), "AV1");
        byte[] payload = item.AsSpan(frameObu.PayloadStart, Math.Min(frameObu.PayloadLength, 64)).ToArray();
        Random random = new(2904);
        for (int i = 0; i < 4000; i++)
        {
            byte[] mutated = i < payload.Length ? payload[..i] : (byte[])payload.Clone();
            if (i >= payload.Length)
            {
                mutated[random.Next(mutated.Length)] ^= (byte)(1 << random.Next(8));
            }

            try
            {
                Av1FrameHeader.Parse(mutated, sequence, 0, 0, "AV1").Describe();
            }
            catch (Exception e) when (e is CorruptDataException or UnsupportedFormatException)
            {
            }
        }
    }

    /// <summary>
    /// Reads the first sequence header of a Section 5 stream and the header of the first frame dav1d outputs with its defaults
    /// (operating point 0, all layers): the first shown frame, or the frame a show_existing_frame names, skipping OBUs outside the
    /// operating point. Compares both with dav1d's.
    /// </summary>
    private static void Compare(ReadOnlySpan<byte> stream, JsonElement expected, string name)
    {
        Av1SequenceHeader? sequence = null;
        Av1FrameHeader? shown = null;
        Av1FrameHeader?[] slots = new Av1FrameHeader?[8];
        int at = 0;
        while (at < stream.Length && shown is null)
        {
            byte header = stream[at];
            Av1ObuType type = (Av1ObuType)((header >> 3) & 15);
            bool extension = (header & 4) != 0, hasSize = (header & 2) != 0;
            int temporal = extension ? stream[at + 1] >> 5 : 0, spatial = extension ? (stream[at + 1] >> 3) & 3 : 0;
            int start = at + (extension ? 2 : 1);
            (ulong size, int length) = hasSize ? Av1BitReader.Leb128(stream[start..]) : ((ulong)(stream.Length - start), 0);
            Assert.True(length >= 0, $"{name}: broken obu_size");
            start += length;
            ReadOnlySpan<byte> payload = stream.Slice(start, (int)Math.Min(size, (ulong)(stream.Length - start)));
            at = start + (int)size;
            int idc = sequence?.OperatingPoints[0].Idc ?? 0;
            if (type is not (Av1ObuType.SequenceHeader or Av1ObuType.TemporalDelimiter) && extension && idc != 0
                && (((idc >> temporal) & 1) == 0 || ((idc >> (spatial + 8)) & 1) == 0))
            {
                continue;
            }

            if (type == Av1ObuType.SequenceHeader && sequence is null)
            {
                sequence = Av1SequenceHeader.Parse(payload, "AV1");
            }
            else if (type is Av1ObuType.FrameHeader or Av1ObuType.Frame && sequence is not null)
            {
                Av1FrameHeader frame = Av1FrameHeader.Parse(payload, sequence, temporal, spatial, "AV1");
                if (frame.ShowExistingFrame)
                {
                    shown = slots[frame.FrameToShowMapIdx] ?? throw new InvalidDataException($"{name}: shows an empty slot");
                    continue;
                }

                for (int i = 0; i < 8; i++)
                {
                    if (((frame.RefreshFrameFlags >> i) & 1) != 0)
                    {
                        slots[i] = frame;
                    }
                }

                shown = frame.ShowFrame ? frame : null;
            }
        }

        if (sequence is null || shown is null)
        {
            throw new NoShownFrameException($"{name}: no sequence header or shown frame");
        }

        Equal(expected.GetProperty("sequence"), sequence.Describe(), name + " sequence");
        Equal(expected.GetProperty("frame"), shown.Describe(), name + " frame");
    }

    private static void Equal(JsonElement expected, List<(string Name, long Value)> actual, string name)
    {
        Dictionary<string, long> theirs = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64());
        Dictionary<string, long> ours = actual.ToDictionary(p => p.Name, p => p.Value);
        List<string> differences = [];
        foreach ((string field, long value) in theirs)
        {
            if (!ours.TryGetValue(field, out long mine))
            {
                differences.Add($"{field} missing (dav1d {value})");
            }
            else if (mine != value)
            {
                differences.Add($"{field} {mine} (dav1d {value})");
            }
        }

        differences.AddRange(ours.Keys.Except(theirs.Keys).Select(k => $"{k} extra"));
        Assert.True(differences.Count == 0, $"{name}: {string.Join(", ", differences)}");
    }

    /// <summary>
    /// Gives the start of a stream as Section 5 OBUs with obu_size: IVF frames joined, Annex B (Argon's, and dav1d's annexb.obu)
    /// rewritten. Only the first <paramref name="limit"/> bytes are read.
    /// </summary>
    private static byte[] Section5(string path, string relative, int limit)
    {
        byte[] data;
        using (FileStream file = File.OpenRead(path))
        {
            data = new byte[(int)Math.Min(file.Length, limit)];
            file.ReadExactly(data);
        }

        if (data is [(byte)'D', (byte)'K', (byte)'I', (byte)'F', ..])
        {
            using MemoryStream joined = new();
            int at = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
            while (at + 12 <= data.Length)
            {
                int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
                joined.Write(data, at + 12, Math.Min(size, data.Length - at - 12));
                at += 12 + size;
            }

            return joined.ToArray();
        }

        bool annexB = (relative.Contains("argon", StringComparison.Ordinal) && !relative.Contains("not_annexb", StringComparison.Ordinal))
            || relative.EndsWith("/annexb.obu", StringComparison.Ordinal);
        if (!annexB)
        {
            return data;
        }

        using MemoryStream output = new();
        int pos = 0;
        while (pos < data.Length)
        {
            (ulong unitSize, int n) = Av1BitReader.Leb128(data.AsSpan(pos));
            if (n < 0)
            {
                break;
            }

            pos += n;
            int unitEnd = (int)Math.Min((ulong)data.Length, (ulong)pos + unitSize);
            ConvertUnit(data, ref pos, unitEnd, output);
        }

        return output.ToArray();
    }

    /// <summary>Rewrites one Annex B temporal unit's frame units as Section 5 OBUs.</summary>
    private static void ConvertUnit(byte[] data, ref int pos, int unitEnd, MemoryStream output)
    {
        while (pos < unitEnd)
        {
            (ulong frameSize, int m) = Av1BitReader.Leb128(data.AsSpan(pos));
            pos += m;
            int frameEnd = (int)Math.Min((ulong)unitEnd, (ulong)pos + frameSize);
            while (pos < frameEnd)
            {
                (ulong obuLength, int k) = Av1BitReader.Leb128(data.AsSpan(pos));
                pos += k;
                int obuEnd = (int)Math.Min((ulong)frameEnd, (ulong)pos + obuLength);
                byte header = data[pos];
                int head = (header & 4) != 0 ? 2 : 1;
                int payloadStart = pos + head;
                if ((header & 2) != 0)
                {
                    payloadStart += Av1BitReader.Leb128(data.AsSpan(payloadStart)).Length;
                }

                output.WriteByte((byte)(header | 2));
                output.Write(data, pos + 1, head - 1);
                for (ulong v = (ulong)(obuEnd - payloadStart); ; v >>= 7)
                {
                    output.WriteByte((byte)((v & 0x7F) | (v >= 0x80 ? 0x80u : 0)));
                    if (v < 0x80)
                    {
                        break;
                    }
                }

                output.Write(data, payloadStart, obuEnd - payloadStart);
                pos = obuEnd;
            }
        }
    }

    private sealed class NoShownFrameException(string message) : Exception(message);
}
