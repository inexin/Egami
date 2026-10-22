using Egami.Video.Codecs.Raw;

namespace Egami.Video.Containers;

/// <summary>
/// Headerless raw video ("rawvideo"): frames of one uncompressed layout back to back, as FFmpeg's <c>-f rawvideo</c> reads and
/// writes them. Nothing in the file says the size or layout, so the caller gives them as a track.
/// </summary>
/// <example>
/// <code language="csharp">
/// VideoTrackInfo track = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.Uncompressed, FourCC = "I420",
///     TimeBase = new Rational(1, 25), FrameRate = new Rational(25, 1), Width = 1920, Height = 1080 };
/// using IDemuxer demuxer = RawVideo.OpenDemuxer(File.OpenRead("frames.yuv"), track);
/// </code>
/// </example>
public static class RawVideo
{
    /// <summary>Opens headerless frames in the layout <paramref name="track"/> describes.</summary>
    /// <param name="stream">The frames.</param>
    /// <param name="track">The layout (<see cref="VideoTrackInfo.FourCC"/>, or chroma format, bit depth and alpha), size and frame rate.</param>
    /// <param name="leaveOpen">Whether disposing the demuxer leaves the stream open.</param>
    /// <returns>The demuxer; every frame is a keyframe packet.</returns>
    /// <exception cref="NotSupportedException">The track is not an uncompressed layout this library knows.</exception>
    public static IDemuxer OpenDemuxer(Stream stream, VideoTrackInfo track, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(track);
        RawLayout layout = RawLayout.ForTrack(track) ?? throw new NotSupportedException("The track is not an uncompressed layout this library knows.");
        return new Demuxer(stream, track, layout.FrameSize(track.Width, track.Height), leaveOpen);
    }

    /// <summary>Creates a muxer that writes the packets of one uncompressed video track back to back.</summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="track">The track.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <returns>The muxer.</returns>
    public static IMuxer CreateMuxer(Stream stream, VideoTrackInfo track, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(track);
        return new Muxer(stream, track, leaveOpen);
    }

    private sealed class Demuxer(Stream stream, VideoTrackInfo track, long frameSize, bool leaveOpen) : IDemuxer
    {
        private readonly MediaInput _input = new(stream, leaveOpen, "rawvideo");
        private readonly long _start = stream.CanSeek ? stream.Position : 0;
        private long _next;

        public MediaInfo Info => new() { FormatName = "rawvideo", Tracks = Tracks };

        public IReadOnlyList<TrackInfo> Tracks { get; } = [track];

        public bool CanSeek => _input.CanSeek;

        public EncodedPacket? ReadPacket()
        {
            if (frameSize is <= 0 or > int.MaxValue)
            {
                return null;
            }

            EncodedPacket packet = EncodedPacket.Rent(track.Number, (int)frameSize, new MediaTime(_next, track.TimeBase));
            if (_input.Read(packet.GetWritableData().Span) < frameSize)
            {
                packet.Dispose();
                return null;
            }

            packet.IsKeyframe = true;
            packet.Duration = new MediaTime(1, track.TimeBase);
            _next++;
            return packet;
        }

        public void Seek(MediaTime time, int? trackNumber = null)
        {
            if (!CanSeek)
            {
                throw new NotSupportedException("The stream cannot seek.");
            }

            MediaTime inBase = time.Rescale(track.TimeBase);
            long frame = Math.Max(0, new MediaTime(inBase.Ticks, track.TimeBase) > time ? inBase.Ticks - 1 : inBase.Ticks);
            frame = Math.Min(frame, Math.Max(0, ((_input.Length - _start) / frameSize) - 1));
            _next = frame;
            _input.Seek(_start + (frame * frameSize));
        }

        public void Dispose() => _input.Dispose();
    }

    private sealed class Muxer(Stream stream, VideoTrackInfo track, bool leaveOpen) : IMuxer
    {
        private bool _finished;

        public IReadOnlyList<TrackInfo> Tracks { get; } = [track];

        public void WritePacket(EncodedPacket packet)
        {
            ArgumentNullException.ThrowIfNull(packet);
            if (_finished)
            {
                throw new InvalidOperationException("The muxer was finished.");
            }

            if (packet.TrackNumber != track.Number)
            {
                throw new ArgumentException($"Track {packet.TrackNumber} was not given to the muxer.", nameof(packet));
            }

            stream.Write(packet.Data.Span);
        }

        public void Finish()
        {
            _finished = true;
            stream.Flush();
        }

        public void Dispose()
        {
            Finish();
            if (!leaveOpen)
            {
                stream.Dispose();
            }
        }
    }
}
