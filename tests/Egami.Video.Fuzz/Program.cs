using Egami;
using Egami.Pixels;
using Egami.Video;
using Egami.Video.Codecs;
using Egami.Video.Containers;

using SharpFuzz;

// Fuzz targets. Each must read any input or fail with an EgamiException: any other exception is a crash, and AFL++ reports
// hangs. Limits are lowered so a valid but huge file cannot exhaust the fuzzer's memory. Instrumented code must not run
// before Fuzzer.OutOfProcess.Run sets up AFL++'s coverage map, so everything is created inside the fuzzed delegate.
//
//   dotnet run --project tests/Egami.Video.Fuzz -- avi                  run one input from stdin (used by AFL++)
//   dotnet run --project tests/Egami.Video.Fuzz -- avi file.avi         run one file, to reproduce a crash
//   dotnet run --project tests/Egami.Video.Fuzz -- replay avi dir       run every file in a directory and count failures
//   dotnet run --project tests/Egami.Video.Fuzz -- seeds dir            write the small seed files into dir/<target>
if (args is ["seeds", string seedDirectory])
{
    Seeds.Write(seedDirectory);
    return;
}

if (args is ["replay", string replayTarget, string directory])
{
    Action<Stream> replay = Target(replayTarget);
    Dictionary<string, int> failures = [];
    int files = 0;
    foreach (string file in Directory.GetFiles(directory))
    {
        files++;
        try
        {
            using FileStream input = File.OpenRead(file);
            replay(input);
        }
        catch (Exception e)
        {
            string key = $"{e.GetType().Name}: {System.Text.RegularExpressions.Regex.Replace(e.Message, "[0-9]+", "#")}";
            key = key[..Math.Min(key.Length, 160)];
            failures[key] = failures.GetValueOrDefault(key) + 1;
            if (failures[key] == 1)
            {
                Console.WriteLine($"{Path.GetFileName(file)}: {e}");
            }
        }
    }

    foreach ((string key, int count) in failures.OrderByDescending(f => f.Value))
    {
        Console.WriteLine($"{count,6}  {key}");
    }

    Console.WriteLine($"{files} files, {failures.Values.Sum()} failures.");
    return;
}

string target = args.Length > 0 ? args[0] : "avi";
Action<Stream> run = Target(target);
if (args.Length > 1)
{
    using FileStream file = File.OpenRead(args[1]);
    run(file);
    Console.WriteLine("No crash.");
    return;
}

Fuzzer.OutOfProcess.Run(run);

static Action<Stream> Target(string name) => name switch
{
    "avi" or "mkv" or "y4m" or "ivf" or "webm" or "mp4" or "obu" => Demux,
    "mp4mux" => RemuxMp4,
    "mjpeg" => DecodeMotionJpeg,
    "vp8" => ReadVp8,
    "vp9" => ReadVp9,
    "vp9raw" => DecodeVp9Packets,
    "wav" or "flac" or "ogg" => ReadAudio,
    "opusraw" => DecodeOpusPackets,
    "vorbisraw" => DecodeVorbisPackets,
    "av1" => ReadAv1,
    "av1raw" => DecodeAv1Packets,
    "vp8enc" => EncodeVp8,
    _ => throw new ArgumentException($"Unknown fuzz target '{name}'. Targets: avi, mkv, y4m, ivf, webm, mp4, obu, mp4mux, mjpeg, vp8, vp9, vp9raw, av1, av1raw, wav, flac, ogg, opusraw, vorbisraw."),
};

static MediaContext Context() => MediaContext.Default with
{
    Egami = EgamiContext.Default with
    {
        MaxParallelism = 1,
        Limits = SafetyLimits.Default with { MaxPixels = 4_000_000, MaxTotalBytes = 256L * 1024 * 1024, MaxMetadataBytes = 1024 * 1024 },
    },
    Limits = MediaLimits.Default with { MaxPacketBytes = 8 << 20, MaxHeaderBytes = 1 << 20, MaxIndexEntries = 1_000_000, MaxTracks = 16 },
};

// The VP8 encoder: the first bytes pick the size and options, the rest fills the frames; every frame must come back out of
// Egami's decoder, and nothing may throw, since every option it can pick is valid.
static void EncodeVp8(Stream stream)
{
    byte[] data = ReadAll(stream);
    if (data.Length < 9)
    {
        return;
    }

    int width = 1 + (data[0] % 48), height = 1 + (data[1] % 48), frames = 1 + (data[2] % 12);
    RateControlMode mode = (RateControlMode)(data[3] % 4);
    VideoEncoderOptions options = new()
    {
        Width = width,
        Height = height,
        Quality = data[4] % 101,
        Effort = data[5] % 11,
        RateControl = mode,
        Bitrate = mode is RateControlMode.Bitrate or RateControlMode.ConstantBitrate ? 1000 + (data[6] * 4000L) : null,
        Quantizer = mode == RateControlMode.Quantizer ? data[6] % 128 : null,
        LatencyMode = (data[7] & 1) != 0 ? LatencyMode.Realtime : LatencyMode.Quality,
        KeyframeInterval = (data[7] & 2) != 0 ? 1 + (data[7] >> 4) : null,
        Vp8 = new Vp8EncoderOptions { Alpha = (data[7] & 4) != 0, Partitions = 1 << ((data[7] >> 3) & 3), GoldenInterval = 4 + (data[6] % 8) },
    };
    MediaContext context = Context();
    using IVideoEncoder encoder = VideoCodecs.Vp8.CreateEncoder(options, context);
    List<EncodedPacket> packets = [];
    int at = 0;
    for (int f = 0; f < frames; f++)
    {
        YuvRaster<byte> picture = YuvRaster.Create(width, height, ChromaFormat.Yuv420, YuvColor.Bt601Limited, alpha: true);
        foreach (YuvPlane<byte>? plane in (YuvPlane<byte>?[])[picture.Y, picture.U, picture.V, picture.A])
        {
            for (int y = 0; y < plane!.Height; y++)
            {
                Span<byte> row = plane.Row(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = data[8 + (at++ % (data.Length - 8))];
                }
            }
        }

        using YuvVideoFrame frame = new(picture, new MediaTime(f, new Rational(1, 30)));
        encoder.Send(frame, new EncodeFrameOptions { ForceKeyframe = (data[(at + f) % data.Length] & 7) == 0 });
        while (encoder.TryReceive(out EncodedPacket? packet))
        {
            packets.Add(packet);
        }
    }

    encoder.Flush();
    while (encoder.TryReceive(out EncodedPacket? packet))
    {
        packets.Add(packet);
    }

    using IVideoDecoder decoder = VideoCodecs.Vp8.CreateDecoder(encoder.Track, null, context);
    int shown = 0;
    foreach (EncodedPacket packet in packets)
    {
        using (packet)
        {
            decoder.Send(packet);
            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                frame.Dispose();
                shown++;
            }
        }
    }

    decoder.Flush();
    while (decoder.TryReceive(out YuvVideoFrame? frame))
    {
        frame.Dispose();
        shown++;
    }

    if (shown != frames)
    {
        throw new InvalidOperationException($"{frames} frames went in, {shown} came out of the decoder.");
    }
}

// Any container: probe, read every packet (decoding Motion JPEG ones), then seek and read again; then the same bytes as a
// non-seekable stream.
static void Demux(Stream stream)
{
    MediaContext context = Context();
    byte[] data = ReadAll(stream);
    try
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(data), context);
        ReadAndDecode(demuxer, context, maxPackets: 200);
        demuxer.Seek(MediaTime.FromSeconds(0.5));
        ReadAndDecode(demuxer, context, maxPackets: 20);
        demuxer.Seek(MediaTime.Zero);
        ReadAndDecode(demuxer, context, maxPackets: 5);
    }
    catch (EgamiException)
    {
    }

    try
    {
        using IDemuxer streaming = MediaFile.OpenDemuxer(new ForwardOnlyStream(data), context);
        ReadAndDecode(streaming, context, maxPackets: 200);
    }
    catch (EgamiException)
    {
    }
}

// An audio file (V5.0: WAV, Wave64, RF64, FLAC): decode it through AudioReader, converting to 16-bit stereo at 22.05 kHz so the
// resampler and mixer run too, seek and decode again; then the same bytes as a non-seekable stream with the codec's own samples.
static void ReadAudio(Stream stream)
{
    MediaContext context = Context() with { Egami = Context().Egami with { Diagnostics = _ => { } } };
    byte[] data = ReadAll(stream);
    static void Drain(Egami.Video.AudioReader reader, int maxFrames)
    {
        for (int i = 0; i < maxFrames && reader.TryRead(out Egami.Video.Audio.AudioFrame? frame); i++)
        {
            frame.Dispose();
        }
    }

    try
    {
        using Egami.Video.AudioReader reader = Egami.Video.AudioReader.Open(new MemoryStream(data), new Egami.Video.AudioReaderOptions
        {
            SampleFormat = Egami.Video.Audio.SampleFormat.S16,
            SampleRate = 22050,
            Layout = Egami.Video.Audio.ChannelLayout.Stereo,
            Quality = Egami.Video.Audio.ResamplerQuality.Fast,
        }, context);
        Drain(reader, 100);
        reader.Seek(MediaTime.FromSeconds(0.25));
        Drain(reader, 10);
    }
    catch (EgamiException)
    {
    }

    try
    {
        using Egami.Video.AudioReader streaming = Egami.Video.AudioReader.Open(new ForwardOnlyStream(data), new Egami.Video.AudioReaderOptions { SampleFormat = null, Errors = DecodeErrorMode.Conceal }, context);
        Drain(streaming, 100);
    }
    catch (EgamiException)
    {
    }
}

// Raw Opus packets (V5.1): the first byte picks mono or stereo, then packets with 16-bit big-endian lengths go to the
// decoder; a zero length is a lost packet (concealment), and every third packet is also decoded as in-band FEC.
static void DecodeOpusPackets(Stream stream)
{
    byte[] data = ReadAll(stream);
    if (data.Length < 1)
    {
        return;
    }

    Egami.Video.Audio.Codecs.Opus.OpusStreamDecoder decoder = new((data[0] & 1) + 1);
    float[] pcm = new float[5760 * 2];
    int p = 1;
    int n = 0;
    while (p + 2 <= data.Length && n++ < 200)
    {
        int length = Math.Min((data[p] << 8) | data[p + 1], data.Length - p - 2);
        ReadOnlySpan<byte> packet = data.AsSpan(p + 2, length);
        p += 2 + length;
        if (length == 0)
        {
            decoder.Decode([], pcm, Math.Max(120, decoder.LastPacketDuration), false, false, out _);
            continue;
        }

        if (n % 3 == 0)
        {
            decoder.Decode(packet, pcm, 5760, true, false, out _);
        }

        decoder.Decode(packet, pcm, 5760, false, false, out _);
    }
}

// Vorbis packets, each after a 16-bit big-endian length: the first three are the headers, the rest audio packets decoded in
// turn (V5.5); a corrupt packet must leave the decoder able to take the next.
static void DecodeVorbisPackets(Stream stream)
{
    byte[] data = ReadAll(stream);
    List<byte[]> packets = [];
    int p = 0;
    while (p + 2 <= data.Length && packets.Count < 200)
    {
        int length = Math.Min((data[p] << 8) | data[p + 1], data.Length - p - 2);
        packets.Add(data.AsSpan(p + 2, length).ToArray());
        p += 2 + length;
    }

    if (packets.Count < 3)
    {
        return;
    }

    Egami.Video.Audio.Codecs.Vorbis.VorbisDecoder decoder;
    try
    {
        decoder = new(packets[..3]);
    }
    catch (EgamiException)
    {
        return;
    }

    int previous = 0;
    for (int i = 3; i < packets.Count; i++)
    {
        decoder.PacketSamples(packets[i], ref previous);
        try
        {
            int count = decoder.Decode(packets[i]);
            for (int c = 0; c < decoder.Channels; c++)
            {
                _ = decoder.GetSamples(c)[..count];
            }
        }
        catch (CorruptDataException)
        {
        }

        if (i % 50 == 0)
        {
            decoder.Reset();
        }
    }
}

// An MP4 file (V4.1): read up to 200 packets, write them to MP4 three ways (moov first in place and from a temporary file,
// moov at the end), and read each back: it must open and give the same number of packets, each with the same bytes.
static void RemuxMp4(Stream stream)
{
    MediaContext context = Context();
    byte[] data = ReadAll(stream);
    List<EncodedPacket> packets = [];
    IReadOnlyList<TrackInfo> tracks;
    MediaMetadata metadata;
    try
    {
        using IDemuxer demuxer = Mp4Format.Instance.OpenDemuxer(new MemoryStream(data), context, leaveOpen: false);
        tracks = demuxer.Tracks;
        metadata = demuxer.Info.Metadata;
        for (int i = 0; i < 200 && demuxer.ReadPacket() is { } packet; i++)
        {
            packets.Add(packet);
        }
    }
    catch (EgamiException)
    {
        packets.ForEach(p => p.Dispose());
        return;
    }

    try
    {
        foreach (Mp4Format format in new[] { Mp4Format.Instance, new Mp4Format { MoovAtEnd = true } })
        {
            foreach (bool seekable in new[] { true, false })
            {
                if (format.MoovAtEnd && !seekable)
                {
                    continue;
                }

                MemoryStream output = new();
                try
                {
                    using IMuxer muxer = format.CreateMuxer(seekable ? output : new Seeds.UnseekableWriter(output), tracks, metadata, context, leaveOpen: true);
                    foreach (EncodedPacket packet in packets)
                    {
                        muxer.WritePacket(packet);
                    }
                }
                catch (NotSupportedException)
                {
                    // A track MP4 cannot hold, such as one read without its sample description.
                    return;
                }

                using IDemuxer again = Mp4Format.Instance.OpenDemuxer(new MemoryStream(output.ToArray()), context, leaveOpen: false);
                int count = 0;
                Dictionary<int, Queue<EncodedPacket>> byTrack = packets.GroupBy(p => p.TrackNumber).ToDictionary(g => g.Key, g => new Queue<EncodedPacket>(g));
                Dictionary<int, int> numbers = tracks.Select((t, i) => (i + 1, t.Number)).ToDictionary();
                while (again.ReadPacket() is { } read)
                {
                    using (read)
                    {
                        count++;
                        EncodedPacket written = byTrack[numbers[read.TrackNumber]].Dequeue();
                        if (!read.Data.Span.SequenceEqual(written.Data.Span))
                        {
                            throw new InvalidOperationException($"Packet {count} came back with other bytes.");
                        }
                    }
                }

                if (count != packets.Count)
                {
                    throw new InvalidOperationException($"{packets.Count} packets were written, {count} read back.");
                }
            }
        }
    }
    finally
    {
        packets.ForEach(p => p.Dispose());
    }
}

static void ReadAndDecode(IDemuxer demuxer, MediaContext context, int maxPackets)
{
    Dictionary<int, IVideoDecoder> decoders = [];
    try
    {
        foreach (VideoTrackInfo track in demuxer.Tracks.OfType<VideoTrackInfo>())
        {
            if (context.FindCodec(track) is { } codec && track.Width is > 0 and <= 2048 && track.Height is > 0 and <= 2048)
            {
                decoders[track.Number] = codec.CreateDecoder(track, null, context);
            }
        }

        for (int i = 0; i < maxPackets && demuxer.ReadPacket() is { } packet; i++)
        {
            using (packet)
            {
                _ = packet.Timestamp.ToTimeSpanOrZero();
                foreach (TrackInfo track in demuxer.Tracks)
                {
                    _ = track.Tags.Count;
                }

                _ = demuxer.Info.Chapters.Sum(c => c.Start.ToTimeSpanOrZero().Ticks);
                _ = demuxer.Info.Attachments.Sum(a => a.Data.Length);
                if (demuxer.Tracks.FirstOrDefault(t => t.Number == packet.TrackNumber) is { } owner && CodecHeaders.CanDescribe(owner))
                {
                    try
                    {
                        _ = CodecHeaders.Describe(owner, packet).Sum(f => f.Value);
                    }
                    catch (EgamiException)
                    {
                    }
                }

                if (decoders.TryGetValue(packet.TrackNumber, out IVideoDecoder? decoder))
                {
                    try
                    {
                        decoder.Send(packet);
                        while (decoder.TryReceive(out YuvVideoFrame? frame))
                        {
                            frame.Dispose();
                        }
                    }
                    catch (EgamiException)
                    {
                    }
                    catch (NotSupportedException)
                    {
                        // A CMYK, YCCK or RGB JPEG has no planes.
                    }
                }
            }
        }
    }
    finally
    {
        foreach (IVideoDecoder decoder in decoders.Values)
        {
            decoder.Dispose();
        }
    }
}

// A VP8 file (IVF or WebM, alpha included) read through VideoReader in each error mode: Skip and Conceal must never throw for
// broken frames; then an exact seek.
static void ReadVp8(Stream stream)
{
    MediaContext context = Context() with { Egami = Context().Egami with { Diagnostics = _ => { } } };
    byte[] data = ReadAll(stream);
    foreach (DecodeErrorMode mode in (DecodeErrorMode[])[DecodeErrorMode.Skip, DecodeErrorMode.Conceal, DecodeErrorMode.Throw])
    {
        try
        {
            using VideoReader reader = VideoReader.Open(new MemoryStream(data), new VideoReaderOptions { Errors = mode }, context);
            if (reader.Track.Width > 2048 || reader.Track.Height > 2048)
            {
                return;
            }

            int frames = 0;
            while (frames++ < 60 && reader.ReadFrame() is { } frame)
            {
                frame.Dispose();
            }

            reader.Seek(MediaTime.FromSeconds(0.3));
            reader.ReadFrame()?.Dispose();
        }
        catch (EgamiException) when (mode == DecodeErrorMode.Throw)
        {
        }
        catch (EgamiException e) when (e is not CorruptDataException { FormatName: "VP8" })
        {
            // Broken containers, unsupported files and safety limits, in any mode; broken VP8 frames only in Throw mode.
        }
    }
}

// A VP9 file (IVF, WebM or Matroska, alpha and HDR10+ included) read through VideoReader in each error mode, as for VP8; then an
// exact seek, and the frames converted to RGB.
static void ReadVp9(Stream stream) => ReadVideo(stream, "VP9");

// An AV1 file (IVF, WebM, Matroska, low-overhead OBUs or Annex B), as for VP9.
static void ReadAv1(Stream stream) => ReadVideo(stream, "AV1");

static void ReadVideo(Stream stream, string codec)
{
    MediaContext context = Context() with { Egami = Context().Egami with { Diagnostics = _ => { } } };
    byte[] data = ReadAll(stream);
    foreach (DecodeErrorMode mode in (DecodeErrorMode[])[DecodeErrorMode.Skip, DecodeErrorMode.Conceal, DecodeErrorMode.Throw])
    {
        try
        {
            using VideoReader reader = VideoReader.Open(new MemoryStream(data), new VideoReaderOptions { Errors = mode }, context);
            if (reader.Track.Width > 2048 || reader.Track.Height > 2048)
            {
                return;
            }

            int frames = 0;
            while (frames++ < 40 && reader.ReadFrame() is { } frame)
            {
                using (frame)
                {
                    if (frames == 1)
                    {
                        using Raster<Rgba8> rgb = frame.ToRaster<Rgba8>(context.Egami);
                    }
                }
            }

            reader.Seek(MediaTime.FromSeconds(0.3));
            reader.ReadFrame()?.Dispose();
        }
        catch (EgamiException) when (mode == DecodeErrorMode.Throw)
        {
        }
        catch (EgamiException e) when (!(e is CorruptDataException corrupt && corrupt.FormatName == codec))
        {
            // Broken containers, unsupported files and safety limits, in any mode; broken frames only in Throw mode.
        }
    }
}

// VP9 packets without a container, straight into the decoder: a byte giving the CodecPrivate's length, the CodecPrivate, then
// packets of a 2-byte little-endian length and the data. Each packet is described, split and joined as a superframe, and
// decoded on one thread and on two.
static void DecodeVp9Packets(Stream stream)
{
    MediaContext context = Context();
    byte[] data = ReadAll(stream);
    if (data.Length == 0)
    {
        return;
    }

    int privateLength = Math.Min(data[0], data.Length - 1);
    byte[] codecPrivate = data.AsSpan(1, privateLength).ToArray();
    List<byte[]> packets = [];
    for (int at = 1 + privateLength; at + 2 <= data.Length && packets.Count < 30;)
    {
        int length = Math.Min(data[at] | (data[at + 1] << 8), data.Length - at - 2);
        packets.Add(data.AsSpan(at + 2, length).ToArray());
        at += 2 + length;
    }

    VideoTrackInfo track = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.Vp9, TimeBase = new Rational(1, 30), Width = 64, Height = 64, CodecPrivate = codecPrivate };
    foreach (byte[] packet in packets)
    {
        using EncodedPacket encoded = new(1, packet, MediaTime.Zero);
        try
        {
            _ = CodecHeaders.Describe(track, encoded).Sum(f => f.Value);
        }
        catch (EgamiException)
        {
        }

        try
        {
            List<ReadOnlyMemory<byte>> frames = Egami.Video.Codecs.Vp9.Vp9Superframe.Split(packet);
            if (frames.Count is >= 1 and <= 8)
            {
                _ = Egami.Video.Codecs.Vp9.Vp9Superframe.Merge(frames).Length;
            }

            _ = Egami.Video.Codecs.Vp9.Vp9Superframe.IsLoneHiddenFrame(packet);
        }
        catch (EgamiException)
        {
        }
    }

    foreach (int threads in (int[])[1, 2])
    {
        MediaContext threaded = context with { Egami = context.Egami with { MaxParallelism = threads, Diagnostics = _ => { } } };
        try
        {
            using IVideoDecoder decoder = VideoCodecs.Vp9.CreateDecoder(track, new VideoDecoderOptions { Errors = DecodeErrorMode.Conceal }, threaded);
            for (int i = 0; i < packets.Count; i++)
            {
                using EncodedPacket encoded = new(1, packets[i], new MediaTime(i, track.TimeBase)) { IsKeyframe = i == 0 };
                decoder.Send(encoded);
                while (decoder.TryReceive(out YuvVideoFrame? frame))
                {
                    frame.Dispose();
                }
            }
        }
        catch (EgamiException e) when (e is not CorruptDataException { FormatName: "VP9" })
        {
            // Unsupported subsampling and safety limits; broken frames are concealed.
        }
    }
}

// AV1 temporal units without a container, as for VP9: a byte giving the av1C's length, the av1C, then units of a 2-byte
// little-endian length and the data. Each unit is split into frames and merged back, its sequence header taken out as an av1C,
// described with and without the references, and decoded on one thread and on two.
static void DecodeAv1Packets(Stream stream)
{
    MediaContext context = Context() with { Egami = Context().Egami with { Diagnostics = _ => { } } };
    byte[] data = ReadAll(stream);
    if (data.Length == 0)
    {
        return;
    }

    int privateLength = Math.Min(data[0], data.Length - 1);
    byte[] codecPrivate = data.AsSpan(1, privateLength).ToArray();
    List<byte[]> packets = [];
    for (int at = 1 + privateLength; at + 2 <= data.Length && packets.Count < 30;)
    {
        int length = Math.Min(data[at] | (data[at + 1] << 8), data.Length - at - 2);
        packets.Add(data.AsSpan(at + 2, length).ToArray());
        at += 2 + length;
    }

    VideoTrackInfo track = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.Av1, TimeBase = new Rational(1, 30), Width = 64, Height = 64, CodecPrivate = codecPrivate };
    using (CodecHeaderReader headers = new(track, context))
    {
        foreach (byte[] packet in packets)
        {
            using EncodedPacket encoded = new(1, packet, MediaTime.Zero);
            try
            {
                _ = CodecHeaders.Describe(track, encoded).Sum(f => f.Value);
            }
            catch (EgamiException)
            {
            }

            try
            {
                _ = headers.Describe(encoded).Sum(f => f.Value);
            }
            catch (EgamiException)
            {
            }

            try
            {
                IReadOnlyList<ReadOnlyMemory<byte>> frames = Egami.Video.Codecs.Av1Packets.SplitFrames(packet);
                _ = Egami.Video.Codecs.Av1Packets.MergeFrames(frames).Length;
                _ = Egami.Video.Codecs.Av1Packets.ExtractConfig(packet)?.Length;
            }
            catch (EgamiException)
            {
            }
        }
    }

    foreach (int threads in (int[])[1, 2])
    {
        MediaContext threaded = context with { Egami = context.Egami with { MaxParallelism = threads } };
        try
        {
            using IVideoDecoder decoder = VideoCodecs.Av1.CreateDecoder(track, new VideoDecoderOptions { Errors = DecodeErrorMode.Conceal }, threaded);
            for (int i = 0; i < packets.Count; i++)
            {
                using EncodedPacket encoded = new(1, packets[i], new MediaTime(i, track.TimeBase)) { IsKeyframe = i == 0 };
                decoder.Send(encoded);
                while (decoder.TryReceive(out YuvVideoFrame? frame))
                {
                    frame.Dispose();
                }
            }
        }
        catch (EgamiException e) when (e is not CorruptDataException { FormatName: "AV1" })
        {
            // Safety limits; broken frames are concealed.
        }
    }
}

// A Motion JPEG packet: decoded at full and reduced scale, then converted and encoded back.
static void DecodeMotionJpeg(Stream stream)
{
    MediaContext context = Context();
    byte[] data = ReadAll(stream);
    VideoTrackInfo track = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.MotionJpeg, TimeBase = new Rational(1, 25), Width = 64, Height = 64 };
    foreach (int scale in (int[])[1, 4])
    {
        try
        {
            using IVideoDecoder decoder = MotionJpegCodec.Instance.CreateDecoder(track, new VideoDecoderOptions { ScaleDenominator = scale }, context);
            using EncodedPacket packet = new(1, data, MediaTime.Zero);
            decoder.Send(packet);
            decoder.Flush();
            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                using (frame)
                {
                    using Raster<Rgb8> rgb = frame.ToRaster<Rgb8>(context.Egami);
                    using IVideoEncoder encoder = MotionJpegCodec.Instance.CreateEncoder(new VideoEncoderOptions { Width = frame.Width, Height = frame.Height }, context);
                    encoder.Send(frame);
                    while (encoder.TryReceive(out EncodedPacket? encoded))
                    {
                        encoded.Dispose();
                    }
                }
            }
        }
        catch (EgamiException)
        {
        }
        catch (NotSupportedException)
        {
            // A CMYK, YCCK or RGB JPEG has no planes.
        }
    }
}

static byte[] ReadAll(Stream stream)
{
    using MemoryStream copy = new();
    stream.CopyTo(copy);
    return copy.ToArray();
}

internal static class MediaTimeExtensions
{
    /// <summary>Converts to a TimeSpan, or zero when the time is outside its range (the fuzzer reads every field).</summary>
    public static TimeSpan ToTimeSpanOrZero(this MediaTime time)
    {
        try
        {
            return time.ToTimeSpan();
        }
        catch (OverflowException)
        {
            return TimeSpan.Zero;
        }
    }
}

internal sealed class ForwardOnlyStream(byte[] data) : Stream
{
    private readonly MemoryStream _inner = new(data);

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

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
