using System.Globalization;
using System.Text;

using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Codecs.Raw;

namespace Egami.Video.Containers.Y4m;

/// <summary>
/// Reads YUV4MPEG2: a one-line text header (size, frame rate, interlacing, pixel aspect, color space and X comments), then
/// frames, each a "FRAME" line and the raw planes (little-endian 16-bit samples above 8 bits).
/// </summary>
internal sealed class Y4mDemuxer : IDemuxer
{
    private const int MaxHeaderLine = 4096;

    private readonly MediaInput _input;
    private readonly long _frameSize;
    private readonly long _firstFrame;
    private readonly List<long> _framePositions = [];
    private readonly VideoTrackInfo _track;
    private long _nextFrame;

    public Y4mDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        _input = new MediaInput(stream, leaveOpen, Y4mFormat.Instance.Name);
        try
        {
            byte[] header = ReadLine("header");
            _firstFrame = _input.Position;
            (_track, RawLayout layout) = ParseHeader(header, context);
            _frameSize = layout.FrameSize(_track.Width, _track.Height);
            if (_frameSize > context.Limits.MaxPacketBytes)
            {
                throw new LimitExceededException("MaxPacketBytes", context.Limits.MaxPacketBytes, _frameSize);
            }

            Tracks = [_track];
            Info = new MediaInfo { FormatName = Y4mFormat.Instance.Name, Tracks = Tracks };
        }
        catch
        {
            _input.Dispose();
            throw;
        }
    }

    public MediaInfo Info { get; }

    public IReadOnlyList<TrackInfo> Tracks { get; }

    public bool CanSeek => _input.CanSeek;

    public EncodedPacket? ReadPacket()
    {
        long position = _input.Position;
        if (_input.PeekByte() < 0)
        {
            return null;
        }

        byte[] line = ReadLine("FRAME");
        if (!line.AsSpan().StartsWith("FRAME"u8))
        {
            throw _input.Corrupt(position, "A frame does not start with FRAME.");
        }

        if (_nextFrame == _framePositions.Count)
        {
            _framePositions.Add(position);
        }

        EncodedPacket packet = EncodedPacket.Rent(_track.Number, (int)_frameSize, new MediaTime(_nextFrame, _track.TimeBase));
        if (_input.Read(packet.GetWritableData().Span) < _frameSize)
        {
            packet.Dispose();
            return null;
        }

        packet.IsKeyframe = true;
        packet.Duration = new MediaTime(1, _track.TimeBase);
        _nextFrame++;
        return packet;
    }

    public void Seek(MediaTime time, int? trackNumber = null)
    {
        if (!CanSeek)
        {
            throw new NotSupportedException("The stream cannot seek.");
        }

        MediaTime inBase = time.Rescale(_track.TimeBase);
        long frame = Math.Max(0, new MediaTime(inBase.Ticks, _track.TimeBase) > time ? inBase.Ticks - 1 : inBase.Ticks);

        // Frame lines may carry parameters, so positions are found by walking the FRAME lines once.
        if (frame >= _framePositions.Count)
        {
            long resume = _framePositions.Count == 0 ? _firstFrame : _framePositions[^1];
            _input.Seek(resume);
            _nextFrame = _framePositions.Count == 0 ? 0 : _framePositions.Count - 1;
            while (_framePositions.Count <= frame)
            {
                long position = _input.Position;
                if (_input.PeekByte() < 0)
                {
                    break;
                }

                ReadLine("FRAME");
                if (_nextFrame == _framePositions.Count)
                {
                    _framePositions.Add(position);
                }

                if (!_input.Skip(_frameSize))
                {
                    _framePositions.RemoveAt(_framePositions.Count - 1);
                    break;
                }

                _nextFrame++;
            }
        }

        frame = Math.Min(frame, Math.Max(0, _framePositions.Count - 1));
        _nextFrame = frame;
        _input.Seek(_framePositions.Count == 0 ? _firstFrame : _framePositions[(int)frame]);
    }

    public void Dispose() => _input.Dispose();

    private byte[] ReadLine(string what)
    {
        List<byte> line = [];
        while (true)
        {
            int b = _input.PeekByte();
            if (b < 0)
            {
                throw _input.Corrupt($"The data ends inside the {what} line.");
            }

            _input.ReadByte();
            if (b == '\n')
            {
                return [.. line];
            }

            if (line.Count == MaxHeaderLine)
            {
                throw _input.Corrupt($"The {what} line is longer than {MaxHeaderLine} bytes.");
            }

            line.Add((byte)b);
        }
    }

    private (VideoTrackInfo Track, RawLayout Layout) ParseHeader(byte[] header, MediaContext context)
    {
        string text = Encoding.ASCII.GetString(header);
        string[] fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 0 || fields[0] != "YUV4MPEG2")
        {
            throw _input.Corrupt(0, "The file does not start with YUV4MPEG2.");
        }

        int width = 0, height = 0;
        Rational rate = new(25, 1), aspect = new(1, 1);
        FieldOrder order = FieldOrder.Progressive;
        string space = "420jpeg";
        bool? fullRange = null;
        foreach (string field in fields.Skip(1))
        {
            string value = field[1..];
            switch (field[0])
            {
                case 'W': width = ParseInt(value); break;
                case 'H': height = ParseInt(value); break;
                case 'F': rate = ParseRatio(value) is { Numerator: > 0 } r ? r : rate; break;
                case 'A': aspect = ParseRatio(value) is { Numerator: > 0 } a ? a : aspect; break;
                case 'C': space = value; break;
                case 'I':
                    order = value switch { "t" => FieldOrder.TopFieldFirst, "b" => FieldOrder.BottomFieldFirst, _ => FieldOrder.Progressive };
                    break;
                case 'X' when value.StartsWith("COLORRANGE=", StringComparison.Ordinal):
                    fullRange = value.EndsWith("FULL", StringComparison.Ordinal);
                    break;
            }
        }

        SafetyLimits limits = context.Egami.Limits;
        if (width <= 0 || height <= 0)
        {
            throw _input.Corrupt(0, "The header has no width or height.");
        }

        if (width > limits.MaxWidth || height > limits.MaxHeight || (long)width * height > limits.MaxPixels)
        {
            throw new LimitExceededException("MaxPixels", limits.MaxPixels, (long)width * height);
        }

        (ChromaFormat chroma, int depth, bool alpha, ChromaSiting siting) = space switch
        {
            "420jpeg" or "420" => (ChromaFormat.Yuv420, 8, false, ChromaSiting.Center),
            "420paldv" => (ChromaFormat.Yuv420, 8, false, ChromaSiting.TopLeft),
            "420mpeg2" => (ChromaFormat.Yuv420, 8, false, ChromaSiting.Left),
            "422" => (ChromaFormat.Yuv422, 8, false, ChromaSiting.Left),
            "444" => (ChromaFormat.Yuv444, 8, false, ChromaSiting.Center),
            "444alpha" => (ChromaFormat.Yuv444, 8, true, ChromaSiting.Center),
            "mono" => (ChromaFormat.Yuv400, 8, false, ChromaSiting.Center),
            "420p10" => (ChromaFormat.Yuv420, 10, false, ChromaSiting.Left),
            "422p10" => (ChromaFormat.Yuv422, 10, false, ChromaSiting.Left),
            "444p10" => (ChromaFormat.Yuv444, 10, false, ChromaSiting.Center),
            "420p12" => (ChromaFormat.Yuv420, 12, false, ChromaSiting.Left),
            "422p12" => (ChromaFormat.Yuv422, 12, false, ChromaSiting.Left),
            "444p12" => (ChromaFormat.Yuv444, 12, false, ChromaSiting.Center),
            "mono10" => (ChromaFormat.Yuv400, 10, false, ChromaSiting.Center),
            "mono12" => (ChromaFormat.Yuv400, 12, false, ChromaSiting.Center),
            _ => throw new UnsupportedFormatException($"YUV4MPEG2 color space '{space}' is not supported."),
        };

        RawLayout layout = RawLayout.Planar(chroma, depth, alpha);
        VideoTrackInfo track = new()
        {
            Number = 1,
            Kind = TrackKind.Video,
            CodecId = CodecIds.Uncompressed,
            CodecPrivate = header,
            TimeBase = rate.Reciprocal(),
            Width = width,
            Height = height,
            FrameRate = rate,
            PixelAspectRatio = aspect,
            FieldOrder = order,
            ChromaFormat = chroma,
            BitDepth = depth,
            HasAlpha = alpha,
            ChromaSiting = siting,
            Color = fullRange is { } full ? new ColorCodePoints(2, 2, 2, full) : null,
        };
        return (track, layout);
    }

    private static int ParseInt(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0;

    private static Rational ParseRatio(string text)
    {
        int colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return default;
        }

        int n = ParseInt(text[..colon]), d = ParseInt(text[(colon + 1)..]);
        return d > 0 ? new Rational(n, d) : default;
    }
}
