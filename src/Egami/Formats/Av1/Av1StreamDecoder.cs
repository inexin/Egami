using Egami.Colors;
using Egami.Pixels;

namespace Egami.Formats.Av1;

/// <summary>
/// Decodes an AV1 video stream (AV1 Bitstream and Decoding Process 1.0.0 with errata 1) a temporal unit at a time, as WebM, Matroska,
/// MP4 and IVF files carry it: key, inter, intra-only and switch frames with every inter tool of the Main, High and Professional
/// profiles, 8 to 12 bits and 4:0:0 to 4:4:4, film grain, scaled references and scalable streams. The frames equal libaom's and
/// dav1d's sample for sample. Containers are not read here: Egami.Video reads them and uses this decoder, and any other source of
/// AV1 temporal units can too.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Send"/> takes one temporal unit: low-overhead OBUs (section 5), each with its size field, as containers store them.
/// It can show no frame (a hidden frame, kept as a reference), one, or with <see cref="Av1DecoderOptions.OutputAllLayers"/> one per
/// spatial layer; <see cref="TryReceiveFrame"/> then copies each out in order. A frame shown with show_existing_frame comes out
/// again without decoding.
/// </para>
/// <para>
/// The decoder keeps at most eight reference frames and the frame being decoded, decodes a frame's tiles and its loop filters'
/// rows on up to <see cref="EgamiContext.MaxParallelism"/> threads, and checks each frame's size against the context's
/// <see cref="EgamiContext.Limits"/>. An instance is not thread-safe.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Av1.cs" region="av1-stream"/>
/// </example>
public sealed class Av1StreamDecoder : IDisposable
{
    private readonly Av1Stream _stream;
    private readonly bool _filmGrain;
    private readonly Queue<(Av1Frame Frame, bool Existing)> _frames = new();
    private byte[] _data = [];
    private bool _disposed;

    /// <summary>Starts a stream.</summary>
    /// <param name="options">The operating point, layers and film grain; <see cref="Av1DecoderOptions.Default"/> when null.</param>
    /// <param name="context">The threads and limits to use; <see cref="EgamiContext.Default"/> when null.</param>
    /// <exception cref="ArgumentOutOfRangeException">The options' operating point is not 0 to 31.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Av1.cs" region="av1-stream"/>
    /// </example>
    public Av1StreamDecoder(Av1DecoderOptions? options = null, EgamiContext? context = null)
    {
        options ??= Av1DecoderOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegative(options.OperatingPoint, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.OperatingPoint, 31, nameof(options));
        EgamiContext c = context ?? EgamiContext.Default;
        _filmGrain = options.ApplyFilmGrain;
        _stream = new Av1Stream("AV1")
        {
            OperatingPoint = options.OperatingPoint,
            OutputAllLayers = options.OutputAllLayers,
            Parallelism = c.MaxParallelism,
            Limits = c.Limits,
        };
    }

    /// <summary>Gets the information of the next frame <see cref="TryReceiveFrame"/> gives, to size its raster, or null when there is none.</summary>
    public Av1FrameInfo? NextFrame => _frames.TryPeek(out (Av1Frame Frame, bool Existing) next) ? Info(next.Frame, next.Existing) : null;

    /// <summary>
    /// Gets the fields of the sequence and frame headers of the last temporal unit <see cref="Send"/> took, in order, under the
    /// specification's syntax element and variable names; a list field carries its index (<c>loop_filter_ref_deltas[2]</c>).
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, long>> LastHeaders => _stream.LastHeaders;

    /// <summary>
    /// Decodes one temporal unit and updates the references. Its shown frames wait for <see cref="TryReceiveFrame"/>; frames not yet
    /// received when the next unit is sent are dropped.
    /// </summary>
    /// <param name="temporalUnit">The temporal unit's OBUs; a temporal delimiter at its start is allowed but not needed.</param>
    /// <exception cref="CorruptDataException">The data is broken, or refers to a frame the decoder does not have (the stream did not start at a key frame).</exception>
    /// <exception cref="LimitExceededException">A frame's size breaks the context's safety limits.</exception>
    public void Send(ReadOnlySpan<byte> temporalUnit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_data.Length < temporalUnit.Length)
        {
            _data = new byte[Math.Max(temporalUnit.Length, _data.Length * 2)];
        }

        temporalUnit.CopyTo(_data);
        _frames.Clear();
        foreach ((Av1Frame Frame, bool Existing) shown in _stream.DecodeUnit(_data, 0, temporalUnit.Length))
        {
            _frames.Enqueue(shown);
        }
    }

    /// <summary>
    /// Copies the next shown frame of the last temporal unit into <paramref name="destination"/>, film grain applied unless the
    /// options turn it off. The copy is the caller's: the decoder's references do not share it.
    /// </summary>
    /// <param name="destination">
    /// A raster of the frame's size (<see cref="NextFrame"/>) and chroma format: 8-bit for an 8-bit stream, else 16-bit of the
    /// stream's bit depth. A monochrome frame needs a 4:0:0 raster. Its color is left as it is.
    /// </param>
    /// <param name="info">What the frame was.</param>
    /// <returns>True when a frame was copied; false when the unit has no frame left.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> does not match the frame.</exception>
    public bool TryReceiveFrame(YuvRaster destination, out Av1FrameInfo info)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_frames.TryPeek(out (Av1Frame Frame, bool Existing) next))
        {
            info = default;
            return false;
        }

        Av1Frame frame = next.Frame;
        info = Info(frame, next.Existing);
        if (destination.Width != info.Width || destination.Height != info.Height || destination.ChromaFormat != info.Chroma || destination.BitDepth != info.BitDepth)
        {
            throw new ArgumentException($"The destination must be {info.BitDepth}-bit {info.Chroma} at {info.Width}×{info.Height}; it is "
                + $"{destination.BitDepth}-bit {destination.ChromaFormat} at {destination.Width}×{destination.Height}.", nameof(destination));
        }

        _frames.Dequeue();
        ushort[][] planes = _filmGrain ? Av1FilmGrainSynthesis.Apply(frame) : frame.Planes!;
        int ssx = frame.Sequence.SubsamplingX, ssy = frame.Sequence.SubsamplingY;
        int cw = (info.Width + ssx) >> ssx, ch = (info.Height + ssy) >> ssy;
        if (destination is YuvRaster<byte> bytes)
        {
            Copy(planes[0], frame.Strides[0], bytes.Y, info.Width, info.Height);
            if (planes.Length > 1)
            {
                Copy(planes[1], frame.Strides[1], bytes.U!, cw, ch);
                Copy(planes[2], frame.Strides[2], bytes.V!, cw, ch);
            }
        }
        else if (destination is YuvRaster<ushort> words)
        {
            Copy(planes[0], frame.Strides[0], words.Y, info.Width, info.Height);
            if (planes.Length > 1)
            {
                Copy(planes[1], frame.Strides[1], words.U!, cw, ch);
                Copy(planes[2], frame.Strides[2], words.V!, cw, ch);
            }
        }
        else
        {
            throw new ArgumentException("The destination must have byte or ushort samples.", nameof(destination));
        }

        return true;
    }

    /// <summary>Drops the references, the frames not yet received and the sequence header; the next unit must start a sequence with a key frame.</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _frames.Clear();
        _stream.Reset();
    }

    /// <summary>Drops the references.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _frames.Clear();
            _stream.Reset();
        }
    }

    private static Av1FrameInfo Info(Av1Frame frame, bool existing)
    {
        Av1FrameHeader h = frame.Header;
        Av1SequenceHeader s = frame.Sequence;
        Av1FrameKind kind = (Av1FrameKind)h.FrameType;
        return new Av1FrameInfo(kind, kind == Av1FrameKind.Key && !existing, h.UpscaledWidth, h.FrameHeight,
            h.RenderWidth, h.RenderHeight, s.BitDepth, s.Chroma, frame.TemporalId, frame.SpatialId,
            new ColorCodePoints((byte)s.ColorPrimaries, (byte)s.TransferCharacteristics, (byte)s.MatrixCoefficients, s.FullRange));
    }

    private static void Copy(ushort[] plane, int stride, YuvPlane<byte> to, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<ushort> source = plane.AsSpan(y * stride, width);
            Span<byte> row = to.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = (byte)source[x];
            }
        }
    }

    private static void Copy(ushort[] plane, int stride, YuvPlane<ushort> to, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            plane.AsSpan(y * stride, width).CopyTo(to.Row(y));
        }
    }
}

/// <summary>How an <see cref="Av1StreamDecoder"/> decodes.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Av1.cs" region="av1-stream"/>
/// </example>
public sealed record Av1DecoderOptions
{
    /// <summary>Gets the default options: film grain applied, operating point 0, the highest spatial layer only.</summary>
    public static Av1DecoderOptions Default { get; } = new();

    /// <summary>Gets a value indicating whether the stream's film grain is added to the frames that come out. Defaults to true.</summary>
    public bool ApplyFilmGrain { get; init; } = true;

    /// <summary>Gets the operating point of a scalable stream whose layers decode, 0 to 31; a point past the stream's last takes its last. Defaults to 0.</summary>
    public int OperatingPoint { get; init; }

    /// <summary>
    /// Gets a value indicating whether every spatial layer of a temporal unit comes out, not only the last (the highest).
    /// Defaults to false, as players show.
    /// </summary>
    public bool OutputAllLayers { get; init; }
}

/// <summary>The type of an AV1 frame (frame_type).</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Av1.cs" region="av1-stream"/>
/// </example>
public enum Av1FrameKind
{
    /// <summary>A key frame: decodes on its own; a shown one resets every reference.</summary>
    Key = 0,

    /// <summary>An inter frame, predicted from reference frames.</summary>
    Inter = 1,

    /// <summary>An intra-only frame: decodes on its own without resetting the references.</summary>
    IntraOnly = 2,

    /// <summary>A switch frame: an inter frame that refreshes every reference, where a stream may switch resolution.</summary>
    Switch = 3,
}

/// <summary>What an AV1 frame was, as <see cref="Av1StreamDecoder.TryReceiveFrame"/> gives it.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Av1.cs" region="av1-stream"/>
/// </example>
/// <param name="Kind">The frame type.</param>
/// <param name="IsShownKeyFrame">Whether the frame is a key frame being shown: a point a player can start from.</param>
/// <param name="Width">The frame width (the upscaled width when superres is on).</param>
/// <param name="Height">The frame height.</param>
/// <param name="RenderWidth">The width the frame is meant to be shown at.</param>
/// <param name="RenderHeight">The height the frame is meant to be shown at.</param>
/// <param name="BitDepth">The bits per sample: 8, 10 or 12.</param>
/// <param name="Chroma">The chroma format.</param>
/// <param name="TemporalId">The temporal layer of the frame's headers.</param>
/// <param name="SpatialId">The spatial layer of the frame's headers.</param>
/// <param name="Color">The sequence header's color primaries, transfer characteristics, matrix and range.</param>
public readonly record struct Av1FrameInfo(Av1FrameKind Kind, bool IsShownKeyFrame, int Width, int Height, int RenderWidth, int RenderHeight,
    int BitDepth, ChromaFormat Chroma, int TemporalId, int SpatialId, ColorCodePoints Color);
