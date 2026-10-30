using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Operations;
using Egami.Operations.Compose;
using Egami.Pixels;

namespace Egami.Formats.Psd;

/// <summary>
/// Flattens a PSD's layers, bottom to top, one layer at a time, into premultiplied float RGBA the canvas's size, as Photoshop
/// composites them: the W3C compositing model with Photoshop's blend modes, a layer's coverage being its transparency times its
/// user mask (unless disabled; the default color outside the mask's bounds), vector mask, fill opacity and blending ranges, its
/// opacity applied as it blends. Clipped layers blend onto their base's content within the base's coverage, and the clipping
/// group goes down with the base's mode and opacity. A pass-through group draws its contents straight onto the backdrop, then
/// mixes the backdrop before and after by the group's opacity and mask; other groups are isolated. 8 and 16-bit documents blend
/// their encoded values, 32-bit ones linear light, as Photoshop does. Gray documents blend as RGB with equal channels. The
/// layers are given with <see cref="Add"/> in file order, so a load can flatten as it decodes them.
/// </summary>
internal sealed partial class PsdFlattener
{
    private readonly int _width;
    private readonly int _height;
    private readonly PsdFile _file;
    private readonly PsdFlattenOptions _options;
    private readonly EgamiContext _context;
    private readonly WarningList _warnings;
    private readonly Func<int, PsdLayer> _infoOf;
    private readonly Dictionary<int, int> _groupOf = [];
    private readonly List<Frame> _frames = [];
    private readonly Dictionary<string, List<string>> _skipped = new(StringComparer.Ordinal);
    private readonly float[] _row;
    private readonly int _channels;
    private readonly int _stride;
    private readonly Dictionary<float[], long> _rented = new(ReferenceEqualityComparer.Instance);
    private float[]? _scratch;
    private long _bytes;

    /// <summary>Starts a flattening.</summary>
    /// <param name="file">The file, its records read.</param>
    /// <param name="options">The options.</param>
    /// <param name="context">The context whose limits the canvases count against.</param>
    /// <param name="warnings">Where to put what was skipped.</param>
    /// <param name="infoOf">The record of each layer as it is to be flattened (the caller's changed copy, or the file's).</param>
    public PsdFlattener(PsdFile file, PsdFlattenOptions options, EgamiContext context, WarningList warnings, Func<int, PsdLayer> infoOf)
    {
        _file = file;
        _width = file.Width;
        _height = file.Height;
        _options = options;
        _context = context;
        _warnings = warnings;
        _infoOf = infoOf;
        _channels = file.Mode == PsdColorMode.Cmyk ? 4 : 3;
        _stride = _channels + 1;
        _row = new float[_stride * _width];

        // Each group's hidden start record (its GroupEnd) is matched with the group record that ends it, above its contents.
        Stack<int> open = new();
        for (int i = 0; i < file.Records.Count; i++)
        {
            PsdLayerKind kind = file.Records[i].Info.Kind;
            if (kind == PsdLayerKind.GroupEnd)
            {
                open.Push(i);
            }
            else if (kind != PsdLayerKind.Layer && open.Count > 0)
            {
                _groupOf[open.Pop()] = i;
            }
        }

        _frames.Add(new Frame(Allocate((long)_stride * _width * _height), -1, null, false, false));
    }

    /// <summary>Gets the bytes of canvases the flattening needs at the most, for <see cref="RasterInfo.WorkingBytes"/>: one per open group level and two for clipping, at 16 bytes a pixel (20 for CMYK).</summary>
    public static long WorkingBytes(PsdFile file)
    {
        int depth = 0, deepest = 0;
        foreach (PsdLayerRecord record in file.Records)
        {
            if (record.Info.Kind == PsdLayerKind.GroupEnd)
            {
                deepest = Math.Max(deepest, ++depth);
            }
            else if (record.Info.Kind != PsdLayerKind.Layer)
            {
                depth = Math.Max(0, depth - 1);
            }
        }

        long pixel = file.Mode == PsdColorMode.Cmyk ? 20 : 16;
        return (long)file.Width * file.Height * ((pixel * (deepest + 3)) + 4);
    }

    /// <summary>Adds the record at <paramref name="index"/>, in file order, with its pixels (its ink for CMYK) and mask when it has them.</summary>
    public void Add(int index, Raster? pixels, PsdMask? mask, InkRaster? ink = null)
    {
        PsdLayerRecord record = _file.Records[index];
        Frame frame = _frames[^1];
        switch (record.Info.Kind)
        {
            case PsdLayerKind.GroupEnd:
                Open(index, frame);
                return;
            case PsdLayerKind.OpenGroup or PsdLayerKind.ClosedGroup:
                if (frame.GroupIndex == index)
                {
                    Close(frame, mask);
                }

                return;
        }

        PsdLayer info = _infoOf(index);
        bool clipped = info.IsClipped && frame.Pending is not null;
        Pending? basis = clipped ? frame.Pending : null;
        if (!clipped)
        {
            Flush(frame);
        }

        bool hidden = frame.Hidden || !info.IsVisible || (basis?.Hidden ?? false);
        if (record.Info.LayerType == PsdLayerType.Adjustment)
        {
            if (!hidden)
            {
                Adjust(info, record, mask, clipped ? basis! : null, frame);
            }

            if (!clipped)
            {
                frame.Pending = null;
            }

            return;
        }

        if (hidden)
        {
            if (!clipped)
            {
                frame.Pending = new Pending(null, null, default, info, record, Hidden: true);
            }

            return;
        }

        Note(info, record);
        float[] buffer = clipped ? Scratch() : Allocate((long)_stride * _width * _height);
        float[]? clip = clipped ? null : Allocate((long)_width * _height);
        Rect area = Render(info, record, pixels, ink, mask, buffer, clip);
        if (clipped)
        {
            // The clipped layer blends onto the base's content within the base's coverage, which stays the clipping group's.
            Pending b = basis!;
            Rect within = Intersect(area, b.Area);
            BlendDown(buffer, within, info, record.BlendRanges, b.Buffer!, b.Clip);
            Clear(buffer, area);
            if (!record.BlendClippedAsGroup || !b.Record.BlendClippedAsGroup)
            {
                Skip("Psd.ClippingBlendSkipped", info.Name);
            }

            return;
        }

        frame.Pending = new Pending(buffer, clip, area, info, record, Hidden: false);
    }

    /// <summary>Flattens what is left and returns the canvas in the document's type, with alpha only when some pixel is not opaque.</summary>
    public Raster Finish()
    {
        try
        {
            return Output(Complete());
        }
        finally
        {
            ReleaseAll();
        }
    }

    /// <summary>Flattens what is left of a CMYK document and returns the canvas as an ink raster of <paramref name="inks"/>.</summary>
    public InkRaster FinishInk(Colors.InkSet inks)
    {
        try
        {
            return OutputInk(Complete(), inks);
        }
        finally
        {
            ReleaseAll();
        }
    }

    /// <summary>Returns every buffer the flattening still holds to the pool; the flattener cannot be used after.</summary>
    public void ReleaseAll()
    {
        foreach (float[] buffer in _rented.Keys.ToArray())
        {
            Release(buffer);
        }

        _scratch = null;
        _frames.Clear();
        ReleaseVector();
    }

    private float[] Complete()
    {
        while (_frames.Count > 1)
        {
            // A group whose end record never came: close it as it stands.
            Close(_frames[^1], null);
        }

        Flush(_frames[0]);
        foreach ((string id, List<string> names) in _skipped)
        {
            _warnings.Add(id, Message(id, names));
        }

        return _frames[0].Canvas;
    }

    private static string Message(string id, List<string> names)
    {
        string list = string.Join(", ", names.Take(10).Select(n => $"\"{n}\"")) + (names.Count > 10 ? $" and {names.Count - 10} more" : "");
        return id switch
        {
            "Psd.EffectsSkipped" => $"Layer effects were not drawn, on {list}.",
            "Psd.AdjustmentSkipped" => $"Adjustment layers of a kind Egami does not apply were left out: {list}.",
            "Psd.FillSkipped" => $"Gradient and pattern fill layers were left out: {list}.",
            "Psd.KnockoutSkipped" => $"Knockout was not applied, on {list}.",
            "Psd.BlendModeUnknown" => $"Layers with a blend mode Egami does not know were blended Normal: {list}.",
            "Psd.MaskFeatherSkipped" => $"Mask feathering was not applied, on {list}.",
            "Psd.ClippingBlendSkipped" => $"Clipped layers that blend with the layers under the base were blended with the base alone: {list}.",
            _ => $"{id}: {list}.",
        };
    }

    private void Skip(string id, string name)
    {
        if (!_skipped.TryGetValue(id, out List<string>? names))
        {
            _skipped[id] = names = [];
        }

        if (!names.Contains(name))
        {
            names.Add(name);
        }
    }

    /// <summary>Notes what a visible layer has that the flattening leaves out.</summary>
    private void Note(PsdLayer info, PsdLayerRecord record)
    {
        if (record.Knockout != 0)
        {
            Skip("Psd.KnockoutSkipped", info.Name);
        }

        if (record.UserMaskFeather > 0 || record.VectorMaskFeather > 0)
        {
            Skip("Psd.MaskFeatherSkipped", info.Name);
        }

        if (EffectsOn(record))
        {
            Skip("Psd.EffectsSkipped", info.Name);
        }
    }

    /// <summary>Returns whether the layer has effects turned on: the lfx2 or lmfx descriptor's master switch, or an old lrFX block.</summary>
    private static bool EffectsOn(PsdLayerRecord record)
    {
        foreach (string key in (string[])["lfx2", "lmfx"])
        {
            if (record.Blocks.TryGetValue(key, out byte[]? data))
            {
                PsdDescriptor? effects = PsdDescriptor.ReadVersioned(data, 4);
                return effects?.Flag("masterFXSwitch") ?? true;
            }
        }

        return record.Has("lrFX");
    }

    private void Open(int index, Frame parent)
    {
        int group = _groupOf.TryGetValue(index, out int g) ? g : -1;
        PsdLayer? info = group >= 0 ? _infoOf(group) : null;
        bool clipped = info is not null && info.IsClipped && parent.Pending is not null;
        if (!clipped)
        {
            Flush(parent);
        }

        bool hidden = parent.Hidden || (info is not null && !info.IsVisible) || (clipped && parent.Pending!.Hidden);
        bool passThrough = info is not null && info.BlendMode is null && info.BlendModeKey == "pass" && !clipped;
        float[] canvas;
        if (hidden)
        {
            canvas = parent.Canvas;
        }
        else if (passThrough)
        {
            canvas = Allocate((long)_stride * _width * _height);
            parent.Canvas.AsSpan(0, _stride * _width * _height).CopyTo(canvas);
        }
        else
        {
            canvas = Allocate((long)_stride * _width * _height);
        }

        _frames.Add(new Frame(canvas, group, info, hidden, passThrough));
    }

    private void Close(Frame frame, PsdMask? mask)
    {
        Flush(frame);
        _frames.RemoveAt(_frames.Count - 1);
        Frame parent = _frames[^1];
        if (frame.Hidden)
        {
            return;
        }

        if (frame.Info is null)
        {
            // A group start with no group record: its contents go down as they are.
            BlendDown(frame.Canvas, new Rect(0, 0, _width, _height), PlainNormal, [], parent.Canvas);
            Release(frame.Canvas);
            return;
        }

        PsdLayerRecord record = _file.Records[frame.GroupIndex];
        PsdLayer info = frame.Info;
        Note(info, record);
        Rect all = new(0, 0, _width, _height);
        if (frame.PassThrough)
        {
            // The backdrop before and after the contents, mixed by the group's opacity, fill and mask.
            float[] before = parent.Canvas;
            float[] after = frame.Canvas;
            float opacity = info.Opacity * info.FillOpacity;
            for (int y = 0; y < _height; y++)
            {
                Span<float> shape = _row.AsSpan(0, _width);
                MaskRow(record, mask, y, 0, shape);
                int row = _stride * y * _width;
                for (int x = 0; x < _width; x++)
                {
                    float t = shape[x] * opacity;
                    int at = row + (_stride * x);
                    for (int c = 0; c < _stride; c++)
                    {
                        before[at + c] += (after[at + c] - before[at + c]) * t;
                    }
                }
            }

            Release(after);
            return;
        }

        // An isolated group is a layer whose pixels are its contents flattened.
        float[] buffer = frame.Canvas;
        float[] clip = Allocate((long)_width * _height);
        for (int y = 0; y < _height; y++)
        {
            Span<float> shape = _row.AsSpan(0, _width);
            MaskRow(record, mask, y, 0, shape);
            int row = y * _width;
            for (int x = 0; x < _width; x++)
            {
                int at = _stride * (row + x);
                float s = shape[x];
                clip[row + x] = buffer[at + _channels] * s;
                float content = s * info.FillOpacity;
                for (int c = 0; c < _stride; c++)
                {
                    buffer[at + c] *= content;
                }
            }
        }

        if (info.IsClipped && parent.Pending is { Hidden: false } basis)
        {
            BlendDown(buffer, Intersect(all, basis.Area), info, record.BlendRanges, basis.Buffer!, basis.Clip);
            Release(buffer);
            Release(clip);
            return;
        }

        parent.Pending = new Pending(buffer, clip, all, info, record, Hidden: false);
    }

    /// <summary>Blends a frame's waiting layer (and what is clipped to it) onto the frame's canvas.</summary>
    private void Flush(Frame frame)
    {
        if (frame.Pending is { Hidden: false, Buffer: not null } pending)
        {
            BlendDown(pending.Buffer, pending.Area, pending.Info, pending.Record.BlendRanges, frame.Canvas);
            Release(pending.Buffer);
            if (pending.Clip is not null)
            {
                Release(pending.Clip);
            }
        }

        frame.Pending = null;
    }

    /// <summary>
    /// Blends premultiplied <paramref name="source"/> within <paramref name="area"/> onto <paramref name="target"/> with the layer's
    /// mode, opacity and blending ranges. With a <paramref name="clip"/> (a clipping base's coverage) the target is taken relative
    /// to it, so the source blends onto the base's content as if the base were opaque, and the base's coverage stays the result's:
    /// a clipped layer draws only where its base is, and as much as the base is there.
    /// </summary>
    private void BlendDown(float[] source, Rect area, PsdLayer info, uint[] blendRanges, float[] target, float[]? clip = null)
    {
        if (area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        BlendMode mode = ModeOf(info);
        bool ranges = HasRanges(blendRanges);
        float opacity = info.Opacity;
        float[] row = ArrayPool<float>.Shared.Rent(_stride * area.Width);
        try
        {
            for (int y = area.Y; y < area.Bottom; y++)
            {
                int at = _stride * ((y * _width) + area.X);
                Span<float> s = row.AsSpan(0, _stride * area.Width);
                source.AsSpan(at, _stride * area.Width).CopyTo(s);
                Span<float> d = target.AsSpan(at, _stride * area.Width);
                if (clip is not null)
                {
                    Relative(d, clip.AsSpan((y * _width) + area.X, area.Width), divide: true);
                }

                if (ranges)
                {
                    BlendIf(blendRanges, s, d, _file.ColorChannels);
                }

                if (_channels == 4)
                {
                    InkRow(mode, s, d, opacity, area.X, y);
                }
                else if (mode == BlendMode.SoftLight)
                {
                    SoftLight(s, d, opacity);
                }
                else
                {
                    BlendKernels.BlendFloat(mode, s, d, default, opacity, area.X, y);
                }

                if (clip is not null)
                {
                    Relative(d, clip.AsSpan((y * _width) + area.X, area.Width), divide: false);
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(row);
        }
    }

    /// <summary>Divides premultiplied pixels by the coverage of their clipping base (0 where it is 0), or multiplies them back.</summary>
    private void Relative(Span<float> pixels, ReadOnlySpan<float> coverage, bool divide)
    {
        for (int i = 0; i < coverage.Length; i++)
        {
            float c = coverage[i];
            float f = divide ? (c > 0 ? 1 / c : 0) : c;
            Span<float> p = pixels.Slice(_stride * i, _stride);
            for (int k = 0; k < p.Length; k++)
            {
                p[k] = divide ? Math.Min(p[k] * f, 1) : p[k] * f;
            }
        }
    }

    /// <summary>
    /// Blends a row of premultiplied ink pixels (four inks, then alpha) onto another with core Egami's ink math, which the ink
    /// canvas shares: every mode on the inks' complements, the non-separable ones on Cyan, Magenta and Yellow with Black kept.
    /// </summary>
    private static void InkRow(BlendMode mode, ReadOnlySpan<float> s, Span<float> d, float opacity, int x, int y)
    {
        int n = d.Length / 5;
        float[] rented = ArrayPool<float>.Shared.Rent(n * 15);
        try
        {
            Span<float> tints = rented.AsSpan(0, 4 * n), alpha = rented.AsSpan(4 * n, n), under = rented.AsSpan(5 * n, 5 * n), result = rented.AsSpan(10 * n, 5 * n);
            for (int i = 0; i < n; i++)
            {
                float a = s[(5 * i) + 4];
                alpha[i] = a;
                for (int p = 0; p < 5; p++)
                {
                    if (p < 4)
                    {
                        tints[(4 * i) + p] = a > 0 ? Math.Clamp(s[(5 * i) + p] / a, 0, 1) : 0;
                    }

                    under[(p * n) + i] = d[(5 * i) + p];
                }
            }

            InkBlend.Mix(InkLayout, n, tints, 4, alpha, default, opacity, mode, default, under, result, x, y);
            for (int i = 0; i < n; i++)
            {
                for (int p = 0; p < 5; p++)
                {
                    d[(5 * i) + p] = result[(p * n) + i];
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    private static readonly InkBlend.InkLayout InkLayout = new(4, 4, [0, 1, 2], 3);

    /// <summary>Returns the mode a layer blends with: its <see cref="PsdLayer.BlendMode"/>, or Normal with a warning when Egami has none for its key.</summary>
    private BlendMode ModeOf(PsdLayer info)
    {
        if (info.BlendMode is BlendMode mode)
        {
            return mode;
        }

        if (info.BlendModeKey != "pass")
        {
            Skip("Psd.BlendModeUnknown", info.Name);
        }

        return BlendMode.SourceOver;
    }

    /// <summary>
    /// Photoshop's Soft Light, which differs from the W3C one (and <see cref="BlendMode.SoftLight"/>) where the source is light:
    /// <c>Cb + (2Cs − 1)(√Cb − Cb)</c> there, without the W3C curve for dark backdrops; then source-over.
    /// </summary>
    private static void SoftLight(Span<float> s, Span<float> d, float opacity)
    {
        for (int i = 0; i < d.Length; i += 4)
        {
            float sa = s[i + 3] * opacity, da = d[i + 3];
            if (sa <= 0)
            {
                continue;
            }

            float both = sa * da;
            for (int c = 0; c < 3; c++)
            {
                float cs = s[i + c] * opacity / sa, cb = da > 0 ? d[i + c] / da : 0;
                float b = cs <= 0.5f ? cb - ((1 - (2 * cs)) * cb * (1 - cb)) : cb + (((2 * cs) - 1) * (MathF.Sqrt(Math.Max(cb, 0)) - cb));
                d[i + c] = (s[i + c] * opacity * (1 - da)) + (d[i + c] * (1 - sa)) + (both * b);
            }

            d[i + 3] = sa + da - both;
        }
    }

    private static readonly PsdLayer PlainNormal = new("", default, 1, "norm", BlendMode.SourceOver, true, false, PsdLayerKind.Layer, 0);

    private float[] Scratch()
    {
        return _scratch ??= Allocate((long)_stride * _width * _height);
    }

    /// <summary>Rents a zeroed buffer of at least <paramref name="floats"/> values from the pool, counting it against <see cref="SafetyLimits.MaxTotalBytes"/>.</summary>
    private float[] Allocate(long floats)
    {
        long bytes = floats * 4;
        if (floats > Array.MaxLength || _bytes + bytes > _context.Limits.MaxTotalBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), _context.Limits.MaxTotalBytes, _bytes + bytes);
        }

        float[] buffer = ArrayPool<float>.Shared.Rent((int)floats);
        Array.Clear(buffer, 0, (int)floats);
        _rented[buffer] = bytes;
        _bytes += bytes;
        return buffer;
    }

    private void Release(float[] buffer)
    {
        if (_rented.Remove(buffer, out long bytes))
        {
            _bytes -= bytes;
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    private void Clear(float[] buffer, Rect area)
    {
        for (int y = area.Y; y < area.Bottom; y++)
        {
            buffer.AsSpan(_stride * ((y * _width) + area.X), _stride * area.Width).Clear();
        }
    }

    private static Rect Intersect(Rect a, Rect b)
    {
        int x0 = Math.Max(a.X, b.X), y0 = Math.Max(a.Y, b.Y), x1 = Math.Min(a.Right, b.Right), y1 = Math.Min(a.Bottom, b.Bottom);
        return x1 > x0 && y1 > y0 ? new Rect(x0, y0, x1 - x0, y1 - y0) : default;
    }

    /// <summary>One open group: its canvas, the group record ending it, and the layer waiting to blend down (the clipping base).</summary>
    private sealed class Frame(float[] canvas, int groupIndex, PsdLayer? info, bool hidden, bool passThrough)
    {
        public float[] Canvas { get; } = canvas;

        public int GroupIndex { get; } = groupIndex;

        public PsdLayer? Info { get; } = info;

        public bool Hidden { get; } = hidden;

        public bool PassThrough { get; } = passThrough;

        public Pending? Pending { get; set; }
    }

    /// <summary>A layer rendered and waiting to blend down: its premultiplied content, its coverage (the clip of what is clipped to it) and its area.</summary>
    private sealed record Pending(float[]? Buffer, float[]? Clip, Rect Area, PsdLayer Info, PsdLayerRecord Record, bool Hidden);
}
