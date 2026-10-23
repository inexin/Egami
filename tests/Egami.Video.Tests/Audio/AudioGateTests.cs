using System.Diagnostics;

using Egami.Video.Audio;
using Egami.Video.Audio.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// The V5.0 gate runs that need the IETF FLAC test files and take minutes: set EGAMI_AUDIO_GATES=1. They write Egami.Video's FLAC
/// encodings into .cache/audio/gates for make-audio-corpus.py --gates to compare with libFLAC (flac -t, sizes) and print
/// decode speed.
/// </summary>
public class AudioGateTests(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("EGAMI_AUDIO_GATES") == "1";

    [Fact]
    public void EncodeIetfFilesAtEveryLevel()
    {
        if (!Enabled || AudioCorpus.IetfDirectory is not { } directory)
        {
            Assert.Skip("Set EGAMI_AUDIO_GATES=1 and fetch the IETF FLAC test files (make-audio-corpus.py --ietf).");
            return;
        }

        string root = Path.Combine(directory, "..", "gates");
        foreach ((string name, AudioCorpus.Reference reference) in Sources(directory))
        {
            for (int level = 0; level <= 8; level++)
            {
                string target = Path.Combine(root, $"L{level}", Path.GetFileName(name));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using AudioReader reader = AudioReader.Open(Path.Combine(directory, name), new AudioReaderOptions { SampleFormat = null });
                using AudioWriter writer = AudioWriter.Create(target, new AudioWriterOptions { Codec = FlacCodec.Instance, Level = level, BitsPerSample = reference.Bits });
                foreach (AudioFrame frame in reader.ReadFrames())
                {
                    using (frame)
                    {
                        writer.Write(frame);
                    }
                }
            }

            output.WriteLine($"encoded {name}");
        }
    }

    [Fact]
    public void DecodeSpeed()
    {
        if (!Enabled || AudioCorpus.IetfDirectory is not { } directory)
        {
            Assert.Skip("Set EGAMI_AUDIO_GATES=1 and fetch the IETF FLAC test files (make-audio-corpus.py --ietf).");
            return;
        }

        long samples = 0;
        Stopwatch watch = new();
        foreach ((string name, _) in Sources(directory))
        {
            byte[] data = File.ReadAllBytes(Path.Combine(directory, name));
            for (int run = 0; run < 3; run++)
            {
                watch.Start();
                using AudioReader reader = AudioReader.Open(new MemoryStream(data), new AudioReaderOptions { SampleFormat = null, Decoder = new AudioDecoderOptions { VerifyChecksums = true } });
                foreach (AudioFrame frame in reader.ReadFrames())
                {
                    samples += frame.SampleCount * frame.ChannelCount;
                    frame.Dispose();
                }

                watch.Stop();
            }
        }

        output.WriteLine($"Decoded {samples / 3} samples in {watch.Elapsed.TotalSeconds / 3:F3} s a pass: {samples / watch.Elapsed.TotalSeconds / 1e6:F1} M samples/s");
    }

    [Fact]
    public void ResamplerSpeed()
    {
        if (!Enabled)
        {
            Assert.Skip("Set EGAMI_AUDIO_GATES=1.");
            return;
        }

        // 60 s of stereo float at 44.1 kHz to 48 kHz at each quality, as make-audio-corpus.py --gates times SoX and FFmpeg.
        using AudioFrame input = new(SampleFormat.F32Planar, ChannelLayout.Stereo, 44100, 44100 * 60, MediaTime.Zero);
        for (int c = 0; c < 2; c++)
        {
            Span<float> s = input.GetSpan<float>(c);
            for (int i = 0; i < s.Length; i++)
            {
                s[i] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / 44100);
            }
        }

        foreach (ResamplerQuality quality in Enum.GetValues<ResamplerQuality>())
        {
            Stopwatch watch = Stopwatch.StartNew();
            using Resampler resampler = new(44100, 48000, ChannelLayout.Stereo, quality);
            for (int start = 0; start < input.SampleCount; start += 4096)
            {
                using AudioFrame part = input.Slice(start, Math.Min(4096, input.SampleCount - start));
                resampler.Send(part);
                while (resampler.TryReceive(out AudioFrame? frame))
                {
                    frame.Dispose();
                }
            }

            resampler.Flush();
            output.WriteLine($"Resampler {quality}: 60 s in {watch.Elapsed.TotalSeconds:F3} s ({resampler.FilterLength} taps)");
        }
    }

    [Fact]
    public void OpusDecodeSpeed()
    {
        if (!Enabled || OpusVectorTests.Directory() is not { } directory)
        {
            Assert.Skip("Set EGAMI_AUDIO_GATES=1 and fetch the Opus test vectors (make-audio-corpus.py --opus).");
            return;
        }

        // The twelve RFC 8251 vectors decoded to stereo, best of three, as make-audio-corpus.py --gates times opus_demo -d.
        List<(byte[] Packet, int Length)> packets = [];
        for (int v = 1; v <= 12; v++)
        {
            byte[] file = File.ReadAllBytes(Path.Combine(directory, $"testvector{v:00}.bit"));
            for (int p = 0; p + 8 <= file.Length;)
            {
                int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(p));
                packets.Add((file.AsSpan(p + 8, length).ToArray(), v));
                p += 8 + length;
            }
        }

        double best = double.MaxValue;
        long samples = 0;
        float[] pcm = new float[5760 * 2 * 2];
        for (int run = 0; run < 3; run++)
        {
            Egami.Video.Audio.Codecs.Opus.OpusStreamDecoder? decoder = null;
            int vector = 0;
            samples = 0;
            Stopwatch watch = Stopwatch.StartNew();
            foreach ((byte[] packet, int v) in packets)
            {
                if (v != vector)
                {
                    decoder = new Egami.Video.Audio.Codecs.Opus.OpusStreamDecoder(2);
                    vector = v;
                }

                samples += Math.Max(0, decoder!.Decode(packet, pcm, 5760 * 2, false, false, out _));
            }

            best = Math.Min(best, watch.Elapsed.TotalSeconds);
        }

        output.WriteLine($"Opus: {samples} samples ({samples / 48000.0:F0} s of audio) in {best:F3} s, {samples / 48000.0 / best:F0}x real time");
    }

    [Fact]
    public void Mp3DecodeSpeed()
    {
        if (!Enabled || Mp3VectorTests.Directory() is not { } directory)
        {
            Assert.Skip("Set EGAMI_AUDIO_GATES=1 and fetch the MPEG audio compliance streams (make-audio-corpus.py --mp3).");
            return;
        }

        // The Layer III compliance streams' frames through the frame decoder, best of three (egami-compare times minimp3 on the same).
        List<(byte[][] Frames, int Rate)> streams = [];
        foreach (string file in Directory.GetFiles(directory, "*.bit").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            // l3-nonstandard-sideinfo-size is one damaged 100-byte frame that no reader locks onto.
            if ((!name.StartsWith("l3-", StringComparison.Ordinal) && !name.StartsWith("M2L3", StringComparison.Ordinal)) || name == "l3-nonstandard-sideinfo-size.bit")
            {
                continue;
            }

            using IDemuxer demuxer = Mp3Format.Instance.OpenDemuxer(File.OpenRead(file), MediaContext.Default, leaveOpen: false);
            List<byte[]> frames = [];
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    frames.Add(packet.Data.ToArray());
                }
            }

            streams.Add(([.. frames], ((AudioTrackInfo)demuxer.Tracks[0]).SampleRate));
        }

        float[] left = new float[Egami.Video.Audio.Codecs.Mp3.Mp3FrameDecoder.MaxSamples];
        float[] right = new float[Egami.Video.Audio.Codecs.Mp3.Mp3FrameDecoder.MaxSamples];
        double best = double.MaxValue;
        double seconds = 0;
        for (int run = 0; run < 3; run++)
        {
            seconds = 0;
            Stopwatch watch = Stopwatch.StartNew();
            foreach ((byte[][] frames, int rate) in streams)
            {
                Egami.Video.Audio.Codecs.Mp3.Mp3FrameDecoder decoder = new();
                foreach (byte[] frame in frames)
                {
                    decoder.Decode(frame, left, right);
                    seconds += (double)new Egami.Video.Audio.Codecs.Mp3.MpegAudioHeader(frame).SamplesPerFrame / rate;
                }
            }

            best = Math.Min(best, watch.Elapsed.TotalSeconds);
        }

        output.WriteLine($"MP3: about {seconds:F0} s of audio in {best:F3} s, {seconds / best:F0}x real time");
    }

    /// <summary>The IETF subset files libFLAC decodes, without the ones made to stress metadata.</summary>
    private static IEnumerable<(string Name, AudioCorpus.Reference Reference)> Sources(string directory) =>
        AudioCorpus.Load(Path.Combine(directory, "reference.json"))
            .Where(p => p.Key.StartsWith("subset/", StringComparison.Ordinal) && p.Value.Md5.Length > 0 && p.Value.Bits >= 4
                && !p.Key.Contains("Extremely large", StringComparison.Ordinal) && !p.Key.Contains("combined", StringComparison.Ordinal))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => (p.Key, p.Value));
}
