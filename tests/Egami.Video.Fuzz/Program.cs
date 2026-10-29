using System.Buffers.Binary;

using Egami;
using Egami.Pixels;
using Egami.Video;
using Egami.Video.Audio;
using Egami.Video.Audio.Filters;
using Egami.Video.Codecs;
using Egami.Video.Containers;
using Egami.Video.Subtitles;

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
    "avi" or "mkv" or "y4m" or "ivf" or "webm" or "mp4" or "obu" or "ac3" => Demux,
    "mp4mux" => RemuxMp4,
    "mjpeg" => DecodeMotionJpeg,
    "vp8" => ReadVp8,
    "vp9" => ReadVp9,
    "vp9raw" => DecodeVp9Packets,
    "h264" => ReadH264,
    "wav" or "flac" or "ogg" or "aiff" or "caf" => ReadAudio,
    "opusraw" => DecodeOpusPackets,
    "vorbisraw" => DecodeVorbisPackets,
    "mp3" => ReadMp3,
    "mp3raw" => DecodeMp3Packets,
    "ac3raw" => DecodeAc3Packets,
    "alac" => DecodeAlacPackets,
    "av1" => ReadAv1,
    "av1raw" => DecodeAv1Packets,
    "vp8enc" => EncodeVp8,
    "opusenc" => EncodeOpus,
    "subs" => ReadSubtitles,
    "subtrack" => ReadSubtitleTracks,
    "vp9enc" => stream => EncodeVp9OrAv1(stream, VideoCodecs.Vp9),
    "av1enc" => stream => EncodeVp9OrAv1(stream, VideoCodecs.Av1),
    "trim" => Trim,
    "ffv1" => ReadFfv1,
    "ffv1raw" => DecodeFfv1Packets,
    "ffv1enc" => EncodeFfv1,
    "afilters" => FilterAudio,
    _ => throw new ArgumentException($"Unknown fuzz target '{name}'. Targets: avi, mkv, y4m, ivf, webm, mp4, obu, ac3, mp4mux, mjpeg, vp8, vp9, vp9raw, h264, av1, av1raw, wav, flac, ogg, aiff, caf, opusraw, vorbisraw, vp8enc, mp3, mp3raw, ac3raw, alac, opusenc, subs, subtrack, vp9enc, av1enc, trim, ffv1, ffv1raw, ffv1enc, afilters."),
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

// The VP9 encoder: the first bytes pick the size, format and options, the rest fills the frames; every frame must come back out
// of Egami's decoder at the coded depth and chroma format, and nothing may throw, since every option it can pick is valid.
// VP9 or AV1 encoding of random frames with random options (AV1 smaller, as it is slower), every packet decoded again.
static void EncodeVp9OrAv1(Stream stream, VideoCodec codec)
{
    byte[] data = ReadAll(stream);
    if (data.Length < 10)
    {
        return;
    }

    bool av1 = codec == VideoCodecs.Av1;
    int width = 1 + (data[0] % (av1 ? 48 : 80)), height = 1 + (data[1] % (av1 ? 32 : 48)), frames = 1 + (data[2] % (av1 ? 8 : 10));
    RateControlMode mode = (RateControlMode)(data[3] % 4);
    int depth = (data[8] & 3) switch { 1 => 10, 2 => 12, _ => 8 };
    ChromaFormat chroma = ((data[8] >> 2) % 3) switch { 1 => ChromaFormat.Yuv422, 2 => ChromaFormat.Yuv444, _ => ChromaFormat.Yuv420 };
    VideoEncoderOptions options = new()
    {
        Width = width,
        Height = height,
        Quality = data[4] % 101,
        Effort = data[5] % 11,
        RateControl = mode,
        Bitrate = mode is RateControlMode.Bitrate or RateControlMode.ConstantBitrate ? 1000 + (data[6] * 4000L) : null,
        Quantizer = mode == RateControlMode.Quantizer ? data[6] : null,
        LatencyMode = (data[7] & 1) != 0 ? LatencyMode.Realtime : LatencyMode.Quality,
        KeyframeInterval = (data[7] & 2) != 0 ? 1 + (data[7] >> 4) : null,
        Vp9 = new Vp9EncoderOptions
        {
            Alpha = (data[7] & 4) != 0 && depth == 8,
            AdaptiveQuantization = (data[7] & 8) != 0,
            ErrorResilient = (data[8] & 16) != 0,
            GoldenInterval = 4 + (data[6] % 8),
            TileColumns = 1 << ((data[8] >> 5) & 3),
        },
        Av1 = new Av1EncoderOptions
        {
            AlternateReference = (data[7] & 4) == 0,
            Pyramid = (data[8] & 32) == 0,
            AdaptiveQuantization = (data[7] & 8) == 0,
            ErrorResilient = (data[8] & 16) != 0,
            GoldenInterval = 4 + (data[6] % 8),
            ScreenContent = ((data[8] >> 6) & 3) switch { 1 => true, 2 => false, _ => null },
        },
    };
    MediaContext context = Context();
    using IVideoEncoder encoder = codec.CreateEncoder(options, context);
    List<EncodedPacket> packets = [];
    int at = 0;
    for (int f = 0; f < frames; f++)
    {
        YuvColor color = YuvColor.Bt601Limited;
        YuvRaster picture;
        if (depth == 8)
        {
            YuvRaster<byte> bytes = YuvRaster.Create(width, height, chroma, color, alpha: true);
            foreach (YuvPlane<byte>? plane in (YuvPlane<byte>?[])[bytes.Y, bytes.U, bytes.V, bytes.A])
            {
                for (int y = 0; y < plane!.Height; y++)
                {
                    Span<byte> row = plane.Row(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        row[x] = data[9 + (at++ % (data.Length - 9))];
                    }
                }
            }

            picture = bytes;
        }
        else
        {
            YuvRaster<ushort> words = YuvRaster.Create(width, height, chroma, depth, color);
            foreach (YuvPlane<ushort>? plane in (YuvPlane<ushort>?[])[words.Y, words.U, words.V])
            {
                for (int y = 0; y < plane!.Height; y++)
                {
                    Span<ushort> row = plane.Row(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        row[x] = (ushort)((data[9 + (at++ % (data.Length - 9))] << (depth - 8)) | (data[(at + x) % data.Length] & ((1 << (depth - 8)) - 1)));
                    }
                }
            }

            picture = words;
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

    using IVideoDecoder decoder = codec.CreateDecoder(encoder.Track, null, context);
    int shown = 0;
    foreach (EncodedPacket packet in packets)
    {
        using (packet)
        {
            decoder.Send(packet);
            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                using (frame)
                {
                    if (frame.Raster.BitDepth != depth || frame.Raster.ChromaFormat != chroma)
                    {
                        throw new InvalidOperationException($"A {depth}-bit {chroma} frame came out as {frame.Raster.BitDepth}-bit {frame.Raster.ChromaFormat}.");
                    }
                }

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

// Smart cut (V5.6): the first four bytes pick the mode, the start and end (in 1/64 s) and the output container, the rest is a
// media file. A trim may fail only as the API says: broken data, a start past the end, a stream with no keyframe, or a track
// the container cannot hold.
static void Trim(Stream stream)
{
    MediaContext context = Context();
    byte[] data = ReadAll(stream);
    if (data.Length < 5)
    {
        return;
    }

    TrimMode mode = (TrimMode)(data[0] % 3);
    MediaTime start = new(data[1], new Rational(1, 64));
    MediaTime? end = data[2] == 0 ? null : start + new MediaTime(data[2], new Rational(1, 64));
    string format = (data[3] % 4) switch { 0 => "mkv", 1 => "webm", 2 => "mp4", _ => "ivf" };
    try
    {
        using MemoryStream output = new();
        TrimResult result = MediaFile.Trim(new MemoryStream(data, 4, data.Length - 4), output, format, start, end, mode, context: context, leaveOpen: true);
        if (result.Frames < result.ReencodedFrames || result.Start < MediaTime.Zero)
        {
            throw new InvalidOperationException($"The trim reports {result.Frames} frames, {result.ReencodedFrames} encoded again, from {result.Start}.");
        }
    }
    catch (EgamiException)
    {
    }
    catch (ArgumentOutOfRangeException e) when (e.ParamName == "start")
    {
    }
    catch (InvalidDataException)
    {
    }
    catch (NotSupportedException)
    {
    }
}

// An audio file (V5.0: WAV, Wave64, RF64, FLAC; V5.9: AIFF, AIFF-C, G.711, CAF): decode it through AudioReader, converting to 16-bit stereo at 22.05 kHz so the
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

// An MPEG audio file (V5.8): read as any audio file (exact seeks), then with Mp3Format.FastSeek, seeking by the Xing table.
static void ReadMp3(Stream stream)
{
    byte[] data = ReadAll(stream);
    ReadAudio(new MemoryStream(data));
    MediaContext context = Context();
    try
    {
        using IDemuxer demuxer = new Mp3Format { FastSeek = true }.OpenDemuxer(new MemoryStream(data), context, leaveOpen: false);
        ReadAndDecode(demuxer, context, maxPackets: 50);
        demuxer.Seek(MediaTime.FromSeconds(0.2));
        ReadAndDecode(demuxer, context, maxPackets: 20);
    }
    catch (EgamiException)
    {
    }
}

// Raw MPEG audio bytes (V5.8): the first byte picks mono or stereo and the error mode, then chunks with 16-bit big-endian
// lengths go to the decoder as packets, so frames are cut anywhere, as AVI chunks cut them; a zero length is a lost packet.
static void DecodeMp3Packets(Stream stream)
{
    byte[] data = ReadAll(stream);
    if (data.Length < 1)
    {
        return;
    }

    MediaContext context = Context() with { Egami = Context().Egami with { Diagnostics = _ => { } } };
    Egami.Video.AudioTrackInfo track = new() { Number = 1, Kind = TrackKind.Audio, CodecId = CodecIds.Mp3, TimeBase = new Rational(1, 44100), SampleRate = 44100, Channels = (data[0] & 1) + 1 };
    Egami.Video.Audio.AudioDecoderOptions options = new() { Errors = (data[0] & 2) != 0 ? DecodeErrorMode.Skip : DecodeErrorMode.Conceal, KeepPadding = (data[0] & 4) != 0 };
    using Egami.Video.Audio.IAudioDecoder decoder = Egami.Video.Audio.Codecs.Mp3Codec.Instance.CreateDecoder(track, options, context);
    int p = 1;
    int n = 0;
    while (p + 2 <= data.Length && n < 200)
    {
        int length = Math.Min((data[p] << 8) | data[p + 1], data.Length - p - 2);
        if (length == 0)
        {
            decoder.SendLoss(new MediaTime(1152, track.TimeBase));
        }
        else
        {
            using EncodedPacket packet = EncodedPacket.Rent(1, length, new MediaTime(n * 1152L, track.TimeBase));
            data.AsSpan(p + 2, length).CopyTo(packet.GetWritableData().Span);
            packet.Duration = n % 7 == 6 ? new MediaTime(500, track.TimeBase) : null;
            decoder.Send(packet);
        }

        p += 2 + length;
        n++;
        while (decoder.TryReceive(out Egami.Video.Audio.AudioFrame? frame))
        {
            frame.Dispose();
        }
    }

    decoder.Flush();
    while (decoder.TryReceive(out Egami.Video.Audio.AudioFrame? frame))
    {
        frame.Dispose();
    }
}

// Raw AC-3 bytes (V5.9): the first byte's bits turn the CRC check on (1), pick Skip over Conceal (2), a stereo downmix (4) and half
// the dynamic range control (8); then chunks with 16-bit big-endian lengths go to the decoder as packets, so frames are cut
// anywhere; a zero length is a lost packet. Damaged frames must never throw in Skip or Conceal.
static void DecodeAc3Packets(Stream stream)
{
    byte[] data = ReadAll(stream);
    if (data.Length < 1)
    {
        return;
    }

    MediaContext context = Context() with { Egami = Context().Egami with { Diagnostics = _ => { } } };
    Egami.Video.AudioTrackInfo track = new() { Number = 1, Kind = TrackKind.Audio, CodecId = CodecIds.Ac3, TimeBase = new Rational(1, 48000), SampleRate = 48000, Channels = 6 };
    Egami.Video.Audio.AudioDecoderOptions options = new()
    {
        Errors = (data[0] & 2) != 0 ? DecodeErrorMode.Skip : DecodeErrorMode.Conceal,
        VerifyChecksums = (data[0] & 1) != 0,
        Ac3 = new Egami.Video.Audio.Codecs.Ac3DecoderOptions
        {
            Downmix = (data[0] & 4) != 0 ? Egami.Video.Audio.ChannelLayout.Stereo : null,
            DynamicRange = (data[0] & 8) != 0 ? 0.5f : 1,
        },
    };
    using Egami.Video.Audio.IAudioDecoder decoder = Egami.Video.Audio.Codecs.Ac3Codec.Instance.CreateDecoder(track, options, context);
    int p = 1;
    int n = 0;
    while (p + 2 <= data.Length && n < 200)
    {
        int length = Math.Min((data[p] << 8) | data[p + 1], data.Length - p - 2);
        if (length == 0)
        {
            decoder.SendLoss(new MediaTime(1536, track.TimeBase));
        }
        else
        {
            using EncodedPacket packet = EncodedPacket.Rent(1, length, new MediaTime(n * 1536L, track.TimeBase));
            data.AsSpan(p + 2, length).CopyTo(packet.GetWritableData().Span);
            decoder.Send(packet);
        }

        p += 2 + length;
        n++;
        while (decoder.TryReceive(out Egami.Video.Audio.AudioFrame? frame))
        {
            frame.Dispose();
        }
    }

    decoder.Flush();
    while (decoder.TryReceive(out Egami.Video.Audio.AudioFrame? frame))
    {
        frame.Dispose();
    }
}

// ALAC packets (V5.9 stage C), in the layout of tools/video/make-alac-references.py: the 24-byte ALACSpecificConfig, then each
// packet as a little-endian 32-bit length and its bytes. A config the decoder refuses is fine; a packet must never throw.
static void DecodeAlacPackets(Stream stream)
{
    byte[] data = ReadAll(stream);
    if (data.Length < 24)
    {
        return;
    }

    MediaContext context = Context() with { Egami = Context().Egami with { Diagnostics = _ => { } } };
    Egami.Video.AudioTrackInfo track = new()
    {
        Number = 1,
        Kind = TrackKind.Audio,
        CodecId = CodecIds.Alac,
        CodecPrivate = data.AsMemory(0, 24),
        TimeBase = new Rational(1, 44100),
        SampleRate = 44100,
        Channels = Math.Clamp((int)data[9], 1, 8),
    };
    Egami.Video.Audio.AudioDecoderOptions options = new() { Errors = (data[10] & 1) != 0 ? DecodeErrorMode.Skip : DecodeErrorMode.Conceal };
    Egami.Video.Audio.IAudioDecoder decoder;
    try
    {
        decoder = Egami.Video.Audio.Codecs.AlacCodec.Instance.CreateDecoder(track, options, context);
    }
    catch (UnsupportedFormatException)
    {
        return;
    }

    using (decoder)
    {
        int p = 24;
        int n = 0;
        while (p + 4 <= data.Length && n < 200)
        {
            int length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p)), (uint)(data.Length - p - 4));
            if (length == 0)
            {
                decoder.SendLoss(new MediaTime(4096, track.TimeBase));
            }
            else
            {
                using EncodedPacket packet = EncodedPacket.Rent(1, length, new MediaTime(n * 4096L, track.TimeBase));
                data.AsSpan(p + 4, length).CopyTo(packet.GetWritableData().Span);
                packet.Duration = n % 5 == 4 ? new MediaTime(1000, track.TimeBase) : null;
                decoder.Send(packet);
            }

            p += 4 + length;
            n++;
            while (decoder.TryReceive(out Egami.Video.Audio.AudioFrame? frame))
            {
                frame.Dispose();
            }
        }
    }
}

// Opus encoding (V5.7): the first bytes pick the channels (1 to 8), the rate (the five Opus rates, or 44.1 kHz through the
// resampler) and every encoder setting; the rest is 16-bit audio, encoded in odd-sized frames, then decoded again. Every
// packet must parse and the decoder must give back exactly the samples that went in.
static void EncodeOpus(Stream stream)
{
    byte[] data = ReadAll(stream);
    if (data.Length < 8)
    {
        return;
    }

    int channels = 1 + (data[0] % 8);
    int rate = (data[1] % 6) switch { 0 => 8000, 1 => 12000, 2 => 16000, 3 => 24000, 4 => 44100, _ => 48000 };
    double[] durations = [2.5, 5, 10, 20, 40, 60, 80, 100, 120];
    Egami.Video.Audio.Codecs.OpusEncoderOptions opus = new()
    {
        Bitrate = (data[2] & 0x80) != 0 ? null : 500 + (data[2] * 2000 * channels),
        RateMode = (Egami.Video.Audio.Codecs.OpusRateMode)(data[3] % 3),
        Application = (Egami.Video.Audio.Codecs.OpusApplication)((data[3] >> 2) % 3),
        FrameDuration = TimeSpan.FromMilliseconds(durations[data[4] % durations.Length]),
        Effort = data[5] % 11,
        Bandwidth = (data[6] & 7) is var b && b < 5 ? (Egami.Video.Audio.Codecs.OpusBandwidth)(1101 + b) : null,
        InbandFec = (data[6] & 8) != 0,
        Dtx = (data[6] & 16) != 0,
        ExpectedLoss = data[7] % 101,
    };
    Egami.Video.Audio.ChannelLayout layout = Egami.Video.Audio.ChannelLayout.ForChannelCount(channels);
    MediaContext context = Context();
    using Egami.Video.Audio.IAudioEncoder encoder = Egami.Video.Audio.AudioCodecs.Opus.CreateEncoder(
        new Egami.Video.Audio.AudioEncoderOptions { SampleRate = rate, Layout = layout, Opus = opus }, context);
    int samples = Math.Min((data.Length - 8) / 2 / channels, 48000);
    List<EncodedPacket> packets = [];
    int at = 0;
    while (at < samples)
    {
        int n = Math.Min(samples - at, 1 + (data[8 + (at % (data.Length - 8))] * 7));
        using Egami.Video.Audio.AudioFrame frame = new(Egami.Video.Audio.SampleFormat.S16, layout, rate, n, new MediaTime(at, new Rational(1, rate)), context);
        data.AsSpan(8 + (at * 2 * channels), n * 2 * channels).CopyTo(frame.GetBytes(0));
        encoder.Send(frame);
        at += n;
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

    using Egami.Video.Audio.IAudioDecoder decoder = Egami.Video.Audio.AudioCodecs.CreateDecoder(encoder.Track, null, context);
    long decoded = 0;
    foreach (EncodedPacket packet in packets)
    {
        using (packet)
        {
            if (Egami.Video.Audio.Codecs.Opus.OpusPacket.SampleCount(packet.Data.Span, 48000) <= 0)
            {
                throw new InvalidOperationException($"The encoder made a packet that does not parse ({packet.Data.Length} bytes).");
            }

            decoder.Send(packet);
            while (decoder.TryReceive(out Egami.Video.Audio.AudioFrame? frame))
            {
                // The decoder starts at minus the pre-skip; AudioReader drops that part, so count only what follows 0.
                long start = frame.Timestamp.Rescale(new Rational(1, 48000)).Ticks;
                decoded += Math.Max(0, Math.Min(frame.SampleCount, start + frame.SampleCount));
                frame.Dispose();
            }
        }
    }

    long expected = rate == 44100 ? -1 : samples * 48000L / rate;
    if (expected >= 0 && decoded != expected)
    {
        throw new InvalidOperationException($"{samples} samples at {rate} Hz went in, {decoded} at 48 kHz came out.");
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

// A raw H.264 stream, MP4 or Matroska file with Egami.Video.H264 registered, as for VP9; then decoded again on two threads.
static void ReadH264(Stream stream)
{
    byte[] data = ReadAll(stream);
    ReadVideo(new MemoryStream(data), "H.264", Context().WithH264());
    MediaContext threaded = Context().WithH264() with { Egami = Context().Egami with { MaxParallelism = 2 } };
    ReadVideo(new MemoryStream(data), "H.264", threaded);
}

static void ReadVideo(Stream stream, string codec, MediaContext? baseContext = null)
{
    baseContext ??= Context();
    MediaContext context = baseContext with { Egami = baseContext.Egami with { Diagnostics = _ => { } } };
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

// An FFV1 file (Matroska or AVI) read through VideoReader with Skip, Conceal and Throw, as for VP9; then checked with
// Ffv1Codec.Verify and every packet's headers described. Only a broken configuration record or container may fail outside
// Throw mode: damaged frames must be skipped or concealed.
static void ReadFfv1(Stream stream)
{
    byte[] data = ReadAll(stream);
    MediaContext context = Context() with { Egami = Context().Egami with { Diagnostics = _ => { } } };
    foreach (DecodeErrorMode mode in (DecodeErrorMode[])[DecodeErrorMode.Skip, DecodeErrorMode.Conceal, DecodeErrorMode.Throw])
    {
        VideoReader reader;
        try
        {
            reader = VideoReader.Open(new MemoryStream(data), new VideoReaderOptions { Errors = mode }, context);
        }
        catch (EgamiException)
        {
            return;
        }

        using (reader)
        {
            if (reader.Track.Width > 2048 || reader.Track.Height > 2048)
            {
                return;
            }

            try
            {
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
            }
            catch (EgamiException) when (mode == DecodeErrorMode.Throw)
            {
            }
            catch (EgamiException e) when (!(e is CorruptDataException corrupt && corrupt.FormatName == "FFV1"))
            {
            }
        }
    }

    try
    {
        _ = Ffv1Codec.Verify(new MemoryStream(data), context).Errors.Count;
    }
    catch (EgamiException)
    {
    }

    try
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(data), context);
        TrackInfo? track = demuxer.Tracks.FirstOrDefault(t => Ffv1Codec.Instance.CanDecode(t));
        for (int i = 0; track is not null && i < 20 && demuxer.ReadPacket() is { } packet; i++)
        {
            using (packet)
            {
                if (packet.TrackNumber == track.Number)
                {
                    try
                    {
                        _ = CodecHeaders.Describe(track, packet).Sum(f => f.Value);
                    }
                    catch (EgamiException)
                    {
                    }
                }
            }
        }
    }
    catch (EgamiException)
    {
    }
}

// FFV1 frames without a container, straight into the decoder: a byte each for the width and height, a 2-byte little-endian
// length and the configuration record (empty for versions 0 and 1), then frames of a 3-byte little-endian length and the data.
// Decoded with Skip and with Conceal on one thread and on two, which must give the same frames; a damaged frame must never
// throw outside Throw mode.
static void DecodeFfv1Packets(Stream stream)
{
    byte[] data = ReadAll(stream);
    if (data.Length < 4)
    {
        return;
    }

    int width = 1 + data[0], height = 1 + data[1];
    int recordLength = Math.Min(data[2] | (data[3] << 8), data.Length - 4);
    byte[] record = data.AsSpan(4, recordLength).ToArray();
    List<byte[]> packets = [];
    for (int at = 4 + recordLength; at + 3 <= data.Length && packets.Count < 20;)
    {
        int length = Math.Min(data[at] | (data[at + 1] << 8) | (data[at + 2] << 16), data.Length - at - 3);
        packets.Add(data.AsSpan(at + 3, length).ToArray());
        at += 3 + length;
    }

    VideoTrackInfo track = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.Ffv1, TimeBase = new Rational(1, 25), Width = width, Height = height, CodecPrivate = record };
    foreach (DecodeErrorMode mode in (DecodeErrorMode[])[DecodeErrorMode.Skip, DecodeErrorMode.Conceal])
    {
        List<byte[]>? first = null;
        foreach (int threads in (int[])[1, 2])
        {
            MediaContext context = Context() with { Egami = Context().Egami with { MaxParallelism = threads, Diagnostics = _ => { } } };
            IVideoDecoder decoder;
            try
            {
                decoder = VideoCodecs.Ffv1.CreateDecoder(track, new VideoDecoderOptions { Errors = mode }, context);
            }
            catch (EgamiException)
            {
                return;
            }

            List<byte[]> frames = [];
            using (decoder)
            {
                for (int i = 0; i < packets.Count; i++)
                {
                    using EncodedPacket packet = new(1, packets[i], new MediaTime(i, track.TimeBase));
                    try
                    {
                        decoder.Send(packet);
                    }
                    catch (EgamiException e) when (e is not CorruptDataException { FormatName: "FFV1" })
                    {
                        // An unsupported layout or a safety limit.
                        return;
                    }

                    while (decoder.TryReceive(out YuvVideoFrame? frame))
                    {
                        using (frame)
                        {
                            frames.Add(Bytes(frame.Raster));
                        }
                    }
                }
            }

            if (first is null)
            {
                first = frames;
            }
            else if (first.Count != frames.Count || first.Zip(frames).Any(p => !p.First.AsSpan().SequenceEqual(p.Second)))
            {
                throw new InvalidOperationException("FFV1 frames differ between one thread and two.");
            }
        }
    }

    static byte[] Bytes(YuvRaster raster)
    {
        MemoryStream bytes = new();
        if (raster is YuvRaster<byte> b)
        {
            foreach (YuvPlane<byte>? plane in (YuvPlane<byte>?[])[b.Y, b.U, b.V, b.A])
            {
                for (int y = 0; plane is not null && y < plane.Height; y++)
                {
                    bytes.Write(plane.Row(y));
                }
            }
        }
        else if (raster is YuvRaster<ushort> w)
        {
            foreach (YuvPlane<ushort>? plane in (YuvPlane<ushort>?[])[w.Y, w.U, w.V, w.A])
            {
                for (int y = 0; plane is not null && y < plane.Height; y++)
                {
                    bytes.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(plane.Row(y)));
                }
            }
        }

        return bytes.ToArray();
    }
}

// The FFV1 encoder: the first bytes pick the size, layout, depth, coder, context model, slices, CRCs and keyframe spacing;
// the rest fills the frames. Every frame must come back out of the decoder unchanged, and nothing may throw, since every
// setting it can pick is valid.
static void EncodeFfv1(Stream stream)
{
    byte[] data = ReadAll(stream);
    if (data.Length < 8)
    {
        return;
    }

    int width = 1 + (data[0] % 64), height = 1 + (data[1] % 48), frames = 1 + (data[2] % 4);
    ChromaFormat chroma = (data[3] % 6) switch
    {
        1 => ChromaFormat.Yuv422,
        2 => ChromaFormat.Yuv444,
        3 => ChromaFormat.Yuv400,
        4 => ChromaFormat.Yuv411,
        5 => ChromaFormat.Yuv410,
        _ => ChromaFormat.Yuv420,
    };
    bool rgb = (data[3] & 64) != 0;
    if (rgb)
    {
        chroma = ChromaFormat.Yuv444;
    }

    int depth = ((data[4] >> 2) & 3) switch { 1 => 10, 2 => 12, 3 => 16, _ => 8 };
    int bits = depth == 8 ? 8 : depth - ((data[4] >> 4) & 1);
    Ffv1Coder coder = depth == 8 && (data[4] & 3) == 2 ? Ffv1Coder.GolombRice : (data[4] & 1) == 1 ? Ffv1Coder.RangeDefaultTable : Ffv1Coder.RangeCustomTable;
    bool alpha = (data[3] & 128) != 0;
    VideoEncoderOptions options = new()
    {
        Width = width,
        Height = height,
        KeyframeInterval = 1 + (data[5] % 3),
        Ffv1 = new Ffv1EncoderOptions
        {
            Coder = coder,
            ContextModel = (data[5] & 8) != 0 ? Ffv1ContextModel.Large : Ffv1ContextModel.Small,
            Slices = 1 + (data[6] % 12),
            SliceCrc = (data[5] & 16) == 0,
            BitDepth = bits,
        },
    };
    MediaContext context = Context();
    YuvColor color = rgb ? new YuvColor(YuvMatrix.Identity, YuvRange.Full, ChromaSiting.Center) : YuvColor.Bt601Limited;
    List<YuvRaster> sources = [];
    int at = 0;
    try
    {
        for (int f = 0; f < frames; f++)
        {
            if (depth == 8)
            {
                YuvRaster<byte> bytes = YuvRaster.Create(width, height, chroma, color, alpha);
                foreach (YuvPlane<byte>? plane in (YuvPlane<byte>?[])[bytes.Y, bytes.U, bytes.V, bytes.A])
                {
                    for (int y = 0; plane is not null && y < plane.Height; y++)
                    {
                        Span<byte> row = plane.Row(y);
                        for (int x = 0; x < row.Length; x++)
                        {
                            row[x] = data[7 + (at++ % (data.Length - 7))];
                        }
                    }
                }

                sources.Add(bytes);
            }
            else
            {
                YuvRaster<ushort> words = YuvRaster.Create(width, height, chroma, depth, color, alpha);
                int shift = depth - bits;
                foreach (YuvPlane<ushort>? plane in (YuvPlane<ushort>?[])[words.Y, words.U, words.V, words.A])
                {
                    for (int y = 0; plane is not null && y < plane.Height; y++)
                    {
                        Span<ushort> row = plane.Row(y);
                        for (int x = 0; x < row.Length; x++)
                        {
                            int v = data[7 + (at++ % (data.Length - 7))] | (data[7 + (at++ % (data.Length - 7))] << 8);
                            row[x] = (ushort)((v & ((1 << bits) - 1)) << shift);
                        }
                    }
                }

                sources.Add(words);
            }
        }

        using IVideoEncoder encoder = VideoCodecs.Ffv1.CreateEncoder(options, context);
        List<EncodedPacket> packets = [];
        for (int f = 0; f < frames; f++)
        {
            // The frame would own the planes; they are disposed at the end instead, after the comparison.
            encoder.Send(new YuvVideoFrame(sources[f], new MediaTime(f, new Rational(1, 30))));
            while (encoder.TryReceive(out EncodedPacket? packet))
            {
                packets.Add(packet);
            }
        }

        using IVideoDecoder check = VideoCodecs.Ffv1.CreateDecoder(encoder.Track, new VideoDecoderOptions { Errors = DecodeErrorMode.Throw }, context);
        for (int f = 0; f < packets.Count; f++)
        {
            using (packets[f])
            {
                check.Send(packets[f]);
            }

            if (!check.TryReceive(out YuvVideoFrame? back))
            {
                throw new InvalidOperationException($"FFV1 frame {f} did not decode.");
            }

            using (back)
            {
                if (!Same(sources[f], back.Raster))
                {
                    throw new InvalidOperationException($"FFV1 frame {f} came back different.");
                }
            }
        }
    }
    finally
    {
        foreach (YuvRaster source in sources)
        {
            source.Dispose();
        }
    }

    static bool Same(YuvRaster a, YuvRaster b)
    {
        if (a.ChromaFormat != b.ChromaFormat || a.BitDepth != b.BitDepth || a.HasAlpha != b.HasAlpha)
        {
            return false;
        }

        if (a is YuvRaster<byte> ab && b is YuvRaster<byte> bb)
        {
            return Planes(ab.Y, bb.Y) && Planes(ab.U, bb.U) && Planes(ab.V, bb.V) && Planes(ab.A, bb.A);
        }

        var aw = (YuvRaster<ushort>)a;
        var bw = (YuvRaster<ushort>)b;
        return Planes(aw.Y, bw.Y) && Planes(aw.U, bw.U) && Planes(aw.V, bw.V) && Planes(aw.A, bw.A);
    }

    static bool Planes<T>(YuvPlane<T>? a, YuvPlane<T>? b)
        where T : unmanaged, IEquatable<T>
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        for (int y = 0; y < a.Height; y++)
        {
            if (!a.Row(y).SequenceEqual(b.Row(y)))
            {
                return false;
            }
        }

        return true;
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

// A subtitle file (V1.2): read leniently, which only fails when the format is not recognised; a file read without repairs or an
// encoding guess must save back to the same bytes, and retimed must save to a file with as many cues. Then converted to every
// format and read back, and read strictly.
static void ReadSubtitles(Stream stream)
{
    List<EgamiDiagnostic> reported = [];
    MediaContext context = Context() with { Egami = Context().Egami with { Diagnostics = reported.Add } };
    byte[] data = ReadAll(stream);
    SubtitleDocument document;
    try
    {
        document = SubtitleDocument.Load(data, context: context);
    }
    catch (UnsupportedFormatException)
    {
        return;
    }

    bool clean = !reported.Any(d => d.Id is "Subtitle.Repaired" or "Subtitle.Encoding");
    if (clean && document.Format != SubtitleFormat.Ssa)
    {
        using MemoryStream saved = new();
        document.Save(saved, context: context);
        if (!saved.ToArray().AsSpan().SequenceEqual(data))
        {
            throw new InvalidOperationException("An unchanged document saved to other bytes than it was read from.");
        }
    }

    try
    {
        SubtitleDocument.Load(data, new SubtitleReaderOptions { Strict = true }, context);
    }
    catch (CorruptDataException)
    {
    }

    SubtitleDocument retimed = document.Clone();
    retimed.Shift(MediaTime.FromSeconds(1.5));
    retimed.Scale(new Rational(1001, 1000));
    retimed.Clip(MediaTime.FromSeconds(1), MediaTime.FromSeconds(3600));
    retimed.FixOverlaps();
    retimed.Sort();
    foreach (SubtitleFormat format in (SubtitleFormat[])[SubtitleFormat.Srt, SubtitleFormat.WebVtt, SubtitleFormat.Ass])
    {
        foreach (SubtitleDocument source in (SubtitleDocument[])[document, retimed])
        {
            string text = source.ToText(format, context: context);
            SubtitleDocument back = SubtitleDocument.Parse(text, format, context: context);
            if (clean && format == source.Format && back.Cues.Count != source.Cues.Count)
            {
                throw new InvalidOperationException($"{format} written and read again has {back.Cues.Count} cues, not {source.Cues.Count}.");
            }
        }
    }
}

// A container's text subtitle tracks (V1.2): each read into a document, then written as a track in Matroska, WebM and MP4 and
// read back from the file.
static void ReadSubtitleTracks(Stream stream)
{
    MediaContext context = Context() with { Egami = Context().Egami with { Diagnostics = _ => { } } };
    byte[] data = ReadAll(stream);
    List<SubtitleDocument> documents = [];
    try
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(data), context);
        foreach (TrackInfo track in demuxer.Tracks.Where(t => SubtitleDocument.IsTextCodec(t.CodecId)))
        {
            demuxer.Seek(MediaTime.Zero);
            try
            {
                documents.Add(SubtitleDocument.ReadTrack(demuxer, track, context: context));
            }
            catch (CorruptDataException)
            {
            }
        }
    }
    catch (EgamiException)
    {
    }

    foreach (SubtitleDocument document in documents.Take(2))
    {
        foreach (string container in (string[])["mkv", "webm", "mp4"])
        {
            SubtitleTrackWriter writer = new(document, 1, SubtitleTrackWriter.CodecIdFor(container, document.Format), context);
            MemoryStream file = new();
            using (IMuxer muxer = MediaFile.CreateMuxer(file, container, [writer.Track], leaveOpen: true, context: context))
            {
                foreach (EncodedPacket packet in writer.Packets)
                {
                    using (packet)
                    {
                        muxer.WritePacket(packet);
                    }
                }

                muxer.Finish();
            }

            file.Position = 0;
            using IDemuxer again = MediaFile.OpenDemuxer(file, context);
            SubtitleDocument.ReadTrack(again, again.Tracks[0], context: context);
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

// V9.1's audio filters (stage D, task 19): the first bytes pick the rate, the channels, a chain of up to six filters with
// parameters inside their documented ranges, and the frame sizes; the rest is 16-bit audio, sent in uneven frames with a
// Reset now and then. Nothing may throw, and every sample out must be finite.
static void FilterAudio(Stream stream)
{
    byte[] data = ReadAll(stream);
    if (data.Length < 24)
    {
        return;
    }

    int at = 0;
    byte Next() => data[at++ % 24];
    double Unit() => Next() / 255.0;
    int rate = (Next() % 5) switch { 0 => 8000, 1 => 16000, 2 => 44100, 3 => 48000, _ => 96000 };
    ChannelLayout layout = (Next() % 3) switch { 0 => ChannelLayout.Mono, 1 => ChannelLayout.Stereo, _ => ChannelLayout.Surround51 };
    MediaTime Time(double most) => MediaTime.FromSeconds(Unit() * most);
    double Frequency() => 20 + (Unit() * ((rate / 2.0 * 0.95) - 20));
    BiquadKind Kind() => (BiquadKind)(Next() % 8);
    MediaContext context = Context();
    using AudioFilterChain chain = new(rate, layout, context);
    int count = 1 + (Next() % 6);
    for (int i = 0; i < count; i++)
    {
        chain.Add((Next() % 18) switch
        {
            0 => new Gain((Unit() * 60) - 40),
            1 => new AudioFade(Next() % 2 == 0 ? FadeDirection.In : FadeDirection.Out, Time(0.5), Time(0.5), (FadeCurve)(Next() % 5)),
            2 => new Pan((Unit() * 2) - 1) { Balance = (Unit() * 2) - 1 },
            3 => new Delay(Time(0.2)),
            4 => new Tempo(0.25 + (Unit() * 3.75), Next() % 2 == 0 ? TempoPreset.Music : TempoPreset.Speech),
            5 => new Speed(0.25 + (Unit() * 3.75)),
            6 => new Biquad(Kind(), Frequency(), 0.1 + (Unit() * 20), (Unit() * 48) - 24),
            7 => Biquad.FromBandwidth(Kind(), Frequency(), 0.05 + (Unit() * 4), (Unit() * 48) - 24),
            8 => new Butterworth(Next() % 2 == 0 ? BiquadKind.LowPass : BiquadKind.HighPass, Frequency(), 2 + (Next() % 7)),
            9 => new ParametricEqualizer(new EqualizerBand(Kind(), Frequency(), (Unit() * 48) - 24, 0.1 + (Unit() * 10)), new EqualizerBand(Kind(), Frequency(), (Unit() * 48) - 24)),
            10 => new Compressor { Threshold = -Unit() * 120, Ratio = 1 + (Unit() * 99), Knee = Unit() * 24, Attack = Time(1), Release = Time(1), Makeup = Unit() * 48, Detection = (LevelDetection)(Next() % 2) },
            11 => new Gate { Threshold = -Unit() * 120, Ratio = 1 + (Unit() * 99), Range = -Unit() * 120, Knee = Unit() * 24, Attack = Time(1), Release = Time(1) },
            12 => new Limiter { Ceiling = -Unit() * 60, Release = MediaTime.FromSeconds(0.001 + Unit()), TruePeak = Next() % 2 == 0 },
            13 => LoudnessNormalizer.Dynamic(new LoudnessTarget(-70 + (Unit() * 65), -Unit() * 9, 1 + (Unit() * 49))),
            14 => new SilenceRemover(Next() % 2 == 0 ? SilenceRemoval.Trim : SilenceRemoval.Shorten) { Threshold = -Unit() * 120, KeepGap = MediaTime.FromSeconds(0.1 + (Unit() * 0.4)), MaxGap = MediaTime.FromSeconds(0.5 + Unit()) },
            15 => new Trim(Time(0.3), Next() % 2 == 0 ? null : MediaTime.FromSeconds(0.3) + Time(1)),
            16 => new Pad(Time(0.3)),
            _ => new Retime(Time(1)),
        });
    }

    int channels = layout.ChannelCount, sizes = Next();
    long sample = 0;
    int resetAt = Next();
    void Check()
    {
        while (chain.TryReceive(out AudioFrame? output))
        {
            using (output)
            {
                for (int c = 0; c < output.ChannelCount; c++)
                {
                    foreach (float value in output.GetSpan<float>(c))
                    {
                        if (!float.IsFinite(value))
                        {
                            throw new InvalidOperationException($"A filter gave {value}.");
                        }
                    }
                }
            }
        }
    }

    int position = 24, frame = 0;
    while (position + 2 <= data.Length)
    {
        int n = Math.Min(1 + ((sizes * (frame + 7)) % 1500), (data.Length - position) / (2 * channels));
        if (n <= 0)
        {
            break;
        }

        using AudioFrame input = new(SampleFormat.S16, layout, rate, n, new MediaTime(sample, new Rational(1, rate)), context);
        data.AsSpan(position, n * 2 * channels).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(input.GetSpan<short>(0)));
        position += n * 2 * channels;
        sample += n;
        chain.Send(input);
        Check();
        if (++frame == 1 + (resetAt % 40))
        {
            chain.Reset();
            sample = 0;
        }
    }

    chain.Flush();
    Check();
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
