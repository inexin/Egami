using Egami.Colors;
using Egami.Pixels;
using Egami.Video;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Video
{
    [Fact]
    public void Probe()
    {
        using SampleFiles files = new();

        #region video-probe
        // What a file holds, without decoding: its container, duration, tracks, tags, chapters and attachments.
        MediaInfo info = MediaFile.Probe("metadata.mkv");
        Console.WriteLine($"{info.FormatName}, {info.Duration?.ToTimeSpan()}");
        foreach (TrackInfo track in info.Tracks)
        {
            string details = track switch
            {
                VideoTrackInfo v => $"{v.Width}x{v.Height} {v.BitDepth}-bit {v.ChromaFormat}, {v.FieldOrder}",
                AudioTrackInfo a => $"{a.SampleRate} Hz, {a.Channels} channels",
                _ => "",
            };
            Console.WriteLine($"#{track.Number} {track.Kind} {track.CodecId} {details}");
        }

        MediaMetadata metadata = info.Metadata;
        foreach (ChapterInfo chapter in metadata.Chapters)
        {
            Console.WriteLine($"{chapter.Start.ToTimeSpan()} {chapter.Title}");
        }

        foreach (AttachmentInfo attachment in metadata.Attachments)
        {
            File.WriteAllBytes(attachment.Name, attachment.Data.ToArray());
        }
        #endregion

        Assert.Equal(TrackKind.Video, info.Tracks[0].Kind);
        Assert.Equal(FieldOrder.Progressive, info.VideoTrack!.FieldOrder);
    }

    [Fact]
    public void ReadFrames()
    {
        using SampleFiles files = new();

        #region video-read
        // Every frame as RGB pixels: VP9, VP8, Motion JPEG or raw, from WebM, Matroska, AVI, IVF or YUV4MPEG2.
        using VideoReader reader = VideoReader.Open("movie.webm");
        Console.WriteLine($"{reader.Track.Width}x{reader.Track.Height} at {reader.Track.FrameRate?.ToDouble()} fps");
        foreach (VideoFrame<Rgba8> frame in reader.ReadFrames<Rgba8>())
        {
            using (frame)
            {
                frame.Raster.Save($"frame-{frame.Timestamp.ToTimeSpan().TotalMilliseconds:0000}.png");
            }
        }
        #endregion

        Assert.NotEmpty(Directory.GetFiles(".", "frame-*.png"));
    }

    [Fact]
    public void ReadPlanes()
    {
        using SampleFiles files = new();

        #region video-planes
        // The codec's own planes, with no conversion: luma and chroma at the stream's subsampling and bit depth.
        using VideoReader reader = VideoReader.Open("movie.webm");
        using YuvVideoFrame first = reader.ReadFrame()!;
        YuvRaster planes = first.Raster;
        Console.WriteLine($"{planes.ChromaFormat} {planes.BitDepth}-bit, color {first.Color}");
        VideoFrame frame = first;                     // the base type encoders and writers take
        using Raster<Rgb8> rgb = first.ToRaster<Rgb8>();
        #endregion

        Assert.Equal(ChromaFormat.Yuv420, planes.ChromaFormat);
        Assert.Equal(frame.Width, rgb.Width);
    }

    [Fact]
    public void Options()
    {
        using SampleFiles files = new();

        #region video-options
        // How a reader decodes: which track, what a broken frame does (Skip, the default, drops it up to the next keyframe;
        // Conceal repeats the last good frame; Throw throws) and the codec's own options.
        VideoReaderOptions options = new()
        {
            TrackNumber = 1,
            Errors = DecodeErrorMode.Conceal,
            Decoder = new VideoDecoderOptions { DecodeAlpha = false },
        };
        using VideoReader reader = VideoReader.Open("alpha.webm", options);
        using YuvVideoFrame frame = reader.ReadFrame()!;
        #endregion

        Assert.Null(Assert.IsType<YuvRaster<byte>>(frame.Raster).A);
    }

    [Fact]
    public async Task OpenAsync()
    {
        using SampleFiles files = new();

        #region video-open-async
        // Opening reads the headers; the async forms do that off the calling thread.
        using VideoReader reader = await VideoReader.OpenAsync("movie.webm", VideoReaderOptions.Default);
        using YuvVideoFrame? frame = reader.ReadFrame();
        #endregion

        Assert.NotNull(frame);
    }

    [Fact]
    public void Seek()
    {
        using SampleFiles files = new();

        #region video-seek
        // An exact seek lands on the frame shown at the time; SeekMode.Keyframe stops at the keyframe before it, which is faster.
        using VideoReader reader = VideoReader.Open("movie.webm");
        reader.Seek(MediaTime.FromSeconds(0.5));
        using YuvVideoFrame atHalf = reader.ReadFrame()!;
        reader.Seek(MediaTime.FromSeconds(0.5), SeekMode.Keyframe);
        using YuvVideoFrame keyframe = reader.ReadFrame()!;
        #endregion

        Assert.True(keyframe.Timestamp <= atHalf.Timestamp);
    }

    [Fact]
    public void Write()
    {
        using SampleFiles files = new();
        using Raster<Rgb8> duck = Raster.Load<Rgb8>("duck.png");

        #region video-write
        // Images into a Motion JPEG clip; the container comes from the extension.
        VideoWriterOptions options = new() { Codec = VideoCodecs.MotionJpeg, FrameRate = new Rational(25, 1), Quality = 85 };
        using (VideoWriter writer = VideoWriter.Create("duck.mkv", options))
        {
            for (int i = 0; i < 10; i++)
            {
                writer.WriteFrame(duck);
            }
        }

        // Or into a stream, in a format by name.
        using MemoryStream avi = new();
        using (VideoWriter writer = VideoWriter.Create(avi, "avi", options, leaveOpen: true))
        {
            writer.WriteFrame(duck);
        }
        #endregion

        Assert.Equal(TrackKind.Video, MediaFile.Probe("duck.mkv").Tracks[0].Kind);
    }

    [Fact]
    public void Remux()
    {
        using SampleFiles files = new();

        #region video-remux
        // Copy the packets into another container without decoding; the sound and the tags come along.
        long packets = MediaFile.Remux("camera.avi", "camera.mkv");

        // Or packet by packet, choosing what goes through.
        using IDemuxer demuxer = MediaFile.OpenDemuxer("movie.webm");
        using (IMuxer muxer = MediaFile.CreateMuxer("movie.ivf", [demuxer.Info.VideoTrack!]))
        {
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    // Data, the time and whether it starts a keyframe; WebM alpha travels in Additions.
                    IReadOnlyList<PacketAddition> additions = packet.Additions;
                    muxer.WritePacket(packet);
                }
            }

            muxer.Finish();
        }
        #endregion

        Assert.True(packets > 0);
        Assert.True(File.Exists("movie.ivf"));
    }

    [Fact]
    public void Decode()
    {
        using SampleFiles files = new();

        #region video-decode
        // A codec by hand: packets in, frames out. VideoCodecs finds the codec for a track's CodecId (CodecIds.Vp9 here).
        using IDemuxer demuxer = MediaFile.OpenDemuxer("movie.webm");
        VideoTrackInfo track = demuxer.Info.VideoTrack!;
        bool isVp9 = track.CodecId == CodecIds.Vp9;
        using IVideoDecoder decoder = VideoCodecs.CreateDecoder(track, new VideoDecoderOptions { Errors = DecodeErrorMode.Throw });
        int frames = 0;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                decoder.Send(packet);
            }

            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                using (frame)
                {
                    frames++;
                }
            }
        }

        decoder.Flush();
        #endregion

        Assert.True(isVp9);
        Assert.True(frames > 0);
    }

    [Fact]
    public void Codecs()
    {
        using SampleFiles files = new();
        using IDemuxer demuxer = MediaFile.OpenDemuxer("movie.webm");
        VideoTrackInfo track = demuxer.Info.VideoTrack!;

        #region video-codecs
        // The built-in codecs, for a MediaContext of your own or to create decoders directly.
        VideoCodec vp9 = Vp9Codec.Instance, vp8 = Vp8Codec.Instance, mjpeg = MotionJpegCodec.Instance, raw = RawVideoCodec.Instance;
        using IVideoDecoder decoder = vp9.CreateDecoder(track, VideoDecoderOptions.Default, MediaContext.Default);

        // Decoders that hand out RGB frames instead of planes.
        using IVideoDecoder<Rgba8> rgb = VideoCodecs.CreateDecoder<Rgba8>(track);
        #endregion

        Assert.Equal(CodecIds.Vp8, vp8.CodecId);
        Assert.Equal(CodecIds.MotionJpeg, mjpeg.CodecId);
        Assert.Equal(CodecIds.Uncompressed, raw.CodecId);
    }

    [Fact]
    public void Encode()
    {
        using SampleFiles files = new();
        using Raster<Rgb8> duck = Raster.Load<Rgb8>("duck.png");

        #region video-encode
        // An encoder by hand: frames in, packets out, for a muxer of your own.
        VideoEncoderOptions options = new() { Width = duck.Width, Height = duck.Height, FrameRate = new Rational(30, 1), Quality = 90 };
        using IVideoEncoder encoder = MotionJpegCodec.Instance.CreateEncoder(options, MediaContext.Default);
        using (VideoFrame<Rgb8> frame = new(Raster.Load<Rgb8>("duck.png"), MediaTime.Zero))
        {
            encoder.Send(frame);
        }

        encoder.Flush();
        List<EncodedPacket> packets = [];
        while (encoder.TryReceive(out EncodedPacket? packet))
        {
            packets.Add(packet);
        }

        // Uncompressed frames go the same way, losslessly.
        using IVideoEncoder rawEncoder = RawVideoCodec.Instance.CreateEncoder(options with { ChromaFormat = ChromaFormat.Yuv444 }, MediaContext.Default);
        #endregion

        Assert.Single(packets);
        packets[0].Dispose();
    }

    [Fact]
    public void Headers()
    {
        using SampleFiles files = new();

        #region video-headers
        // A packet's header fields under the codec's own names, as a hex dump with meaning.
        using IDemuxer demuxer = MediaFile.OpenDemuxer("movie.webm");
        TrackInfo track = demuxer.Tracks[0];
        using EncodedPacket packet = demuxer.ReadPacket()!;
        if (CodecHeaders.CanDescribe(track))
        {
            foreach (CodecHeaderField field in CodecHeaders.Describe(track, packet))
            {
                Console.WriteLine($"{field.Name} = {field.Value}");
            }
        }
        #endregion

        Assert.Contains(CodecHeaders.Describe(track, packet), f => f.Name == "frame_type");
    }

    [Fact]
    public void Hdr()
    {
        using SampleFiles files = new();

        #region video-hdr
        // An HDR10 frame tone mapped to SDR through Egami's color pipeline, from its mastering display and light levels.
        using (VideoReader reader = VideoReader.Open("hdr10.mkv"))
        using (YuvVideoFrame frame = reader.ReadFrame()!)
        using (Raster<Rgba8> sdr = frame.ToRaster<Rgba8>(ColorProfile.Srgb))
        {
            sdr.Save("sdr.png");
        }

        // HDR10+ dynamic metadata, frame by frame.
        using VideoReader plus = VideoReader.Open("hdr10plus.webm");
        foreach (YuvVideoFrame frame in plus.ReadFrames())
        {
            using (frame)
            {
                if (frame.Hdr10Plus is { } meta)
                {
                    double maxScl = meta.MaxScl.Max();                      // cd/m²
                    Hdr10PlusPercentile median = meta.DistributionMaxRgb.First(p => p.Percentage == 50);
                    Hdr10PlusToneCurve? curve = meta.ToneCurve;
                    Console.WriteLine($"MaxSCL {maxScl}, median {median.Value} cd/m², knee {curve?.KneeX}");
                }
            }
        }
        #endregion

        Assert.True(File.Exists("sdr.png"));
    }

    [Fact]
    public void Quality()
    {
        using SampleFiles files = new();

        #region video-quality
        // PSNR and SSIM of a decode against a reference, plane by plane, and the MD5 of a frame as libvpx's tests take it.
        VideoQuality quality = new();
        using (VideoReader reference = VideoReader.Open("movie.webm"))
        using (VideoReader distorted = VideoReader.Open("movie.webm"))
        {
            foreach ((YuvVideoFrame a, YuvVideoFrame b) in reference.ReadFrames().Zip(distorted.ReadFrames()))
            {
                using (a)
                using (b)
                {
                    quality.Add(a.Raster, b.Raster);
                    string md5 = VideoQuality.Md5(a.Raster);
                }
            }
        }

        PlaneScores psnr = quality.Psnr, ssim = quality.Ssim;
        Console.WriteLine($"{quality.Frames} frames: PSNR Y {psnr.Y:0.00} dB, SSIM {ssim.Average:0.0000}");
        #endregion

        Assert.Equal(1.0, quality.Ssim.Average, 6);
    }

    [Fact]
    public void Limits()
    {
        using SampleFiles files = new();

        #region video-limits
        // Limits for files from outside, and a handler for what a reader recovers from (a broken index, a cut-off cluster).
        List<MediaWarning> warnings = [];
        MediaContext context = MediaContext.Default with
        {
            Limits = MediaLimits.Default with { MaxPacketBytes = 8 << 20, MaxTracks = 8 },
            Warnings = warnings.Add,
            Egami = EgamiContext.Default with { MaxParallelism = 2 },
        };
        using VideoReader reader = VideoReader.Open("movie.webm", null, context);
        foreach (MediaWarning warning in warnings)
        {
            Console.WriteLine($"{warning.Code}: {warning.Message}");
        }
        #endregion

        Assert.Empty(warnings);
    }

    [Fact]
    public void Containers()
    {
        using SampleFiles files = new();

        #region video-containers
        // The containers, by name or extension, and headerless raw frames in a layout you give.
        ContainerFormat? webm = MediaContext.Default.FindContainer("webm");
        bool known = webm is MatroskaFormat && MediaContext.Default.FindContainer("avi") is AviFormat
            && MediaContext.Default.FindContainer("ivf") is IvfFormat && MediaContext.Default.FindContainer("y4m") is Y4mFormat;

        VideoTrackInfo track = new()
        {
            Number = 1,
            Kind = TrackKind.Video,
            CodecId = CodecIds.Uncompressed,
            FourCC = "I420",
            TimeBase = new Rational(1, 25),
            FrameRate = new Rational(25, 1),
            Width = 64,
            Height = 48,
        };
        using MemoryStream frames = new(new byte[64 * 48 * 3 / 2 * 2]);
        using IDemuxer raw = RawVideo.OpenDemuxer(frames, track);
        #endregion

        Assert.True(known);
        Assert.NotNull(raw.ReadPacket());
    }

    [Fact]
    public void Mp4()
    {
        using SampleFiles files = new();

        #region video-mp4
        // MP4 and QuickTime files read like any other: rotation from the display matrix, the start timecode, edit lists applied.
        MediaInfo phone = MediaFile.Probe("phone.mov");
        Console.WriteLine($"{phone.FormatName}: turn {phone.VideoTrack!.Orientation} to show upright");
        Console.WriteLine($"starts at timecode {MediaFile.Probe("camera.mov").Timecode}");

        // Frames come out as coded; AutoOrient turns each one upright.
        using (VideoReader reader = VideoReader.Open("phone.mov", new VideoReaderOptions { AutoOrient = true }))
        {
            using VideoFrame<Rgb8> upright = reader.ReadFrames<Rgb8>().First();
            Console.WriteLine($"{reader.Track.Width}x{reader.Track.Height} coded, {upright.Raster.Width}x{upright.Raster.Height} shown");
        }

        // Writing MP4: moov before the media (the file plays while it downloads), tags, chapters and cover art kept.
        MediaFile.Remux("movie.webm", "movie.mp4");
        MediaFile.Remux("metadata.mkv", "metadata.mp4");

        // mov_text subtitles: each packet's text and style runs.
        using IDemuxer demuxer = MediaFile.OpenDemuxer("subtitled.mp4");
        TrackInfo subtitles = demuxer.Tracks.First(t => t.CodecId == CodecIds.MovText);
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber == subtitles.Number)
                {
                    MovTextSample text = MovTextSample.Parse(packet.Data.Span);
                    Console.WriteLine($"{packet.Timestamp.ToTimeSpan()}: {text.Text} ({text.Styles.Count} styles)");
                }
            }
        }
        #endregion

        Assert.Equal(Egami.Metadata.ImageOrientation.Rotate270, phone.VideoTrack.Orientation);
        Assert.Equal("MP4", MediaFile.Probe("movie.mp4").FormatName);
        Assert.Equal(2, MediaFile.Probe("metadata.mp4").Chapters.Count);
    }
}
