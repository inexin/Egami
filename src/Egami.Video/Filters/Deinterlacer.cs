using System.Diagnostics.CodeAnalysis;

using Egami.Colors;
using Egami.Pixels;

namespace Egami.Video.Filters;

/// <summary>
/// Turns interlaced frames into progressive ones. Each output frame keeps one field's lines and makes the other field's:
/// <see cref="DeinterlaceMode.Adaptive"/> keeps the woven lines where the picture holds still and guesses along edges where it
/// moves, using the fields before and after; <see cref="DeinterlaceMode.Bob"/> guesses from the kept field alone. Every plane is
/// done on its own lines, so a 4:2:0 field keeps its own chroma rows; 8- to 16-bit samples, any chroma format and alpha work.
/// The filter holds one frame back to look at the next.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoFilters.cs" region="deinterlace"/>
/// </example>
public sealed class Deinterlacer : IVideoFilter
{
    private static readonly MediaTime FallbackStep = new(1, new Rational(1, 25));

    private readonly DeinterlacerOptions _options;
    private readonly FieldOrder _trackOrder;
    private readonly bool _trackFlags;
    private readonly MediaTime? _trackStep;
    private readonly MediaContext _context;
    private readonly Queue<YuvVideoFrame> _ready = new();
    private readonly Stack<Held> _spare = new();
    private Held? _previous;
    private Held? _current;
    private MediaTime? _step;
    private bool _disposed;

    /// <summary>Creates a deinterlacer.</summary>
    /// <param name="options">How it works, or <see langword="null"/> for <see cref="DeinterlacerOptions.Default"/>.</param>
    /// <param name="context">The context output frames are allocated from, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public Deinterlacer(DeinterlacerOptions? options = null, MediaContext? context = null)
    {
        _options = options ?? DeinterlacerOptions.Default;
        _options.Validate();
        _context = context ?? MediaContext.Default;
    }

    /// <summary>
    /// Creates a deinterlacer for a track: frames that do not give their field order take the track's
    /// <see cref="VideoTrackInfo.FieldOrder"/>, and the track's frame rate times frames that give no duration. Only the H.264
    /// decoder flags each frame; frames of other codecs count as flagged when the container calls the track interlaced.
    /// </summary>
    /// <param name="track">The track the frames come from.</param>
    /// <param name="options">How it works, or <see langword="null"/> for <see cref="DeinterlacerOptions.Default"/>.</param>
    /// <param name="context">The context output frames are allocated from, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="track"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public Deinterlacer(VideoTrackInfo track, DeinterlacerOptions? options = null, MediaContext? context = null)
        : this(options, context)
    {
        ArgumentNullException.ThrowIfNull(track);
        _trackOrder = track.FieldOrder;
        _trackFlags = track.FieldOrder != FieldOrder.Progressive && track.CodecId != CodecIds.Avc;
        if (track.FrameRate is { Numerator: > 0 } rate)
        {
            _trackStep = new MediaTime(1, rate.Reciprocal());
        }
    }

    /// <summary>Gets the options the deinterlacer runs with.</summary>
    public DeinterlacerOptions Options => _options;

    /// <summary>Gets one frame's time, as the deinterlacer holds one frame back; zero before the first frame.</summary>
    public MediaTime Latency => _step ?? _trackStep ?? MediaTime.Zero;

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The deinterlacer is disposed.</exception>
    public void Send(YuvVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current is not null && !_current.Fits(frame.Raster))
        {
            // A new size or format starts a new run: finish the frame held without a next one.
            Emit(_previous, _current, null);
            Recycle(ref _previous);
            Recycle(ref _current);
        }

        Held next = Take(frame);
        if (_current is not null)
        {
            if (frame.Timestamp > _current.Timestamp)
            {
                _step = frame.Timestamp - _current.Timestamp;
            }

            Emit(_previous, _current, next);
        }

        Recycle(ref _previous);
        _previous = _current;
        _current = next;
    }

    /// <inheritdoc/>
    public bool TryReceive([NotNullWhen(true)] out YuvVideoFrame? frame) => _ready.TryDequeue(out frame);

    /// <inheritdoc/>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_current is not null)
        {
            Emit(_previous, _current, null);
        }

        Recycle(ref _previous);
        Recycle(ref _current);
    }

    /// <inheritdoc/>
    public void Reset()
    {
        while (_ready.TryDequeue(out YuvVideoFrame? frame))
        {
            frame.Dispose();
        }

        Recycle(ref _previous);
        Recycle(ref _current);
        _step = null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Reset();
        while (_spare.TryPop(out Held? held))
        {
            held.Raster.Dispose();
        }
    }

    /// <summary>The field order of a frame, by the options, the frame, the track and then top field first.</summary>
    private FieldOrder OrderOf(Held frame) => _options.FieldOrder
        ?? (frame.FieldOrder != FieldOrder.Progressive ? frame.FieldOrder
        : _trackOrder != FieldOrder.Progressive ? _trackOrder
        : FieldOrder.TopFieldFirst);

    private void Emit(Held? previous, Held current, Held? next)
    {
        bool deinterlace = _options.Frames == DeinterlaceFrames.All || _trackFlags || current.FieldOrder != FieldOrder.Progressive;
        bool fields = _options.Rate == DeinterlaceRate.Field;
        MediaTime? duration = current.Duration;
        if (!deinterlace)
        {
            if (!fields)
            {
                _ready.Enqueue(Copy(current, current.Timestamp, duration, current.FieldOrder, current.RepeatFields));
                return;
            }

            MediaTime half = Half(duration ?? Step(current, next));
            _ready.Enqueue(Copy(current, current.Timestamp, half, current.FieldOrder, 0));
            _ready.Enqueue(Copy(current, Later(current.Timestamp, half), half, current.FieldOrder, 0));
            return;
        }

        int firstParity = OrderOf(current) == FieldOrder.BottomFieldFirst ? 1 : 0;
        if (!fields)
        {
            _ready.Enqueue(Make(previous, current, next, firstParity, second: false, current.Timestamp, duration, current.RepeatFields));
            return;
        }

        MediaTime step = Half(duration ?? Step(current, next));
        _ready.Enqueue(Make(previous, current, next, firstParity, second: false, current.Timestamp, step, 0));
        _ready.Enqueue(Make(previous, current, next, firstParity, second: true, Later(current.Timestamp, step), step, 0));
    }

    /// <summary>How long a frame lasts when it does not say: up to the next frame, else the last step seen, else the track's rate.</summary>
    private MediaTime Step(Held current, Held? next) =>
        next is not null && next.Timestamp > current.Timestamp ? next.Timestamp - current.Timestamp : _step ?? _trackStep ?? FallbackStep;

    private static MediaTime Half(MediaTime time) => (time.Ticks & 1) == 0
        ? new MediaTime(time.Ticks / 2, time.TimeBase)
        : new MediaTime(time.Ticks, new Rational(time.TimeBase.Numerator, checked(time.TimeBase.Denominator * 2)));

    /// <summary>Adds half a frame to a timestamp, in a finer time base when the timestamp's own cannot hold it.</summary>
    private static MediaTime Later(MediaTime start, MediaTime half)
    {
        if (half.Rescale(start.TimeBase) == half)
        {
            return start + half;
        }

        Rational finer = new(start.TimeBase.Numerator, checked(start.TimeBase.Denominator * 2));
        return new MediaTime(checked(start.Ticks * 2), finer) + half;
    }

    /// <summary>
    /// Makes one progressive frame at the time of one of <paramref name="current"/>'s fields: the first field in time, or the
    /// second when <paramref name="second"/> is set. Fields beyond the start or end of the run mirror the ones on the other side.
    /// </summary>
    private YuvVideoFrame Make(Held? previous, Held current, Held? next, int firstParity, bool second, MediaTime timestamp, MediaTime? duration, int repeatFields)
    {
        int keep = second ? 1 - firstParity : firstParity;

        // The missing field's lines just before and just after the kept field, and the kept field's own lines a frame earlier and
        // a frame later. In a frame the first field comes before the second, so which frame holds them depends on which is kept.
        Held before = second ? current : previous ?? current;
        Held after = second ? next ?? current : current;
        Held earlier = previous ?? next ?? current;
        Held later = next ?? previous ?? current;
        YuvRaster target = Allocate(current);
        try
        {
            if (target is YuvRaster<byte> bytes)
            {
                Fill(bytes, (YuvRaster<byte>)current.Raster, (YuvRaster<byte>)before.Raster, (YuvRaster<byte>)after.Raster,
                    (YuvRaster<byte>)earlier.Raster, (YuvRaster<byte>)later.Raster, keep, _options.Mode);
            }
            else
            {
                Fill((YuvRaster<ushort>)target, (YuvRaster<ushort>)current.Raster, (YuvRaster<ushort>)before.Raster, (YuvRaster<ushort>)after.Raster,
                    (YuvRaster<ushort>)earlier.Raster, (YuvRaster<ushort>)later.Raster, keep, _options.Mode);
            }

            return current.Frame(target, timestamp, duration, FieldOrder.Progressive, repeatFields);
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    private static void Fill<T>(YuvRaster<T> target, YuvRaster<T> current, YuvRaster<T> before, YuvRaster<T> after, YuvRaster<T> earlier,
        YuvRaster<T> later, int keep, DeinterlaceMode mode)
        where T : unmanaged
    {
        FillPlane(target.Y, current.Y, before.Y, after.Y, earlier.Y, later.Y, keep, mode);
        if (target.U is not null)
        {
            FillPlane(target.U, current.U!, before.U!, after.U!, earlier.U!, later.U!, keep, mode);
            FillPlane(target.V!, current.V!, before.V!, after.V!, earlier.V!, later.V!, keep, mode);
        }

        if (target.A is not null)
        {
            FillPlane(target.A, current.A!, before.A!, after.A!, earlier.A!, later.A!, keep, mode);
        }
    }

    private static void FillPlane<T>(YuvPlane<T> target, YuvPlane<T> current, YuvPlane<T> before, YuvPlane<T> after, YuvPlane<T> earlier,
        YuvPlane<T> later, int keep, DeinterlaceMode mode)
        where T : unmanaged
    {
        int width = target.Width;
        int height = target.Height;
        for (int y = 0; y < height; y++)
        {
            Span<T> row = target.Row(y)[..width];
            int up = y - 1 >= 0 ? y - 1 : y + 1;
            int down = y + 1 < height ? y + 1 : y - 1;
            if ((y & 1) == keep || up >= height || down < 0)
            {
                // A kept line, or a plane one line high that has no kept line to work from.
                current.Row(y)[..width].CopyTo(row);
                continue;
            }

            ReadOnlySpan<T> above = current.Row(up)[..width];
            ReadOnlySpan<T> below = current.Row(down)[..width];
            if (mode == DeinterlaceMode.Bob)
            {
                DeinterlaceKernels.Bob(above, below, row);
                continue;
            }

            int up2 = y - 2 >= 0 ? y - 2 : y;
            int down2 = y + 2 < height ? y + 2 : y;
            DeinterlaceKernels.FieldRows<T> rows = new()
            {
                Above = above,
                Below = below,
                Before = before.Row(y)[..width],
                After = after.Row(y)[..width],
                BeforeUp = before.Row(up2)[..width],
                AfterUp = after.Row(up2)[..width],
                BeforeDown = before.Row(down2)[..width],
                AfterDown = after.Row(down2)[..width],
                EarlierAbove = earlier.Row(up)[..width],
                EarlierBelow = earlier.Row(down)[..width],
                LaterAbove = later.Row(up)[..width],
                LaterBelow = later.Row(down)[..width],
            };
            DeinterlaceKernels.Adaptive(rows, row);
        }
    }

    private YuvVideoFrame Copy(Held held, MediaTime timestamp, MediaTime? duration, FieldOrder fieldOrder, int repeatFields)
    {
        YuvRaster target = Allocate(held);
        try
        {
            Held.CopyPlanes(held.Raster, target);
            return held.Frame(target, timestamp, duration, fieldOrder, repeatFields);
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    private YuvRaster Allocate(Held like) => Allocate(like.Raster, _context.Egami);

    private static YuvRaster Allocate(YuvRaster like, EgamiContext context) => like.BitDepth == 8
        ? YuvRaster.Create(like.Width, like.Height, like.ChromaFormat, like.Color, like.HasAlpha, context)
        : YuvRaster.Create(like.Width, like.Height, like.ChromaFormat, like.BitDepth, like.Color, like.HasAlpha, context);

    /// <summary>Copies a frame the caller keeps into a held frame, reusing a spare one's memory when it fits.</summary>
    private Held Take(YuvVideoFrame frame)
    {
        Held? held = null;
        while (_spare.TryPop(out Held? spare))
        {
            if (spare.Fits(frame.Raster))
            {
                held = spare;
                break;
            }

            spare.Raster.Dispose();
        }

        held ??= new Held(Allocate(frame.Raster, _context.Egami));
        try
        {
            Held.CopyPlanes(frame.Raster, held.Raster);
        }
        catch
        {
            held.Raster.Dispose();
            throw;
        }

        held.Timestamp = frame.Timestamp;
        held.Duration = frame.Duration;
        held.Color = frame.Color;
        held.Hdr = frame.Hdr;
        held.Hdr10Plus = frame.Hdr10Plus;
        held.RenderSize = frame.RenderSize;
        held.FieldOrder = frame.FieldOrder;
        held.RepeatFields = frame.RepeatFields;
        return held;
    }

    private void Recycle(ref Held? held)
    {
        if (held is not null)
        {
            _spare.Push(held);
            held = null;
        }
    }

    /// <summary>A copy of a frame the filter holds to look back or ahead at.</summary>
    private sealed class Held(YuvRaster raster)
    {
        public YuvRaster Raster { get; } = raster;

        public MediaTime Timestamp { get; set; }

        public MediaTime? Duration { get; set; }

        public ColorCodePoints? Color { get; set; }

        public HdrMetadata? Hdr { get; set; }

        public Hdr10PlusMetadata? Hdr10Plus { get; set; }

        public Size? RenderSize { get; set; }

        public FieldOrder FieldOrder { get; set; }

        public int RepeatFields { get; set; }

        public bool Fits(YuvRaster other) => other.Width == Raster.Width && other.Height == Raster.Height && other.ChromaFormat == Raster.ChromaFormat
            && other.BitDepth == Raster.BitDepth && other.HasAlpha == Raster.HasAlpha && other.Color == Raster.Color && other.GetType() == Raster.GetType();

        public YuvVideoFrame Frame(YuvRaster raster, MediaTime timestamp, MediaTime? duration, FieldOrder fieldOrder, int repeatFields) =>
            new(raster, timestamp, duration, Color)
            {
                Hdr = Hdr,
                Hdr10Plus = Hdr10Plus,
                RenderSize = RenderSize,
                FieldOrder = fieldOrder,
                RepeatFields = repeatFields,
            };

        public static void CopyPlanes(YuvRaster source, YuvRaster target)
        {
            if (source is YuvRaster<byte> bytes)
            {
                CopyPlanes(bytes, (YuvRaster<byte>)target);
            }
            else
            {
                CopyPlanes((YuvRaster<ushort>)source, (YuvRaster<ushort>)target);
            }
        }

        private static void CopyPlanes<T>(YuvRaster<T> source, YuvRaster<T> target)
            where T : unmanaged
        {
            CopyPlane(source.Y, target.Y);
            if (source.U is not null)
            {
                CopyPlane(source.U, target.U!);
                CopyPlane(source.V!, target.V!);
            }

            if (source.A is not null)
            {
                CopyPlane(source.A, target.A!);
            }
        }

        private static void CopyPlane<T>(YuvPlane<T> source, YuvPlane<T> target)
            where T : unmanaged
        {
            for (int y = 0; y < source.Height; y++)
            {
                source.Row(y)[..source.Width].CopyTo(target.Row(y));
            }
        }
    }
}
