using Egami.Video.Audio;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Audio;

/// <summary>Opus files from opusenc decode like opusdec, from Ogg, WebM and MP4 alike, with exact trimming, seeking and loss handling.</summary>
public class OpusFileTests
{
    [Theory]
    [InlineData("opus-stereo.opus")]
    [InlineData("opus-mono-speech.opus")]
    [InlineData("opus-hybrid.opus")]
    [InlineData("opus-51.opus")]
    [InlineData("opus-fec.opus")]
    [InlineData("opus-chained.opus")]
    public void DecodesLikeOpusdec(string name)
    {
        // opusdec's 16-bit output, pre-skip and end trimmed and with no dither, is the reference; ours is converted the same way.
        using AudioReader expected = AudioReader.Open(OggTests.PathOf(name + ".wav"), new AudioReaderOptions { SampleFormat = SampleFormat.S16Planar });
        using AudioReader actual = AudioReader.Open(OggTests.PathOf(name), new AudioReaderOptions { SampleFormat = SampleFormat.S16Planar, Errors = DecodeErrorMode.Throw });
        Assert.Equal(expected.Track.Channels, actual.Layout.ChannelCount);
        short[][] a = Collect(expected), b = Collect(actual);
        Assert.Equal(a[0].Length, b[0].Length);

        // The channels come in canonical order on both sides (opusdec writes WAV order with a channel mask).
        for (int c = 0; c < a.Length; c++)
        {
            int worst = 0;
            for (int i = 0; i < a[c].Length; i++)
            {
                worst = Math.Max(worst, Math.Abs(a[c][i] - b[c][i]));
            }

            Assert.True(worst <= 1, $"Channel {c} differs by up to {worst}.");
        }
    }

    [Theory]
    [InlineData("webm-opus.webm")]
    [InlineData("mp4-opus.mp4")]
    public void DecodesFromOtherContainers(string name)
    {
        // The same 64 kb/s stream as opus-stereo's sibling: half a second, pre-skip and end padding trimmed.
        using AudioReader reader = AudioReader.Open(OggTests.PathOf(name), new AudioReaderOptions { Errors = DecodeErrorMode.Throw });
        Assert.Equal(48000, reader.SampleRate);
        Assert.Equal(80, reader.Track.SeekPreRoll!.Value.Rescale(new Rational(1, 1000)).Ticks);
        long samples = Collect(reader)[0].Length;
        Assert.InRange(samples, 24000, name.EndsWith(".mp4", StringComparison.Ordinal) ? 24960 : 24000);
    }

    [Theory]
    [InlineData("opus-stereo.opus")]
    [InlineData("opus-hybrid.opus")]
    [InlineData("webm-opus.webm")]
    public void SeeksToTheSample(string name)
    {
        AudioReaderOptions options = new() { SampleFormat = SampleFormat.F32Planar, Errors = DecodeErrorMode.Throw };
        using AudioReader whole = AudioReader.Open(OggTests.PathOf(name), options);
        float[] all = Collect32(whole)[0];
        using AudioReader reader = AudioReader.Open(OggTests.PathOf(name), options);
        foreach (long target in new long[] { 12000, 4800, 9600 })
        {
            reader.Seek(new MediaTime(target, new Rational(1, 48000)));
            List<float> after = [];
            bool first = true;
            while (after.Count < 5760 && reader.TryRead(out AudioFrame? frame))
            {
                using (frame)
                {
                    if (first)
                    {
                        Assert.Equal(new MediaTime(target, new Rational(1, 48000)), frame.Timestamp);
                        first = false;
                    }

                    after.AddRange(frame.AsSpan<float>(0).ToArray());
                }
            }

            // Decoding restarts 80 ms early (RFC 7845's pre-roll). CELT's energy prediction still settles for a few frames after,
            // as libopus's does, about 4 dB a frame, so 100 ms on the error is 30 dB below the signal; a sample misaligned by even
            // one would stay far worse.
            Assert.True(Snr(after.ToArray().AsSpan(4800, 960), all.AsSpan((int)target + 4800, 960)) >= 30, $"Seeking to {target} does not converge.");
        }
    }

    [Fact]
    public void ConcealsLostPacketsAndUsesFec()
    {
        // Drop every fifth packet of a stream encoded with in-band FEC; the decoder fills each gap and stays in time.
        using IDemuxer demuxer = MediaFile.OpenDemuxer(OggTests.PathOf("opus-fec.opus"));
        AudioTrackInfo track = (AudioTrackInfo)demuxer.Tracks[0];
        using IAudioDecoder decoder = AudioCodecs.CreateDecoder(track);
        int index = 0;
        long samples = 0;
        MediaTime? last = null;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (index++ % 5 == 2)
                {
                    decoder.SendLoss(packet.Duration!.Value);
                    continue;
                }

                decoder.Send(packet);
            }

            while (decoder.TryReceive(out AudioFrame? frame))
            {
                using (frame)
                {
                    if (last is { } l)
                    {
                        Assert.Equal(l, frame.Timestamp);
                    }

                    last = frame.Timestamp + frame.Duration;
                    samples += frame.SampleCount;
                }
            }
        }

        // Every packet's length comes out, lost ones included (the last is trimmed to the stream's end).
        Assert.InRange(samples, 24000, 24000 + track.CodecDelay!.Value.Ticks);
    }

    [Fact]
    public void MapsSurroundToTheCanonicalOrder()
    {
        using AudioReader reader = AudioReader.Open(OggTests.PathOf("opus-51.opus"));
        Assert.Equal(ChannelLayout.Surround51, reader.Layout);
    }

    private static double Snr(ReadOnlySpan<float> got, ReadOnlySpan<float> expected)
    {
        double signal = 0, error = 0;
        for (int i = 0; i < got.Length; i++)
        {
            signal += expected[i] * expected[i];
            error += (got[i] - expected[i]) * (got[i] - expected[i]);
        }

        return 10 * Math.Log10(signal / Math.Max(error, 1e-20));
    }

    private static short[][] Collect(AudioReader reader)
    {
        List<short>[] channels = [];
        foreach (AudioFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                using AudioFrame planar = frame.Convert(SampleFormat.S16Planar);
                if (channels.Length == 0)
                {
                    channels = [.. Enumerable.Range(0, frame.ChannelCount).Select(_ => new List<short>())];
                }

                for (int c = 0; c < frame.ChannelCount; c++)
                {
                    channels[c].AddRange(planar.AsSpan<short>(c).ToArray());
                }
            }
        }

        return [.. channels.Select(c => c.ToArray())];
    }

    private static float[][] Collect32(AudioReader reader)
    {
        List<float>[] channels = [];
        foreach (AudioFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                if (channels.Length == 0)
                {
                    channels = [.. Enumerable.Range(0, frame.ChannelCount).Select(_ => new List<float>())];
                }

                for (int c = 0; c < frame.ChannelCount; c++)
                {
                    channels[c].AddRange(frame.AsSpan<float>(c).ToArray());
                }
            }
        }

        return [.. channels.Select(c => c.ToArray())];
    }
}
