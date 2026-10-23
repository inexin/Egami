using Egami.Video.Audio.Codecs.Mp3;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// The ISO/IEC 11172-4 compliance streams and minimp3's damaged-stream vectors (fetched by make-audio-corpus.py --mp3 into
/// .cache/audio/mp3-vectors) against their 16-bit references. Frames are decoded and joined as the references were made: a frame
/// that is damaged or whose bit reservoir is missing is left out, and each frame has its own channel count. V5.8 Design Q24's
/// full accuracy: the maximum error is at most 2^-14 and the PSNR at least 96 dB over the 16-bit references.
/// </summary>
public class Mp3VectorTests(ITestOutputHelper log)
{
    public static TheoryData<string> Vectors()
    {
        TheoryData<string> data = [];
        foreach (string name in new[]
        {
            "l1-fl1", "l1-fl2", "l1-fl3", "l1-fl4", "l1-fl5", "l1-fl6", "l1-fl7", "l1-fl8", "l2-fl10", "l2-fl11", "l2-fl12", "l2-fl13",
            "l2-fl14", "l2-fl15", "l2-fl16", "l3-compl", "l3-he_32khz", "l3-he_44khz", "l3-he_48khz", "l3-he_free", "l3-he_mode",
            "l3-hecommon", "l3-si", "l3-si_huff", "l3-sin1k0db", "l3-test45", "l3-test46", "M2L3_bitrate_16_all", "M2L3_bitrate_22_all",
            "M2L3_bitrate_24_all", "M2L3_compl24", "M2L3_noise", "l3-nonstandard-compl-sideinfo-bigvalues",
            "l3-nonstandard-compl-sideinfo-blocktype", "l3-nonstandard-compl-sideinfo-size", "l3-nonstandard-id3v1",
            "l3-nonstandard-sideinfo-size",
        })
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>The fetched vectors, or null when make-audio-corpus.py --mp3 has not fetched them.</summary>
    public static string? Directory()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".cache", "audio", "mp3-vectors");
            if (File.Exists(Path.Combine(candidate, "l3-compl.bit")))
            {
                return candidate;
            }
        }

        return null;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void DecodesAtFullAccuracy(string name)
    {
        if (Directory() is not { } directory)
        {
            Assert.Skip("Run python tools/video/make-audio-corpus.py --mp3 to fetch the MPEG audio compliance streams.");
            return;
        }

        string pcmPath = Path.Combine(directory, name + ".pcm");
        if (!File.Exists(pcmPath))
        {
            Assert.Skip($"{name} has no reference.");
            return;
        }

        List<float> ours = DecodeLikeReference(File.ReadAllBytes(Path.Combine(directory, name + ".bit")));
        byte[] reference = File.ReadAllBytes(pcmPath);
        int count = reference.Length / 2;

        // The ISO references of Layers I and II run on past the streams' last frame; every decoded sample must have its reference.
        Assert.True(ours.Count <= count || name.StartsWith("l3", StringComparison.Ordinal) || name.StartsWith("M2", StringComparison.Ordinal),
            $"{ours.Count} samples decoded, the reference has {count}");
        int n = Math.Min(count, ours.Count);
        if (!name.StartsWith("l1", StringComparison.Ordinal) && !name.StartsWith("l2", StringComparison.Ordinal))
        {
            // Some Layer III references stop one frame early; FFmpeg decodes that last frame too, as this decoder does.
            Assert.True(ours.Count == count || ours.Count == count + 1152 || ours.Count == count + 2304, $"{ours.Count} samples decoded, the reference has {count}");
        }

        double squares = 0;
        double max = 0;
        for (int i = 0; i < n; i++)
        {
            // The references are 16-bit, so they clip at full scale.
            double e = Math.Clamp(ours[i], -1.0, 32767 / 32768.0) - (BitConverter.ToInt16(reference, 2 * i) / 32768.0);
            squares += e * e;
            max = Math.Max(max, Math.Abs(e));
        }

        double rms = n == 0 ? 0 : Math.Sqrt(squares / n);
        double psnr = rms == 0 ? 200 : 20 * Math.Log10(1 / rms);
        log.WriteLine($"{name}: {n} samples, RMS error {rms:E3}, max {max:E3}, PSNR {psnr:F1} dB");
        Assert.True(max <= 1.0 / 16384, $"max error {max}");
        Assert.True(psnr >= 96, $"PSNR {psnr:F1} dB");
    }

    /// <summary>Finds and decodes every frame, dropping damaged frames and frames without their bit reservoir, interleaving each frame's own channels.</summary>
    internal static List<float> DecodeLikeReference(byte[] data)
    {
        Mp3FrameDecoder decoder = new();
        float[] left = new float[Mp3FrameDecoder.MaxSamples];
        float[] right = new float[Mp3FrameDecoder.MaxSamples];
        List<float> output = [];
        int pos = 0;
        int free = 0;
        while (pos < data.Length)
        {
            int found = MpegAudioSync.Find(data.AsSpan(pos), ref free, out int total, atEnd: true);
            if (found < 0)
            {
                break;
            }

            pos += found;
            ReadOnlySpan<byte> frame = data.AsSpan(pos, total);
            MpegAudioHeader header = new(frame);
            int channels = header.Channels;
            Mp3FrameResult result = decoder.Decode(frame, left, channels == 2 ? right : left);
            if (result == Mp3FrameResult.Decoded)
            {
                for (int i = 0; i < header.SamplesPerFrame; i++)
                {
                    output.Add(left[i]);
                    if (channels == 2)
                    {
                        output.Add(right[i]);
                    }
                }
            }

            // Keep going frame by frame while the headers chain, as a decoder fed the whole stream does.
            pos += total;
            while (pos + MpegAudioHeader.Size <= data.Length && MpegAudioHeader.Matches(frame, data.AsSpan(pos)))
            {
                MpegAudioHeader next = new(data.AsSpan(pos));
                int length = next.TotalBytes(free);
                if (length == 0 || pos + length > data.Length)
                {
                    break;
                }

                frame = data.AsSpan(pos, length);
                channels = next.Channels;
                result = decoder.Decode(frame, left, channels == 2 ? right : left);
                if (result == Mp3FrameResult.Decoded)
                {
                    for (int i = 0; i < next.SamplesPerFrame; i++)
                    {
                        output.Add(left[i]);
                        if (channels == 2)
                        {
                            output.Add(right[i]);
                        }
                    }
                }

                pos += length;
            }
        }

        return output;
    }
}
