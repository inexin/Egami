using System.Globalization;
using System.Text;

using Egami.Pixels;
using Egami.Video.Codecs.Raw;

namespace Egami.Video.Containers.Y4m;

/// <summary>
/// Writes YUV4MPEG2 from one uncompressed planar video track. A header read from a YUV4MPEG2 file (kept in the track's
/// <see cref="TrackInfo.CodecPrivate"/>) is written back as it was, so reading and writing a file gives the same bytes.
/// </summary>
internal sealed class Y4mMuxer : IMuxer
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly int _inputNumber;
    private readonly long _frameSize;
    private bool _finished;

    public Y4mMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks is not [VideoTrackInfo track] || RawLayout.ForTrack(track) is not { Shape: RawLayout.Kind.Planar } layout)
        {
            throw new NotSupportedException("YUV4MPEG2 holds one planar uncompressed video track (I420, Y42B, 444P, Y800 or planar YUV at 8 to 12 bits).");
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        _inputNumber = track.Number;
        _frameSize = layout.FrameSize(track.Width, track.Height);
        Tracks = [track with { Number = 1 }];
        byte[] header = track.CodecPrivate.Span.StartsWith("YUV4MPEG2"u8) ? track.CodecPrivate.ToArray() : Header(track, layout);
        _stream.Write(header);
        _stream.WriteByte((byte)'\n');
    }

    public IReadOnlyList<TrackInfo> Tracks { get; }

    public void WritePacket(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (_finished)
        {
            throw new InvalidOperationException("The muxer was finished.");
        }

        if (packet.TrackNumber != _inputNumber)
        {
            throw new ArgumentException($"Track {packet.TrackNumber} was not given to the muxer.", nameof(packet));
        }

        if (packet.Data.Length != _frameSize)
        {
            throw new ArgumentException($"A frame of this track has {_frameSize} bytes, not {packet.Data.Length}.", nameof(packet));
        }

        _stream.Write("FRAME\n"u8);
        _stream.Write(packet.Data.Span);
    }

    public void Finish()
    {
        _finished = true;
        _stream.Flush();
    }

    public void Dispose()
    {
        Finish();
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }

    private static byte[] Header(VideoTrackInfo track, RawLayout layout)
    {
        Rational rate = track.FrameRate ?? track.TimeBase.Reciprocal();
        string space = (layout.Chroma, layout.BitDepth, layout.Alpha) switch
        {
            (ChromaFormat.Yuv420, 8, false) => track.ChromaSiting switch
            {
                ChromaSiting.TopLeft => "420paldv",
                ChromaSiting.Left => "420mpeg2",
                _ => "420jpeg",
            },
            (ChromaFormat.Yuv422, 8, false) => "422",
            (ChromaFormat.Yuv444, 8, false) => "444",
            (ChromaFormat.Yuv444, 8, true) => "444alpha",
            (ChromaFormat.Yuv400, 8, false) => "mono",
            (ChromaFormat.Yuv400, var d, false) => $"mono{d}",
            (var c, var d, false) when d is 10 or 12 => $"{c switch { ChromaFormat.Yuv420 => "420", ChromaFormat.Yuv422 => "422", _ => "444" }}p{d}",
            _ => throw new NotSupportedException("YUV4MPEG2 has no color space for this layout."),
        };
        string interlace = track.FieldOrder switch
        {
            FieldOrder.TopFieldFirst => "t",
            FieldOrder.BottomFieldFirst => "b",
            _ => "p",
        };
        StringBuilder header = new(string.Create(CultureInfo.InvariantCulture, $"YUV4MPEG2 W{track.Width} H{track.Height} F{rate.Numerator}:{rate.Denominator} I{interlace} A{track.PixelAspectRatio.Numerator}:{track.PixelAspectRatio.Denominator} C{space}"));
        if (track.Color is { } color)
        {
            header.Append(color.FullRange ? " XCOLORRANGE=FULL" : " XCOLORRANGE=LIMITED");
        }

        return Encoding.ASCII.GetBytes(header.ToString());
    }
}
