using Egami.Video;
using Egami.Video.Audio;
using Egami.Video.Audio.Codecs;
using Egami.Video.Containers;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Audio
{
    /// <summary>One second of a 440 Hz tone in stereo, as planar float.</summary>
    private static AudioFrame Tone(int rate = 48000)
    {
        AudioFrame frame = new(SampleFormat.F32Planar, ChannelLayout.Stereo, rate, rate, MediaTime.Zero);
        for (int c = 0; c < 2; c++)
        {
            Span<float> samples = frame.GetSpan<float>(c);
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / rate);
            }
        }

        return frame;
    }

    [Fact]
    public void Frames()
    {
        #region audio-frames
        // A frame holds samples for every channel, a sample rate, a layout, a sample format and the time of its first sample.
        using AudioFrame frame = new(SampleFormat.F32Planar, ChannelLayout.Stereo, 48000, 480, MediaTime.Zero);
        Span<float> left = frame.GetSpan<float>(channel: 0);
        for (int i = 0; i < left.Length; i++)
        {
            left[i] = MathF.Sin(2 * MathF.PI * 1000 * i / 48000);
        }

        Console.WriteLine($"{frame.SampleCount} samples, {frame.Duration.ToTimeSpan().TotalMilliseconds} ms, {frame.Layout}");
        #endregion

        Assert.Equal(10, frame.Duration.ToTimeSpan().TotalMilliseconds);
    }

    [Fact]
    public void WriteAndRead()
    {
        using SampleFiles files = new();
        using AudioFrame tone = Tone();

        #region audio-write
        // FLAC for .flac files, PCM for .wav, .w64, .mka and .mp4 unless the options name a codec; FLAC levels run 0 to 8.
        using (AudioWriter writer = AudioWriter.Create("tone.flac", new AudioWriterOptions { Level = 8, BitsPerSample = 24 }))
        {
            writer.Write(tone);
        }

        // 16-bit WAV at 44.1 kHz: the writer resamples and converts.
        using (AudioWriter writer = AudioWriter.Create("tone.wav", new AudioWriterOptions { SampleRate = 44100, BitsPerSample = 16 }))
        {
            writer.Write(tone);
        }
        #endregion

        #region audio-read
        // The first audio track of any file, as planar float by default; seeking is sample exact.
        using AudioReader reader = AudioReader.Open("tone.flac");
        Console.WriteLine($"{reader.Track.CodecId} {reader.SampleRate} Hz {reader.Layout}");
        reader.Seek(MediaTime.FromSeconds(0.5));
        double peak = 0;
        while (reader.TryRead(out AudioFrame? frame))
        {
            using (frame)
            {
                foreach (float sample in frame.AsSpan<float>(channel: 0))
                {
                    peak = Math.Max(peak, Math.Abs(sample));
                }
            }
        }

        // Or ask for one format, rate and layout, and let the reader convert.
        AudioReaderOptions options = new() { SampleFormat = SampleFormat.S16, SampleRate = 22050, Layout = ChannelLayout.Mono };
        using AudioReader mono = AudioReader.Open("tone.wav", options);
        #endregion

        Assert.InRange(peak, 0.49, 0.51);
        Assert.Equal(22050, mono.SampleRate);
    }

    [Fact]
    public void Convert()
    {
        using AudioFrame tone = Tone();

        #region audio-convert
        // Integer samples carry their valid bits: 24-bit audio is S32 with ValidBits 24, from -2^23 to 2^23 - 1.
        using AudioFrame pcm24 = tone.Convert(SampleFormat.S32Planar);
        Console.WriteLine($"{pcm24.Format}, {pcm24.Format.BytesPerSample()} bytes, planar: {pcm24.Format.IsPlanar()}");

        // Down to interleaved 16-bit with triangular dither.
        using AudioFrame cd = tone.Convert(SampleFormat.S16, options: new AudioConvertOptions { Dither = true });
        ReadOnlySpan<short> interleaved = cd.AsSpan<short>();
        #endregion

        Assert.Equal(2 * 48000, interleaved.Length);
    }

    [Fact]
    public void Layouts()
    {
        #region audio-layouts
        // Channels come in WAVE_FORMAT_EXTENSIBLE order whatever the codec stored: 5.1 is FL, FR, FC, LFE, BL, BR.
        ChannelLayout layout = ChannelLayout.Surround51;
        int lfe = layout.IndexOf(Speakers.LowFrequency);
        Speakers fourth = layout.GetSpeaker(3);
        ChannelLayout custom = new(Speakers.FrontLeft | Speakers.FrontRight | Speakers.TopFrontLeft | Speakers.TopFrontRight);
        ChannelLayout ambisonic = ChannelLayout.Unpositioned(4);
        #endregion

        Assert.Equal(3, lfe);
        Assert.Equal(Speakers.LowFrequency, fourth);
        Assert.Equal(4, custom.ChannelCount);
        Assert.False(ambisonic.IsPositioned);
    }

    [Fact]
    public void Mix()
    {
        using AudioFrame surround = new(SampleFormat.F32Planar, ChannelLayout.Surround51, 48000, 480, MediaTime.Zero);

        #region audio-mix
        // ITU-R BS.775 downmix: center and surrounds at -3 dB, LFE dropped unless given a level.
        ChannelMixer downmix = new(ChannelLayout.Surround51, ChannelLayout.Stereo);
        using AudioFrame stereo = downmix.Mix(surround);
        float centerInLeft = downmix[0, 2];

        // Or a matrix of one's own, indexed [output, input].
        ChannelMixer swap = new(ChannelLayout.Stereo, ChannelLayout.Stereo, new float[,] { { 0, 1 }, { 1, 0 } });
        #endregion

        Assert.Equal(ChannelMixer.MinusThreeDecibels, centerInLeft);
        Assert.Equal(1, swap[0, 1]);
    }

    [Fact]
    public void Resample()
    {
        using AudioFrame tone = Tone();

        #region audio-resample
        // 48 kHz to 44.1 kHz with the default quality (100 dB stopband); frames go in and come out like a codec's.
        using Resampler resampler = new(48000, 44100, ChannelLayout.Stereo, ResamplerQuality.Good);
        resampler.Send(tone);
        resampler.Flush();
        long samples = 0;
        while (resampler.TryReceive(out AudioFrame? frame))
        {
            using (frame)
            {
                samples += frame.SampleCount;
            }
        }
        #endregion

        Assert.Equal(44100, samples);
    }

    [Fact]
    public void Codecs()
    {
        using AudioFrame tone = Tone();

        #region audio-encode
        // An encoder takes frames of any length and format and cuts packets of its frame size.
        using IAudioEncoder encoder = FlacCodec.Instance.CreateEncoder(
            new AudioEncoderOptions { SampleRate = 48000, Layout = ChannelLayout.Stereo, BitsPerSample = 16, Level = 5 },
            MediaContext.Default);
        encoder.Send(tone);
        encoder.Flush();
        List<EncodedPacket> packets = [];
        while (encoder.TryReceive(out EncodedPacket? packet))
        {
            packets.Add(packet);
        }
        #endregion

        #region audio-codecs
        // The same send and receive loop as video, for the track the encoder describes (or one a demuxer read).
        using IAudioDecoder decoder = AudioCodecs.CreateDecoder(encoder.Track);
        long decoded = 0;
        foreach (EncodedPacket packet in packets)
        {
            using (packet)
            {
                decoder.Send(packet);
            }

            while (decoder.TryReceive(out AudioFrame? frame))
            {
                using (frame)
                {
                    decoded += frame.SampleCount;
                }
            }
        }

        // FLAC decodes to its own integers: S16Planar up to 16 bits, S32Planar above.
        #endregion

        Assert.Equal(48000, decoded);
    }

    [Fact]
    public void Containers()
    {
        using SampleFiles files = new();
        using AudioFrame tone = Tone();
        using (AudioWriter writer = AudioWriter.Create("tone.wav"))
        {
            writer.Write(tone);
        }

        #region audio-containers
        // WAV, Wave64 and FLAC are containers like the others: remux PCM from WAV into Wave64 without decoding.
        MediaFile.Remux("tone.wav", "tone.w64");
        MediaInfo info = MediaFile.Probe("tone.w64");
        AudioTrackInfo track = (AudioTrackInfo)info.Tracks[0];
        Console.WriteLine($"{info.FormatName}: {track.CodecId}, {track.SampleRate} Hz, {track.BitsPerSample} bits, {track.Layout}");
        #endregion

        Assert.Equal("Wave64", info.FormatName);
    }

    [Fact]
    public void VideoWithAudio()
    {
        using SampleFiles files = new();
        using AudioFrame tone = Tone();
        using Raster<Egami.Pixels.Rgb8> picture = Raster.Create<Egami.Pixels.Rgb8>(64, 48);

        #region audio-video
        // A video writer can encode an audio track next to the video.
        VideoWriterOptions options = new() { AudioCodec = FlacCodec.Instance, AudioSampleRate = 48000, AudioLayout = ChannelLayout.Stereo };
        using (VideoWriter writer = VideoWriter.Create("clip.mkv", options))
        {
            for (int i = 0; i < 30; i++)
            {
                writer.WriteFrame(picture);
            }

            writer.WriteAudio(tone);
        }
        #endregion

        Assert.Equal(2, MediaFile.Probe("clip.mkv").Tracks.Count);
    }

    [Fact]
    public void Transcode()
    {
        using SampleFiles files = new();
        using AudioFrame tone = Tone();
        using (AudioWriter writer = AudioWriter.Create("talk.mka"))
        {
            writer.Write(tone);
        }

        #region audio-transcode
        // Keep every other track as it is and turn the audio into FLAC.
        MediaFile.Transcode("talk.mka", "talk-flac.mkv", audio: FlacCodec.Instance, audioLevel: 8);
        #endregion

        Assert.Equal(CodecIds.Flac, MediaFile.Probe("talk-flac.mkv").Tracks[0].CodecId);
    }
}
