using System.Globalization;
using System.Text.Json;

using Egami.Heic.Hevc;
using Egami.Nal;

namespace Egami.Heic.Tests;

/// <summary>
/// Phase 2.9.10 task 19: with EGAMI_CORPUS=heic, every conformance stream's pictures get HM's picture order counts in decoding
/// order, every reference picture set finds its pictures, and the pictures come out in order count order within each coded
/// video sequence.
/// </summary>
public class HevcSequenceTests
{
    public static TheoryData<string> Suites() => ["HEVC_v1", "RExt"];

    /// <summary>The suites, on one thread and on five (task 25); five threads decode only the streams of tiles, wavefronts and slices.</summary>
    public static TheoryData<string, int> SuitesAndThreads() => new() { { "HEVC_v1", 1 }, { "RExt", 1 }, { "HEVC_v1", 5 }, { "RExt", 5 } };

    [Theory]
    [MemberData(nameof(Suites))]
    public void ConformanceOrderCountsAndOutputOrderMatchHm(string suite)
    {
        if (HevcTestFiles.Cache() is not string cache)
        {
            return;
        }

        using JsonDocument summary = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(cache, "reference", suite, "summary.json")));
        using JsonDocument frames = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(cache, "reference", suite, "frames.json")));
        JsonElement hmFrames = frames.RootElement.GetProperty("streams");
        string[] files = [.. Directory.GetFiles(Path.Combine(cache, "hevc", suite), "*", SearchOption.AllDirectories)];
        List<string> failures = [];
        int checkedStreams = 0, unsupported = 0;
        foreach (JsonProperty stream in summary.RootElement.GetProperty("streams").EnumerateObject())
        {
            if (stream.Value.GetProperty("hm_exit").GetInt32() != 0 || !stream.Value.TryGetProperty("slices", out JsonElement slices))
            {
                continue;
            }

            // HM's slices in decoding order: one entry per slice, so the pictures are the runs of equal order counts.
            List<int> expected = [];
            foreach (JsonElement s in slices.EnumerateArray())
            {
                int poc = int.Parse(s.GetString()!.Split(' ')[0], CultureInfo.InvariantCulture);
                if (expected.Count == 0 || expected[^1] != poc)
                {
                    expected.Add(poc);
                }
            }

            string path = files.First(f => Path.GetFileName(f) == stream.Name);
            try
            {
                HevcSequenceDecoder decoder = new(SafetyLimits.Default) { DecodeSamples = false };
                List<HevcDpbPicture> decoded = [];
                List<(HevcDpbPicture Picture, int Poc)> output = [];
                decoder.PictureStarted = decoded.Add;
                foreach (ReadOnlyMemory<byte> unit in NalFraming.SplitAnnexB(File.ReadAllBytes(path)))
                {
                    decoder.Send(HevcNal.Parse(unit.Span), output, TestContext.Current.CancellationToken);
                }

                decoder.Flush(output, TestContext.Current.CancellationToken);
                // Pictures in a row with one order count (all-IDR streams) are one entry on both sides, as HM's are.
                List<int> pocs = [];
                foreach (HevcDpbPicture p in decoded)
                {
                    if (pocs.Count == 0 || pocs[^1] != p.Poc)
                    {
                        pocs.Add(p.Poc);
                    }
                }

                int hmCount = hmFrames.TryGetProperty(stream.Name, out JsonElement f) && f.TryGetProperty("frames", out JsonElement list) ? list.GetArrayLength() : -1;
                bool ordered = output.Zip(output.Skip(1)).All(o => o.First.Picture.Sequence < o.Second.Picture.Sequence
                    || (o.First.Picture.Sequence == o.Second.Picture.Sequence && o.First.Poc < o.Second.Poc));
                if (!pocs.SequenceEqual(expected))
                {
                    failures.Add($"{stream.Name}: order counts {string.Join(",", pocs.Take(24))}, HM {string.Join(",", expected.Take(24))}");
                }
                else if (!ordered || (hmCount >= 0 && output.Count != hmCount))
                {
                    failures.Add($"{stream.Name}: {output.Count} output (HM {hmCount}): {string.Join(",", output.Take(24).Select(o => o.Poc))}");
                }

                checkedStreams++;
            }
            catch (UnsupportedFormatException)
            {
                unsupported++;
            }
            catch (Exception e) when (e is CorruptDataException or IndexOutOfRangeException or InvalidOperationException or ArgumentException)
            {
                failures.Add($"{stream.Name}: {e.GetType().Name} {e.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{checkedStreams} checked, {unsupported} unsupported, {failures.Count} failed:\n" + string.Join("\n", failures));
    }

    /// <summary>
    /// Tasks 20 to 22: with EGAMI_CORPUS=heic, every frame of every conformance stream equals HM's, in output order. The streams
    /// that fail are listed with their first wrong frame, so the test shows progress.
    /// </summary>
    [Theory]
    [MemberData(nameof(SuitesAndThreads))]
    public void ConformanceFramesDecodeBitExact(string suite, int threads)
    {
        if (HevcTestFiles.Cache() is not string cache)
        {
            return;
        }

        using JsonDocument frames = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(cache, "reference", suite, "frames.json")));
        string[] files = [.. Directory.GetFiles(Path.Combine(cache, "hevc", suite), "*", SearchOption.AllDirectories)];
        string? only = Environment.GetEnvironmentVariable("EGAMI_HEVC_STREAM");
        List<string> failures = [];
        int passed = 0, unsupported = 0, totalHashes = 0;
        foreach (JsonProperty stream in frames.RootElement.GetProperty("streams").EnumerateObject())
        {
            if (!stream.Value.TryGetProperty("frames", out JsonElement expected) || (only is not null && !stream.Name.Contains(only, StringComparison.Ordinal))
                || (threads > 1 && !System.Text.RegularExpressions.Regex.IsMatch(stream.Name, "TILE|WPP|ENTP|DSLICE|SLIST|STRUCT|SLICES", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            {
                continue;
            }

            string path = files.First(f => Path.GetFileName(f) == stream.Name);
            try
            {
                HevcSequenceDecoder decoder = new(SafetyLimits.Default, threads);
                List<(HevcDpbPicture Picture, int Poc)> output = [];
                List<string> md5s = [];
                int hashes = 0, wrongHashes = 0;
                void Take()
                {
                    foreach ((HevcDpbPicture picture, _) in output)
                    {
                        md5s.Add(HevcDecodeTests.Md5(picture.Picture));
                        if (picture.Hash is { } hash)
                        {
                            hashes++;
                            wrongHashes += HevcDecoder.Matches(picture.Picture, hash) ? 0 : 1;
                        }
                    }
                }

                foreach (ReadOnlyMemory<byte> unit in NalFraming.SplitAnnexB(File.ReadAllBytes(path)))
                {
                    decoder.Send(HevcNal.Parse(unit.Span), output, TestContext.Current.CancellationToken);
                    Take();
                    output.Clear();
                }

                decoder.Flush(output, TestContext.Current.CancellationToken);
                Take();
                if (only is not null && Environment.GetEnvironmentVariable("EGAMI_HEVC_DUMP") is { Length: > 0 } dump)
                {
                    // For debugging one stream: its last output pictures as raw planes.
                    using FileStream file = File.Create(dump);
                    foreach ((HevcDpbPicture picture, _) in output)
                    {
                        foreach (ushort[] plane in picture.Picture.Planes)
                        {
                            file.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(plane.AsSpan()));
                        }
                    }
                }

                string[] hm = [.. expected.EnumerateArray().Select(e => e.GetString()!)];
                int first = Enumerable.Range(0, Math.Min(hm.Length, md5s.Count)).FirstOrDefault(i => hm[i] != md5s[i], -1);
                totalHashes += hashes;
                if (first >= 0 || hm.Length != md5s.Count)
                {
                    failures.Add($"{stream.Name}: {md5s.Count} frames (HM {hm.Length}), first differing {first}");
                }
                else if (wrongHashes > 0)
                {
                    failures.Add($"{stream.Name}: {wrongHashes} of {hashes} picture hashes differ");
                }
                else
                {
                    passed++;
                }
            }
            catch (UnsupportedFormatException)
            {
                unsupported++;
            }
            catch (Exception e) when (e is CorruptDataException or IndexOutOfRangeException or InvalidOperationException or ArgumentException)
            {
                failures.Add($"{stream.Name}: {e.GetType().Name} {e.Message}");
            }
        }

        Assert.True(only is not null || threads > 1 || totalHashes > 0, "No decoded picture hash SEI message was found.");
        Assert.True(failures.Count == 0, $"{passed} bit-exact ({totalHashes} picture hashes checked), {unsupported} unsupported, {failures.Count} failed:\n" + string.Join("\n", failures));
    }
}
