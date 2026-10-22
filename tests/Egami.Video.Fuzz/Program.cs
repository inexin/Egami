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
    "avi" or "mkv" or "y4m" or "ivf" or "webm" => Demux,
    "mjpeg" => DecodeMotionJpeg,
    "vp8" => ReadVp8,
    "vp9" => ReadVp9,
    "vp9raw" => DecodeVp9Packets,
    _ => throw new ArgumentException($"Unknown fuzz target '{name}'. Targets: avi, mkv, y4m, ivf, webm, mjpeg, vp8, vp9, vp9raw."),
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
static void ReadVp9(Stream stream)
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
        catch (EgamiException e) when (e is not CorruptDataException { FormatName: "VP9" })
        {
            // Broken containers, unsupported files and safety limits, in any mode; broken VP9 frames only in Throw mode.
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
