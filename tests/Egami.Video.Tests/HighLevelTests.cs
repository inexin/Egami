using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;
using Egami.Video.Tests.Containers;

namespace Egami.Video.Tests;

/// <summary>The examples from the V1 design, run as tests, plus the reader's and writer's other promises.</summary>
public class HighLevelTests
{
    [Fact]
    public void DesignExampleReadFramesAndSaveThumbnails()
    {
        string directory = AviWriterTests.TempFile("thumbs");
        Directory.CreateDirectory(directory);
        int thumbnails = 0;

        using var reader = VideoReader.Open(Corpus.PathOf("avi-mjpeg-420-pcm.avi"));
        foreach (VideoFrame<Rgb8> frame in reader.ReadFrames<Rgb8>())
        {
            using (frame)
            {
                if (frame.Timestamp.ToTimeSpan().TotalSeconds % 1 < 0.04)
                {
                    using Raster<Rgb8> thumb = frame.Raster.Derive(x => x.Fit(80, 60), TestContext.Current.CancellationToken);
                    thumb.Save(Path.Combine(directory, $"thumb-{frame.Timestamp.Ticks}.png"));
                    thumbnails++;
                }
            }
        }

        Assert.Equal(2, thumbnails);
        Assert.Equal((80, 60), (Raster.Probe(Path.Combine(directory, "thumb-25.png")).Width, Raster.Probe(Path.Combine(directory, "thumb-25.png")).Height));
    }

    [Theory]
    [InlineData("out.avi")]
    [InlineData("out.mkv")]
    public void DesignExampleWriteAClipFromRasters(string name)
    {
        string path = AviWriterTests.TempFile(name);
        List<Raster<Rgb8>> images = [.. Enumerable.Range(0, 30).Select(Gradient)];
        using (var writer = VideoWriter.Create(path, new VideoWriterOptions { Codec = VideoCodecs.MotionJpeg, FrameRate = new Rational(30, 1), Quality = 85 }))
        {
            foreach (Raster<Rgb8> image in images)
            {
                writer.WriteFrame(image);
            }
        }

        using VideoReader reader = VideoReader.Open(path);
        Assert.Equal(new Rational(30, 1), reader.Track.FrameRate);
        int i = 0;
        foreach (VideoFrame<Rgb8> frame in reader.ReadFrames<Rgb8>())
        {
            using (frame)
            {
                // Matroska stores milliseconds.
                Rational ms = new(1, 1000);
                Assert.Equal(new MediaTime(i, new Rational(1, 30)).Rescale(ms), frame.Timestamp.Rescale(ms));
                Rgb8 expected = images[i].GetPixel(40, 20), actual = frame.Raster.GetPixel(40, 20);
                Assert.InRange(Math.Abs(expected.R - actual.R), 0, 12);
                i++;
            }
        }

        Assert.Equal(30, i);
        images.ForEach(image => image.Dispose());
    }

    [Fact]
    public void DesignExampleProbeAndRemuxWithoutDecodingKeepsAudio()
    {
        string output = AviWriterTests.TempFile("remux.mkv");
        MediaInfo info = MediaFile.Probe(Corpus.PathOf("avi-mjpeg-420-pcm.avi"));
        Assert.Equal(2, info.Tracks.Count);
        using (var demuxer = MediaFile.OpenDemuxer(Corpus.PathOf("avi-mjpeg-420-pcm.avi")))
        using (var muxer = MediaFile.CreateMuxer(output, demuxer.Tracks))
        {
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    muxer.WritePacket(packet);
                }
            }
        }

        MediaInfo remuxed = MediaFile.Probe(output);
        Assert.Equal("Matroska", remuxed.FormatName);
        Assert.Equal([CodecIds.MotionJpeg, CodecIds.PcmLittleEndian], remuxed.Tracks.Select(t => t.CodecId));

        string again = AviWriterTests.TempFile("remux-helper.avi");
        Assert.Equal(Corpus.ReadProbe(Corpus.PathOf("avi-mjpeg-420-pcm.avi")).Packets.Count, MediaFile.Remux(output, again));
        Assert.Equal(1, MediaFile.Remux(output, again, t => t.Kind == TrackKind.Video) > 0 ? MediaFile.Probe(again).Tracks.Count : 0);
    }

    [Fact]
    public void DesignExampleLowLevelCodecLoop()
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Corpus.PathOf("mkv-mjpeg-pcm.mkv"));
        VideoTrackInfo videoTrack = demuxer.Info.VideoTrack!;
        using var decoder = VideoCodecs.CreateDecoder<Rgb8>(videoTrack);
        int frames = 0;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber == videoTrack.Number)
                {
                    decoder.Send(packet);
                }
            }

            while (decoder.TryReceive(out VideoFrame<Rgb8>? frame))
            {
                using (frame)
                {
                    frames++;
                }
            }
        }

        decoder.Flush();
        while (decoder.TryReceive(out VideoFrame<Rgb8>? frame))
        {
            using (frame)
            {
                frames++;
            }
        }

        Assert.Equal(50, frames);
    }

    [Fact]
    public void ReaderSeeksToTheKeyframeAtOrBeforeTheTime()
    {
        using VideoReader reader = VideoReader.Open(Corpus.PathOf("mkv-mjpeg-pcm.mkv"));
        Assert.True(reader.CanSeek);
        _ = reader.ReadFrames().Take(10).Select(f => { f.Dispose(); return 0; }).Count();
        reader.Seek(MediaTime.FromSeconds(1.23));
        using YuvVideoFrame frame = reader.ReadFrame()!;
        Assert.Equal(new MediaTime(1200, new Rational(1, 1000)), frame.Timestamp);
    }

    [Fact]
    public async Task OpenAndProbeAsync()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        MediaInfo info = await MediaFile.ProbeAsync(Corpus.PathOf("webm-vp8-opus.webm"), cancellationToken: cancel);
        Assert.Equal("WebM", info.FormatName);
        byte[] bytes = await File.ReadAllBytesAsync(Corpus.PathOf("avi-mjpeg-444-noaudio.avi"), cancel);
        using VideoReader reader = await VideoReader.OpenAsync(new ForwardOnlyStream(new MemoryStream(bytes)), cancellationToken: cancel);
        Assert.True(reader.CanSeek, "a non-file stream is buffered into memory");
        Assert.Equal(20, reader.ReadFrames().Select(f => { f.Dispose(); return 0; }).Count());
    }

    [Fact]
    public void CorruptFramesCanBeSkipped()
    {
        byte[] file = File.ReadAllBytes(Corpus.PathOf("avi-mjpeg-444-noaudio.avi"));

        // Wipe the third frame's JPEG data to non-JPEG bytes.
        int movi = AviReaderTests.FindChunk(file, "movi");
        int position = movi + 4;
        for (int i = 0; i < 2; i++)
        {
            position += 8 + BitConverter.ToInt32(file, position + 4);
            position += position & 1;
        }

        file.AsSpan(position + 8, 64).Fill(0x11);
        using (VideoReader strict = VideoReader.Open(new MemoryStream(file), new VideoReaderOptions { Errors = DecodeErrorMode.Throw }))
        {
            Assert.Throws<CorruptDataException>(() => strict.ReadFrames().Select(f => { f.Dispose(); return 0; }).Count());
        }

        // Skipping is the default, and each dropped frame is reported.
        List<EgamiDiagnostic> reported = [];
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { Diagnostics = reported.Add } };
        using (VideoReader lenient = VideoReader.Open(new MemoryStream(file), context: context))
        {
            Assert.Equal(19, lenient.ReadFrames().Select(f => { f.Dispose(); return 0; }).Count());
        }

        Assert.Equal("Video.CorruptFrame", Assert.Single(reported).Id);
    }

    [Fact]
    public void TranscodeKeepsTheAudioTrack()
    {
        string output = AviWriterTests.TempFile("transcode.mkv");
        using (VideoReader reader = VideoReader.Open(Corpus.PathOf("avi-mjpeg-420-pcm.avi")))
        using (IDemuxer audio = MediaFile.OpenDemuxer(Corpus.PathOf("avi-mjpeg-420-pcm.avi")))
        {
            TrackInfo sound = audio.Tracks.First(t => t.Kind == TrackKind.Audio);
            using VideoWriter writer = VideoWriter.Create(output, new VideoWriterOptions { FrameRate = new Rational(25, 1), OtherTracks = [sound] });
            foreach (YuvVideoFrame frame in reader.ReadFrames())
            {
                using (frame)
                {
                    writer.WriteFrame(frame);
                }
            }

            while (audio.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    if (packet.TrackNumber == sound.Number)
                    {
                        writer.WritePacket(packet);
                    }
                }
            }
        }

        MediaInfo info = MediaFile.Probe(output);
        Assert.Equal([TrackKind.Audio, TrackKind.Video], info.Tracks.Select(t => t.Kind));
    }

    private static Raster<Rgb8> Gradient(int shift)
    {
        Raster<Rgb8> raster = Raster.Create<Rgb8>(96, 64);
        for (int y = 0; y < raster.Height; y++)
        {
            Span<Rgb8> row = raster.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = new Rgb8((byte)((x * 2) + (shift * 4)), (byte)(y * 3), 128);
            }
        }

        return raster;
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
