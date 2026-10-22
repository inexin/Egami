using Egami;
using Egami.Formats.Jpeg;
using Egami.Pixels;
using Egami.Video;
using Egami.Video.Containers;

/// <summary>Writes small seed files: AFL++ works best from inputs of a few kilobytes.</summary>
internal static class Seeds
{
    /// <summary>VP8 seeds cut from the corpus (tests/video/corpus/vp8, read from the repo root): the first packets of a few clips.</summary>
    public static void WriteVp8(string directory)
    {
        string ivf = Directory.CreateDirectory(Path.Combine(directory, "ivf")).FullName;
        string webm = Directory.CreateDirectory(Path.Combine(directory, "webm")).FullName;
        string vp8 = Directory.CreateDirectory(Path.Combine(directory, "vp8")).FullName;
        foreach ((string clip, string format, int packets, string target) in (ReadOnlySpan<(string, string, int, string)>)[
            ("v0-odd-97x61.ivf", "ivf", 4, ivf), ("v3-pan.ivf", "ivf", 3, ivf), ("alpha-odd-75x45.webm", "webm", 3, webm),
            ("v0-odd-97x61.webm", "webm", 3, webm), ("v0-odd-97x61.ivf", "ivf", 6, vp8), ("alpha-odd-75x45.webm", "webm", 4, vp8)])
        {
            using IDemuxer demuxer = MediaFile.OpenDemuxer(Path.Combine("tests", "video", "corpus", "vp8", clip));
            MemoryStream output = new();
            using (IMuxer muxer = MediaFile.CreateMuxer(output, format, demuxer.Tracks, leaveOpen: true))
            {
                for (int i = 0; i < packets && demuxer.ReadPacket() is { } packet; i++)
                {
                    using (packet)
                    {
                        muxer.WritePacket(packet);
                    }
                }
            }

            File.WriteAllBytes(Path.Combine(target, $"{Path.GetFileNameWithoutExtension(clip)}-{packets}.{format}"), output.ToArray());
        }
    }

    /// <summary>VP9 seeds cut from the corpus (tests/video/corpus/vp9): the first packets of a few clips, in files and as raw packets.</summary>
    public static void WriteVp9(string directory)
    {
        string vp9 = Directory.CreateDirectory(Path.Combine(directory, "vp9")).FullName;
        string raw = Directory.CreateDirectory(Path.Combine(directory, "vp9raw")).FullName;
        foreach ((string clip, string format, int packets) in (ReadOnlySpan<(string, string, int)>)[
            ("p0-odd-97x61.ivf", "ivf", 4), ("p0-altref.webm", "webm", 3), ("alpha-odd-75x45.webm", "webm", 3), ("p2-10bit.ivf", "ivf", 3),
            ("p1-444.ivf", "ivf", 2), ("p0-tiles.ivf", "ivf", 2)])
        {
            using IDemuxer demuxer = MediaFile.OpenDemuxer(Path.Combine("tests", "video", "corpus", "vp9", clip));
            MemoryStream output = new();
            MemoryStream rawOutput = new();
            byte[] codecPrivate = demuxer.Tracks[0].CodecPrivate.ToArray();
            rawOutput.WriteByte((byte)codecPrivate.Length);
            rawOutput.Write(codecPrivate);
            using (IMuxer muxer = MediaFile.CreateMuxer(output, format, demuxer.Tracks, leaveOpen: true))
            {
                for (int i = 0; i < packets && demuxer.ReadPacket() is { } packet; i++)
                {
                    using (packet)
                    {
                        muxer.WritePacket(packet);
                        rawOutput.WriteByte((byte)packet.Data.Length);
                        rawOutput.WriteByte((byte)(packet.Data.Length >> 8));
                        rawOutput.Write(packet.Data.Span);
                    }
                }
            }

            string name = $"{Path.GetFileNameWithoutExtension(clip)}-{packets}";
            File.WriteAllBytes(Path.Combine(vp9, $"{name}.{format}"), output.ToArray());
            File.WriteAllBytes(Path.Combine(raw, $"{name}.bin"), rawOutput.ToArray());
        }
    }

    public static void Write(string directory)
    {
        string avi = Directory.CreateDirectory(Path.Combine(directory, "avi")).FullName;
        string mkv = Directory.CreateDirectory(Path.Combine(directory, "mkv")).FullName;
        string mjpeg = Directory.CreateDirectory(Path.Combine(directory, "mjpeg")).FullName;
        string y4m = Directory.CreateDirectory(Path.Combine(directory, "y4m")).FullName;
        WriteVp8(directory);
        WriteVp9(directory);
        File.WriteAllBytes(Path.Combine(y4m, "420.y4m"), [.. "YUV4MPEG2 W4 H2 F25:1 Ip A1:1 C420jpeg XCOLORRANGE=FULL\n"u8, .. "FRAME\n"u8, .. new byte[12], .. "FRAME\n"u8, .. new byte[12]]);
        File.WriteAllBytes(Path.Combine(y4m, "444p10.y4m"), [.. "YUV4MPEG2 W2 H2 F30000:1001 It A4:3 C444p10\n"u8, .. "FRAME\n"u8, .. new byte[24]]);

        byte[] frame = Jpeg(16, 16, ChromaSubsampling.Subsample420, 50);
        File.WriteAllBytes(Path.Combine(mjpeg, "420.jpg"), frame);
        File.WriteAllBytes(Path.Combine(mjpeg, "422.jpg"), Jpeg(16, 8, ChromaSubsampling.Subsample422, 50));
        File.WriteAllBytes(Path.Combine(mjpeg, "nodht.jpg"), StripDht(frame));
        byte[] avi1 = [0xFF, 0xE0, 0x00, 0x10, (byte)'A', (byte)'V', (byte)'I', (byte)'1', 1, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] field = Jpeg(16, 8, ChromaSubsampling.Subsample422, 30);
        File.WriteAllBytes(Path.Combine(mjpeg, "fields.jpg"), [.. field[..2], .. avi1, .. field[2..], .. field]);

        VideoTrackInfo video = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.MotionJpeg, TimeBase = new Rational(1, 25), Width = 16, Height = 16, FrameRate = new Rational(25, 1) };
        AudioTrackInfo audio = new() { Number = 2, Kind = TrackKind.Audio, CodecId = CodecIds.PcmLittleEndian, TimeBase = new Rational(1, 8000), SampleRate = 8000, Channels = 1, BitsPerSample = 16 };
        File.WriteAllBytes(Path.Combine(avi, "mjpeg-pcm.avi"), Mux("avi", [video, audio], frame, 3));
        File.WriteAllBytes(Path.Combine(avi, "mjpeg.avi"), Mux("avi", [video], frame, 2));
        File.WriteAllBytes(Path.Combine(mkv, "mjpeg-pcm.mkv"), Mux("mkv", [video, audio], frame, 3));
        File.WriteAllBytes(Path.Combine(mkv, "mjpeg.mkv"), Mux("mkv", [video], frame, 2));
        File.WriteAllBytes(Path.Combine(mkv, "live.mkv"), Mux("mkv", [video, audio], frame, 2, seekable: false));
        File.WriteAllBytes(Path.Combine(mkv, "metadata.mkv"), Mux("mkv", [video with { Tags = new Dictionary<string, string> { ["ENCODER"] = "x" } }], frame, 1, metadata: new MediaMetadata
        {
            Tags = new Dictionary<string, string> { ["TITLE"] = "t", ["ARTIST"] = "a" },
            Chapters = [new ChapterInfo(MediaTime.Zero, new MediaTime(1, new Rational(1, 25)), "c")],
            Attachments = [new AttachmentInfo("cover.jpg", "image/jpeg", frame)],
        }));
    }

    private static byte[] Mux(string format, IReadOnlyList<TrackInfo> tracks, byte[] frame, int count, bool seekable = true, MediaMetadata? metadata = null)
    {
        MemoryStream output = new();
        Stream target = seekable ? output : new UnseekableWriter(output);
        using (IMuxer muxer = MediaFile.CreateMuxer(target, format, tracks, leaveOpen: true, metadata: metadata))
        {
            for (int i = 0; i < count; i++)
            {
                using EncodedPacket video = new(1, frame, new MediaTime(i, new Rational(1, 25))) { IsKeyframe = true };
                muxer.WritePacket(video);
                if (tracks.Count > 1)
                {
                    using EncodedPacket sound = new(2, new byte[640], new MediaTime(i * 320, new Rational(1, 8000))) { IsKeyframe = true };
                    muxer.WritePacket(sound);
                }
            }
        }

        return output.ToArray();
    }

    private static byte[] Jpeg(int width, int height, ChromaSubsampling subsampling, int quality)
    {
        using Raster<Rgb8> raster = Raster.Create<Rgb8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgb8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgb8((byte)(x * 16), (byte)(y * 16), (byte)((x ^ y) * 16));
            }
        }

        using MemoryStream output = new();
        raster.Save(output, JpegFormat.Info, new JpegWriterOptions { Quality = quality, Subsampling = subsampling, StripMetadata = true });
        return output.ToArray();
    }

    private static byte[] StripDht(byte[] jpeg)
    {
        List<byte> output = [jpeg[0], jpeg[1]];
        int position = 2;
        while (position + 4 <= jpeg.Length && jpeg[position] == 0xFF && jpeg[position + 1] != 0xDA)
        {
            int length = (jpeg[position + 2] << 8) | jpeg[position + 3];
            if (jpeg[position + 1] != 0xC4)
            {
                output.AddRange(jpeg.AsSpan(position, 2 + length));
            }

            position += 2 + length;
        }

        output.AddRange(jpeg.AsSpan(position));
        return [.. output];
    }

    private sealed class UnseekableWriter(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
