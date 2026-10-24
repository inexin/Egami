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

    /// <summary>
    /// Trim seeds: four bytes (mode, start and length in 1/64 s, output container) and the first packets of corpus clips with a
    /// keyframe after their first (tests/video/corpus), so smart cut encodes a head and copies the rest.
    /// </summary>
    public static void WriteTrim(string directory)
    {
        string trim = Directory.CreateDirectory(Path.Combine(directory, "trim")).FullName;
        foreach ((string clip, int packets, byte[] header) in (ReadOnlySpan<(string, int, byte[])>)[
            ("vp8/v0-keyframes.webm", 9, [0, 12, 20, 0]), ("vp9/p0-keyframes.ivf", 8, [0, 6, 0, 3]), ("av1/forward-keyframes.webm", 14, [2, 8, 16, 2]),
            ("vp8/v0-altref.ivf", 6, [1, 4, 8, 1])])
        {
            using IDemuxer demuxer = MediaFile.OpenDemuxer(Path.Combine("tests", "video", "corpus", clip));
            string format = Path.GetExtension(clip)[1..];
            MemoryStream output = new();
            output.Write(header);
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

            File.WriteAllBytes(Path.Combine(trim, $"{Path.GetFileNameWithoutExtension(clip)}-{packets}.trim"), output.ToArray());
        }
    }

    /// <summary>Subtitle seeds: the subtitle files of the corpus, and its files with subtitle tracks (tests/video/corpus/subtitles).</summary>
    public static void WriteSubtitles(string directory)
    {
        string corpus = Path.Combine("tests", "video", "corpus", "subtitles");
        string subs = Directory.CreateDirectory(Path.Combine(directory, "subs")).FullName;
        string tracks = Directory.CreateDirectory(Path.Combine(directory, "subtrack")).FullName;
        foreach (string file in Directory.GetFiles(corpus).Concat(Directory.GetFiles(Path.Combine(corpus, "tracks"), "*.vtt")))
        {
            File.Copy(file, Path.Combine(subs, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string file in Directory.GetFiles(Path.Combine(corpus, "tracks")).Where(f => !f.EndsWith(".vtt", StringComparison.Ordinal)))
        {
            File.Copy(file, Path.Combine(tracks, Path.GetFileName(file)), overwrite: true);
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

    /// <summary>
    /// AV1 seeds from the corpus (tests/video/corpus/av1): the first units of a few clips in IVF and WebM and as raw units, and the
    /// small OBU and Annex B files whole.
    /// </summary>
    public static void WriteAv1(string directory)
    {
        string av1 = Directory.CreateDirectory(Path.Combine(directory, "av1")).FullName;
        string raw = Directory.CreateDirectory(Path.Combine(directory, "av1raw")).FullName;
        string obu = Directory.CreateDirectory(Path.Combine(directory, "obu")).FullName;
        string corpus = Path.Combine("tests", "video", "corpus", "av1");
        foreach ((string clip, string format, int packets) in (ReadOnlySpan<(string, string, int)>)[
            ("tools-8bit.ivf", "ivf", 4), ("tools-10bit.ivf", "webm", 3), ("compound-masks.ivf", "ivf", 3), ("switch-frames.ivf", "ivf", 3),
            ("resize.ivf", "webm", 3), ("hdr10plus.ivf", "ivf", 2), ("yuv444.ivf", "ivf", 2), ("tiles.ivf", "ivf", 2)])
        {
            using IDemuxer demuxer = MediaFile.OpenDemuxer(Path.Combine(corpus, clip));
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
            File.WriteAllBytes(Path.Combine(av1, $"{name}.{format}"), output.ToArray());
            File.WriteAllBytes(Path.Combine(raw, $"{name}.bin"), rawOutput.ToArray());
        }

        foreach (string file in (ReadOnlySpan<string>)["tools-8bit.obu", "tools-8bit.annexb.obu", "monochrome.obu", "monochrome.annexb.obu"])
        {
            File.Copy(Path.Combine(corpus, file), Path.Combine(obu, file), overwrite: true);
            File.Copy(Path.Combine(corpus, file), Path.Combine(av1, file), overwrite: true);
        }
    }

    /// <summary>H.264 seeds: the smallest clips of tests/video/corpus/h264 whole, CAVLC and CABAC, 10-bit, 4:2:2, and one MP4.</summary>
    public static void WriteH264(string directory)
    {
        string h264 = Directory.CreateDirectory(Path.Combine(directory, "h264")).FullName;
        string corpus = Path.Combine("tests", "video", "corpus", "h264");
        foreach (string file in (ReadOnlySpan<string>)["tiny-motion.h264", "qp-high.h264", "gray.h264", "high-crop.h264", "high10.mp4", "high422-cavlc.h264", "cavlc-slices.h264"])
        {
            File.Copy(Path.Combine(corpus, file), Path.Combine(h264, file), overwrite: true);
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
        WriteAv1(directory);
        WriteH264(directory);
        WriteMp4(directory);
        WriteMp3(directory);
        WriteAc3(directory);
        WriteSubtitles(directory);
        WriteTrim(directory);
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

    /// <summary>
    /// MPEG audio seeds (V5.8): the small corpus files whole (tests/video/corpus/audio/mp3), and frames of two of them cut into
    /// chunks of uneven length for the raw decoder target.
    /// </summary>
    public static void WriteMp3(string directory)
    {
        string mp3 = Directory.CreateDirectory(Path.Combine(directory, "mp3")).FullName;
        string raw = Directory.CreateDirectory(Path.Combine(directory, "mp3raw")).FullName;
        string corpus = Path.Combine("tests", "video", "corpus", "audio", "mp3");
        foreach (string clip in (ReadOnlySpan<string>)["mp3-vbr-joint.mp3", "mp3-mpeg2-mono.mp3", "mp3-mpeg25-8k.mp3", "mp3-id3v1-noxing.mp3",
            "mp3-id3v23-cover-chapters.mp3", "mp2-crc-mono.mp2"])
        {
            File.Copy(Path.Combine(corpus, clip), Path.Combine(mp3, clip), overwrite: true);
        }

        foreach ((string clip, byte flags) in (ReadOnlySpan<(string, byte)>)[("mp3-mpeg2-mono.mp3", 0), ("mp3-vbr-joint.mp3", 3), ("mp2-crc-mono.mp2", 4)])
        {
            using IDemuxer demuxer = MediaFile.OpenDemuxer(Path.Combine(corpus, clip));
            List<byte> bytes = [];
            for (int i = 0; i < 6 && demuxer.ReadPacket() is { } packet; i++)
            {
                using (packet)
                {
                    bytes.AddRange(packet.Data.Span);
                }
            }

            MemoryStream output = new();
            output.WriteByte(flags);
            int p = 0;
            for (int k = 0; p < bytes.Count; k++)
            {
                int length = Math.Min(bytes.Count - p, 100 + (k * 337 % 500));
                output.WriteByte((byte)(length >> 8));
                output.WriteByte((byte)length);
                output.Write(bytes.GetRange(p, length).ToArray());
                p += length;
            }

            File.WriteAllBytes(Path.Combine(raw, Path.GetFileNameWithoutExtension(clip) + ".bin"), output.ToArray());
        }
    }

    /// <summary>
    /// AC-3 decoder seeds (V5.9): the first frames of three corpus clips (tests/video/corpus/audio/ac3-decode: stereo with coupling,
    /// 5.1, dual mono), cut into chunks of uneven length, with flag bytes that turn the CRC check off so mutations reach the parser.
    /// </summary>
    public static void WriteAc3(string directory)
    {
        string raw = Directory.CreateDirectory(Path.Combine(directory, "ac3raw")).FullName;
        string corpus = Path.Combine("tests", "video", "corpus", "audio", "ac3-decode");
        foreach ((string clip, byte flags) in (ReadOnlySpan<(string, byte)>)[("stereo-96k-cpl.ac3", 0), ("3-2-lfe-192k-cpl.ac3", 6), ("dual-mono-192k.ac3", 9)])
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(corpus, clip));
            int frame = Egami.Video.Audio.Codecs.Ac3.Ac3Header.FrameSize(bytes[4] >> 6, bytes[4] & 0x3F);
            bytes = bytes[..Math.Min(bytes.Length, 3 * frame)];
            MemoryStream output = new();
            output.WriteByte(flags);
            int p = 0;
            for (int k = 0; p < bytes.Length; k++)
            {
                int length = Math.Min(bytes.Length - p, 150 + (k * 337 % 700));
                output.WriteByte((byte)(length >> 8));
                output.WriteByte((byte)length);
                output.Write(bytes, p, length);
                p += length;
            }

            File.WriteAllBytes(Path.Combine(raw, Path.GetFileNameWithoutExtension(clip) + ".bin"), output.ToArray());
        }
    }

    /// <summary>
    /// MP4 seeds (V4.1): the first packets of corpus clips written by Egami's MP4 writer, two small corpus files whole, and the
    /// moov and first fragment of FFmpeg's and MP4Box's fragmented files.
    /// </summary>
    public static void WriteMp4(string directory)
    {
        string mp4 = Directory.CreateDirectory(Path.Combine(directory, "mp4")).FullName;
        string corpus = Path.Combine("tests", "video", "corpus", "mp4");
        foreach ((string clip, int packets) in (ReadOnlySpan<(string, int)>)[("mp4-vp9-opus.mp4", 4), ("mp4-metadata.mp4", 3), ("mp4-movtext.mp4", 4), ("mov-tmcd.mov", 2),
            ("mp4-editlist-trim.mp4", 6), ("mp4-av1.mp4", 3), ("mp4-hevc.mp4", 2)])
        {
            using IDemuxer demuxer = MediaFile.OpenDemuxer(Path.Combine(corpus, clip));
            MemoryStream output = new();
            using (IMuxer muxer = Mp4Format.Instance.CreateMuxer(output, demuxer.Tracks, demuxer.Info.Metadata, MediaContext.Default, leaveOpen: true))
            {
                for (int i = 0; i < packets * demuxer.Tracks.Count && demuxer.ReadPacket() is { } packet; i++)
                {
                    using (packet)
                    {
                        muxer.WritePacket(packet);
                    }
                }
            }

            File.WriteAllBytes(Path.Combine(mp4, $"{Path.GetFileNameWithoutExtension(clip)}-{packets}.mp4"), output.ToArray());
        }

        foreach (string whole in (ReadOnlySpan<string>)["m4a-aac.m4a", "mp4box-stz2.mp4"])
        {
            File.Copy(Path.Combine(corpus, whole), Path.Combine(mp4, whole), overwrite: true);
        }

        foreach (string fragmented in (ReadOnlySpan<string>)["mp4-frag.mp4", "mp4box-frag-mfra.mp4", "mp4-cmaf.mp4"])
        {
            // ftyp, moov and the first moof with its mdat.
            byte[] file = File.ReadAllBytes(Path.Combine(corpus, fragmented));
            int moof = file.AsSpan().IndexOf("moof"u8) - 4;
            int mdat = file.AsSpan(moof).IndexOf("mdat"u8) + moof - 4;
            int end = mdat + System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(mdat));
            File.WriteAllBytes(Path.Combine(mp4, $"{Path.GetFileNameWithoutExtension(fragmented)}-1.mp4"), file[..end]);
        }
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

    internal sealed class UnseekableWriter(Stream inner) : Stream
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
