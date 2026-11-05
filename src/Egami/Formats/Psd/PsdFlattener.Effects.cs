using System.Buffers;

using Egami.Operations;

namespace Egami.Formats.Psd;

/// <summary>
/// The flattener's layer effects: drop and inner shadows, outer and inner glows, bevel and emboss, satin, color, gradient and
/// pattern overlays, and strokes, drawn from the layer's shape (its coverage after its masks) onto the backdrop around its
/// content, then the whole mixed with the backdrop by the layer's opacity.
/// </summary>
internal sealed partial class PsdFlattener
{
    /// <summary>Gets the Gaussian σ per pixel of an effect's size (shadows, glows, satin), fitted to Photoshop-saved files.</summary>
    internal const double EffectSigma = 0.4;

    /// <summary>A rendered layer with effects waiting to blend down: its effects and the plane of the masks that hide them.</summary>
    private sealed record EffectPending(float[]? Buffer, float[]? Clip, Rect Area, PsdLayer Info, PsdLayerRecord Record, PsdEffects Effects, float[]? Hide)
        : Pending(Buffer, Clip, Area, Info, Record, false);

    /// <summary>Returns the layer's effects when they are to be drawn; warns when they are on but cannot be read.</summary>
    private PsdEffects? EffectsOf(PsdLayer info, PsdLayerRecord record)
    {
        if (!_options.ApplyEffects)
        {
            return null;
        }

        PsdEffects? effects = PsdEffects.Of(record, _file);
        if (effects is null && PsdEffects.IsDamaged(record))
        {
            Skip("Psd.EffectsSkipped", info.Name);
        }

        return effects;
    }

    /// <summary>
    /// Returns the plane of the masks that hide the layer's effects rather than shape them (user mask with lmgm, vector mask
    /// with vmgm), or <see langword="null"/> when none does.
    /// </summary>
    private float[]? HidePlane(PsdLayerRecord record, PsdMask? mask, PsdEffects effects)
    {
        bool user = effects.UserMaskHides && mask is { IsDisabled: false };
        bool vector = effects.VectorMaskHides && (record.Has("vmsk") || record.Has("vsms"));
        if (!user && !vector)
        {
            return null;
        }

        float[] plane = Allocate((long)_width * _height);
        for (int y = 0; y < _height; y++)
        {
            MaskRow(record, user ? mask : null, y, 0, plane.AsSpan(y * _width, _width), vector);
        }

        return plane;
    }

    /// <summary>
    /// Blends a layer with effects onto <paramref name="target"/>. Under the layer: the drop shadows and outer glows, then the
    /// parts of strokes and outer bevels outside the shape, which split each pixel with the content. Then the content in the
    /// layer's mode, and over it, within the layer's shape (atop the content, so they take its coverage rather than add to it),
    /// the interior effects and the parts of strokes inside the shape; when the interior effects blend as a group they go onto
    /// the content first and the fill opacity takes both. Last the whole is mixed with the backdrop by the layer's opacity and
    /// the masks that hide effects. With a <paramref name="clip"/> the target is taken relative to its clipping base, as
    /// <see cref="BlendDown"/> does.
    /// </summary>
    private void DrawEffects(EffectPending p, float[] target, float[]? clip)
    {
        PsdEffects effects = p.Effects;
        PsdLayer info = p.Info;
        if (p.Area.Width <= 0 || p.Area.Height <= 0 || p.Clip is null)
        {
            return;
        }

        int reach = effects.Reach;
        Rect area = Intersect(new Rect(p.Area.X - reach, p.Area.Y - reach, p.Area.Width + (2 * reach), p.Area.Height + (2 * reach)), new Rect(0, 0, _width, _height));
        Fx c = new(this, area, p, effects);
        float[]? before = null, backdrop = null;
        try
        {
            c.Shape = Allocate((long)area.Width * area.Height);
            for (int y = 0; y < area.Height; y++)
            {
                Span<float> row = c.Shape.AsSpan(y * area.Width, area.Width);
                if (effects.TransparencyShapes)
                {
                    p.Clip.AsSpan(((area.Y + y) * _width) + area.X, area.Width).CopyTo(row);
                }
                else if (area.Y + y >= p.Area.Y && area.Y + y < p.Area.Bottom)
                {
                    // The layer's transparency does not shape the effects: its whole bounds do.
                    row.Slice(p.Area.X - area.X, p.Area.Width).Fill(1);
                }
            }

            if (clip is not null)
            {
                for (int y = area.Y; y < area.Bottom; y++)
                {
                    Relative(target.AsSpan(_stride * ((y * _width) + area.X), _stride * area.Width), clip.AsSpan((y * _width) + area.X, area.Width), divide: true);
                }
            }

            if (info.Opacity < 1 || p.Hide is not null)
            {
                before = Copy(target, area);
            }

            foreach (PsdEffect e in effects.Items)
            {
                if (e.IsUnder)
                {
                    Draw(c, e, target, inside: false);
                }
            }

            foreach (PsdEffect e in effects.Items)
            {
                if (e.Kind is PsdEffectKind.Stroke or PsdEffectKind.Bevel)
                {
                    Draw(c, e, target, inside: false);
                }
            }

            List<PsdEffect> interior = effects.Items.FindAll(e => e.IsInterior);
            List<PsdEffect> strokes = effects.Items.FindAll(e => e.Kind == PsdEffectKind.Stroke);
            if (effects.InteriorAsGroup && interior.Count > 0)
            {
                // The interior effects go onto the content, unfilled; the fill opacity then takes both.
                Atop(c, p.Buffer!, null, interior);
                float fill = info.FillOpacity;
                for (int y = area.Y; y < area.Bottom && fill < 1; y++)
                {
                    foreach (ref float v in p.Buffer.AsSpan(_stride * ((y * _width) + area.X), _stride * area.Width))
                    {
                        v *= fill;
                    }
                }

                interior.Clear();
            }

            if (interior.Count + strokes.Count > 0)
            {
                backdrop = Copy(target, area);
            }

            BlendDown(p.Buffer!, effects.InteriorAsGroup ? area : p.Area, info with { Opacity = 1 }, p.Record.BlendRanges, target);
            if (backdrop is not null)
            {
                interior.AddRange(strokes);
                Atop(c, target, backdrop, interior);
            }

            if (before is not null)
            {
                Mix(target, before, area, info.Opacity, p.Hide);
            }

            if (clip is not null)
            {
                for (int y = area.Y; y < area.Bottom; y++)
                {
                    Relative(target.AsSpan(_stride * ((y * _width) + area.X), _stride * area.Width), clip.AsSpan((y * _width) + area.X, area.Width), divide: false);
                }
            }
        }
        finally
        {
            if (before is not null)
            {
                Release(before);
            }

            if (backdrop is not null)
            {
                Release(backdrop);
            }

            c.Release();
        }
    }

    /// <summary>Copies the target within <paramref name="area"/> into a new pooled buffer of the area's size.</summary>
    private float[] Copy(float[] target, Rect area)
    {
        float[] copy = Allocate((long)_stride * area.Width * area.Height);
        for (int y = 0; y < area.Height; y++)
        {
            target.AsSpan(_stride * (((area.Y + y) * _width) + area.X), _stride * area.Width).CopyTo(copy.AsSpan(_stride * y * area.Width));
        }

        return copy;
    }

    /// <summary>
    /// Draws effects within the layer's shape, atop what is there: the part of each pixel the shape covers (coverage S) holds
    /// the backdrop B with the content blended in, so its own color is B + (target − B) / S; the effects blend onto that, and
    /// the pixel becomes B + S (that − B). Without a backdrop (the content alone) B is clear.
    /// </summary>
    private void Atop(Fx c, float[] target, float[]? backdrop, List<PsdEffect> effects)
    {
        Rect area = c.Area;
        float[] shape = c.Shape!;
        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 1)
            {
                foreach (PsdEffect e in effects)
                {
                    Draw(c, e, target, inside: true);
                }
            }

            for (int y = 0; y < area.Height; y++)
            {
                Span<float> d = target.AsSpan(_stride * (((area.Y + y) * _width) + area.X), _stride * area.Width);
                for (int x = 0; x < area.Width; x++)
                {
                    float s = shape[(y * area.Width) + x];
                    if (s <= Empty)
                    {
                        // No shape: the effects drew nothing that stays; the backdrop is what was there.
                        for (int k = 0; k < _stride && pass == 1; k++)
                        {
                            d[(_stride * x) + k] = backdrop is null ? 0 : backdrop[(_stride * ((y * area.Width) + x)) + k];
                        }

                        continue;
                    }

                    for (int k = 0; k < _stride; k++)
                    {
                        int i = (_stride * x) + k;
                        float b = backdrop is null ? 0 : backdrop[(_stride * y * area.Width) + i];
                        d[i] = pass == 0 ? b + ((d[i] - b) / s) : b + ((d[i] - b) * s);
                    }
                }
            }
        }
    }

    /// <summary>Mixes the target back toward the backdrop it had before: by the opacity times the hiding masks' coverage.</summary>
    private void Mix(float[] target, float[] before, Rect area, float opacity, float[]? hide)
    {
        for (int y = 0; y < area.Height; y++)
        {
            Span<float> d = target.AsSpan(_stride * (((area.Y + y) * _width) + area.X), _stride * area.Width);
            ReadOnlySpan<float> b = before.AsSpan(_stride * y * area.Width, _stride * area.Width);
            for (int x = 0; x < area.Width; x++)
            {
                float t = opacity * (hide is null ? 1 : hide[((area.Y + y) * _width) + area.X + x]);
                for (int k = 0; k < _stride; k++)
                {
                    int i = (_stride * x) + k;
                    d[i] = b[i] + ((d[i] - b[i]) * t);
                }
            }
        }
    }

    /// <summary>
    /// Draws one effect onto <paramref name="target"/>: its part outside the layer's shape, or (<paramref name="inside"/>) its
    /// part inside, as a share of the shape's coverage, for <see cref="Atop"/>.
    /// </summary>
    private void Draw(Fx c, PsdEffect e, float[] target, bool inside)
    {
        PsdDescriptor s = e.Settings;
        switch (e.Kind)
        {
            case PsdEffectKind.DropShadow or PsdEffectKind.InnerShadow:
                {
                    float[] plane = c.Shadow(e);
                    Blend(c, plane, new SolidPainter(ColorOf(s.Get<PsdDescriptor>("Clr ") ?? Black)), null, e.ModeKey(), e.Opacity, target);
                    c.Free(plane);
                    break;
                }

            case PsdEffectKind.OuterGlow or PsdEffectKind.InnerGlow:
                {
                    float[] plane = c.Glow(e);
                    float[]? table = s.Get<PsdDescriptor>("Grad") is PsdDescriptor grad ? GradientTable(grad, s) : null;
                    Blend(c, plane, table is null ? new SolidPainter(ColorOf(s.Get<PsdDescriptor>("Clr ") ?? Black)) : null, table, e.ModeKey(), e.Opacity, target, (float)Math.Clamp((s.Number("ShdN") ?? 0) / 100, 0, 1));
                    c.Free(plane);
                    break;
                }

            case PsdEffectKind.Satin:
                {
                    float[] plane = c.Satin(e);
                    Blend(c, plane, new SolidPainter(ColorOf(s.Get<PsdDescriptor>("Clr ") ?? Black)), null, e.ModeKey(), e.Opacity, target);
                    c.Free(plane);
                    break;
                }

            case PsdEffectKind.ColorOverlay:
                Blend(c, null, new SolidPainter(ColorOf(s.Get<PsdDescriptor>("Clr ") ?? Black)), null, e.ModeKey(), e.Opacity, target);
                break;
            case PsdEffectKind.GradientOverlay or PsdEffectKind.PatternOverlay:
                if (OverlayPainter(c, e) is Painter painter)
                {
                    Blend(c, null, painter, null, e.ModeKey(), e.Opacity, target);
                }
                else
                {
                    Skip("Psd.EffectsSkipped", c.Pending.Info.Name);
                }

                break;
            case PsdEffectKind.Bevel:
                {
                    (float[] highlight, float[] shadow) = c.Bevel(e, inside);
                    Blend(c, shadow, new SolidPainter(ColorOf(s.Get<PsdDescriptor>("sdwC") ?? Black)), null, e.ModeKey("sdwM"), (float)Math.Clamp((s.Number("sdwO") ?? 75) / 100, 0, 1), target);
                    Blend(c, highlight, new SolidPainter(ColorOf(s.Get<PsdDescriptor>("hglC") ?? White)), null, e.ModeKey("hglM"), (float)Math.Clamp((s.Number("hglO") ?? 75) / 100, 0, 1), target);
                    c.Free(highlight);
                    c.Free(shadow);
                    break;
                }

            case PsdEffectKind.Stroke:
                DrawStroke(c, e, target, inside);
                break;
        }
    }

    /// <summary>Draws a stroke's part outside the shape, or (<paramref name="inside"/>) inside it, in its color, gradient or pattern.</summary>
    private void DrawStroke(Fx c, PsdEffect e, float[] target, bool inside)
    {
        PsdDescriptor s = e.Settings;
        float[] plane = c.Stroke(e, inside);
        Painter? paint = (s.Items.GetValueOrDefault("PntT") as string) switch
        {
            "GrFl" when IsShapeBurst(s) => null,
            "GrFl" or "Ptrn" => OverlayPainter(c, e),
            _ => new SolidPainter(ColorOf(s.Get<PsdDescriptor>("Clr ") ?? Black)),
        };
        float[]? burst = paint is null && s.Get<PsdDescriptor>("Grad") is PsdDescriptor g && IsShapeBurst(s) ? GradientTable(g, s) : null;
        if (paint is null && burst is null)
        {
            Skip("Psd.EffectsSkipped", c.Pending.Info.Name);
        }
        else if (burst is not null)
        {
            // A shape burst gradient runs across the stroke, from its outer edge in.
            float[] place = c.Burst(e, plane);
            Blend(c, plane, null, burst, e.ModeKey(), e.Opacity, target, 0, place);
            c.Free(place);
        }
        else
        {
            Blend(c, plane, paint, null, e.ModeKey(), e.Opacity, target);
        }

        c.Free(plane);
    }

    private static readonly PsdDescriptor Black = PsdDescriptor.Of("RGBC", ("Rd  ", 0.0), ("Grn ", 0.0), ("Bl  ", 0.0));
    private static readonly PsdDescriptor White = PsdDescriptor.Of("RGBC", ("Rd  ", 255.0), ("Grn ", 255.0), ("Bl  ", 255.0));

    private static bool IsShapeBurst(PsdDescriptor s) => (s.Items.GetValueOrDefault("Type") as string) is "shapeburst" or "ShpB";

    /// <summary>Returns a gradient's table in the document's space, or <see langword="null"/> for a noise gradient.</summary>
    private float[]? GradientTable(PsdDescriptor grad, PsdDescriptor owner)
    {
        if (PsdGradient.Read(grad, owner) is not { IsNoise: false } gradient)
        {
            return null;
        }

        float[] table = new float[(PsdGradient.Steps + 1) * _stride];
        gradient.Table(table, _channels, ColorOf, _channels == 3 && !_lab);
        if (owner.Flag("Rvrs") == true)
        {
            for (int i = 0, j = PsdGradient.Steps; i < j; i++, j--)
            {
                for (int k = 0; k < _stride; k++)
                {
                    (table[(i * _stride) + k], table[(j * _stride) + k]) = (table[(j * _stride) + k], table[(i * _stride) + k]);
                }
            }
        }

        return table;
    }

    /// <summary>Returns the painter of a gradient or pattern overlay (or stroke): the gradient over the layer's box, the pattern tiled from the canvas's origin.</summary>
    private Painter? OverlayPainter(Fx c, PsdEffect e)
    {
        PsdDescriptor s = e.Settings;
        if (e.Kind == PsdEffectKind.PatternOverlay || (s.Items.GetValueOrDefault("PntT") as string) == "Ptrn")
        {
            return PatternPainterOf(s);
        }

        (double Left, double Top, double Right, double Bottom) box = (s.Flag("Algn") ?? true) ? c.Box() : (0, 0, _width, _height);
        if (e.Kind == PsdEffectKind.Stroke && (s.Flag("Algn") ?? true))
        {
            // A stroke's gradient spans the stroke's own box: the shape's grown by the part outside it.
            float grow = (s.Items.GetValueOrDefault("Styl") as string) switch { "InsF" => 0, "CtrF" => 0.5f, _ => 1 } * (float)e.Pixels("Sz  ");
            box = (box.Left - grow, box.Top - grow, box.Right + grow, box.Bottom + grow);
        }

        return GradientPainterIn(s, box);
    }

    /// <summary>
    /// Blends an effect onto the target within the effect area: its colors (a painter's, or a gradient's looked up by the
    /// coverage, or by <paramref name="place"/> when given) times <paramref name="coverage"/>, in its mode and opacity.
    /// </summary>
    private void Blend(Fx c, float[]? coverage, Painter? painter, float[]? table, string modeKey, float opacity, float[] target, float jitter = 0, float[]? place = null)
    {
        Rect area = c.Area;
        BlendMode mode = PsdFile.BlendModeOf(modeKey) ?? BlendMode.SourceOver;
        if (PsdFile.BlendModeOf(modeKey) is null)
        {
            Skip("Psd.BlendModeUnknown", c.Pending.Info.Name);
        }

        bool special = mode is BlendMode.HardMix or BlendMode.VividLight;
        int w = area.Width;
        float[] colors = ArrayPool<float>.Shared.Rent(_stride * w);
        float[] source = ArrayPool<float>.Shared.Rent(_stride * w);
        try
        {
            for (int y = 0; y < area.Height; y++)
            {
                // No coverage plane: everywhere, within the shape's rows.
                ReadOnlySpan<float> cover = (coverage ?? c.Shape!).AsSpan(y * w, w);
                if (!cover.ContainsAnyExcept(0f))
                {
                    continue;
                }

                Span<float> col = colors.AsSpan(0, _stride * w);
                if (painter is not null)
                {
                    painter.Row(area.Y + y, area.X, col);
                }

                Span<float> s = source.AsSpan(0, _stride * w);
                for (int x = 0; x < w; x++)
                {
                    float a = coverage is null ? 1 : cover[x];
                    if (table is not null && a > 0)
                    {
                        // A gradient glow: the glow's strength picks the color (full strength the gradient's start).
                        float t = place is null ? 1 - a : place[(y * w) + x];
                        if (jitter > 0)
                        {
                            t += jitter * (Noise(area.X + x, area.Y + y, 7) - 0.5f);
                        }

                        float at = Math.Clamp(t, 0, 1) * PsdGradient.Steps;
                        int k = Math.Min((int)at, PsdGradient.Steps - 1);
                        float f = at - k;
                        for (int ch = 0; ch < _stride; ch++)
                        {
                            col[(_stride * x) + ch] = table[(k * _stride) + ch] + ((table[((k + 1) * _stride) + ch] - table[(k * _stride) + ch]) * f);
                        }

                        a = place is null ? col[(_stride * x) + _channels] : a * col[(_stride * x) + _channels];
                    }
                    else if (painter is not null)
                    {
                        a *= Math.Clamp(col[(_stride * x) + _channels], 0, 1);
                    }

                    for (int ch = 0; ch < _channels; ch++)
                    {
                        s[(_stride * x) + ch] = a > 0 ? col[(_stride * x) + ch] * a : 0;
                    }

                    s[(_stride * x) + _channels] = a;
                }

                Span<float> d = target.AsSpan(_stride * (((area.Y + y) * _width) + area.X), _stride * w);
                BlendRow(mode, special, s, d, opacity, 1, area.X, area.Y + y);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(colors);
            ArrayPool<float>.Shared.Return(source);
        }
    }

    /// <summary>A seeded value from 0 to 1 for a pixel, the same on every run: an integer hash of its place.</summary>
    private static float Noise(int x, int y, int salt)
    {
        uint h = (uint)x * 0x9E3779B1u;
        h ^= (uint)y * 0x85EBCA77u;
        h ^= (uint)salt * 0xC2B2AE3Du;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return (h >> 8) / 16777216f;
    }

    /// <summary>
    /// The planes one layer's effects are drawn with, the effect area's size: the layer's shape, its signed distance field
    /// (made when first needed), and the effects' coverages, all pooled and counted against the context's limits.
    /// </summary>
    private sealed class Fx(PsdFlattener owner, Rect area, EffectPending pending, PsdEffects effects)
    {
        private float[]? _distance;

        public Rect Area { get; } = area;

        public EffectPending Pending { get; } = pending;

        public float[]? Shape { get; set; }

        private int W => Area.Width;

        private int H => Area.Height;

        private int N => Area.Width * Area.Height;

        /// <summary>Gets the signed distance from each pixel to the shape's edge, positive outside.</summary>
        public float[] Distance
        {
            get
            {
                if (_distance is null)
                {
                    _distance = owner.Allocate(N);
                    MeasureDistance(_distance);
                }

                return _distance;
            }
        }

        /// <summary>
        /// Measures the distance field. Where the layer runs past the canvas, its edge out there counts too: the area grows (by
        /// the effects' reach at most) over the layer's bounds (its vector mask's, when it has one), which take the coverage of
        /// the canvas's edge pixels.
        /// </summary>
        private void MeasureDistance(float[] distance)
        {
            PsdLayerRecord record = Pending.Record;
            long x0 = record.Left, y0 = record.Top, right = record.Right, bottom = record.Bottom;
            if ((record.Blocks.TryGetValue("vmsk", out byte[]? path) || record.Blocks.TryGetValue("vsms", out path))
                && PsdVectorMask.Bounds(path, owner._width, owner._height) is { } bounds && bounds.Right > bounds.Left)
            {
                // A shape's own path, rather than its pixels' bounds, which take in its smoothed edge.
                x0 = (long)Math.Round(Math.Max(bounds.Left, int.MinValue));
                y0 = (long)Math.Round(Math.Max(bounds.Top, int.MinValue));
                right = (long)Math.Round(Math.Min(bounds.Right, int.MaxValue));
                bottom = (long)Math.Round(Math.Min(bounds.Bottom, int.MaxValue));
            }

            // How far the layer runs past each side of the canvas, and the padding: that, and a pixel past it, within reach.
            int reach = effects.Reach + 1;
            long[] past = [Area.X == 0 ? -x0 : 0, Area.Y == 0 ? -y0 : 0, Area.Right == owner._width ? right - owner._width : 0, Area.Bottom == owner._height ? bottom - owner._height : 0];
            int[] pad = new int[4];
            for (int k = 0; k < 4; k++)
            {
                pad[k] = past[k] > 0 ? (int)Math.Min(past[k] + 1, reach) : 0;
            }

            if (pad[0] + pad[1] + pad[2] + pad[3] == 0)
            {
                owner.SignedDistance(Shape!, W, H, distance);
                return;
            }

            int left = pad[0], top = pad[1], w = W + left + pad[2], h = H + top + pad[3];
            float[] shape = owner.Allocate((long)w * h), padded = owner.Allocate((long)w * h);
            try
            {
                for (int y = 0; y < h; y++)
                {
                    int sy = y - top;
                    bool rowIn = sy >= -past[1] && sy < H + past[3];
                    for (int x = 0; x < w; x++)
                    {
                        int sx = x - left;
                        bool inside = rowIn && sx >= -past[0] && sx < W + past[2];
                        shape[(y * w) + x] = inside ? Shape![(Math.Clamp(sy, 0, H - 1) * W) + Math.Clamp(sx, 0, W - 1)] : 0;
                    }
                }

                owner.SignedDistance(shape, w, h, padded);
                for (int y = 0; y < H; y++)
                {
                    padded.AsSpan(((y + top) * w) + left, W).CopyTo(distance.AsSpan(y * W, W));
                }
            }
            finally
            {
                owner.Release(shape);
                owner.Release(padded);
            }
        }

        public void Free(float[] plane)
        {
            if (!ReferenceEquals(plane, Shape))
            {
                owner.Release(plane);
            }
        }

        public void Release()
        {
            if (Shape is not null)
            {
                owner.Release(Shape);
            }

            if (_distance is not null)
            {
                owner.Release(_distance);
            }
        }

        /// <summary>
        /// Returns how much of the pixel at area index <paramref name="i"/> the layer's content leaves open (one minus its
        /// alpha with the fill opacity): what goes under the content shows through that much.
        /// </summary>
        public float Open(int i)
        {
            int x = Area.X + (i % W), y = Area.Y + (i / W);
            float alpha = Pending.Buffer![(owner._stride * ((y * owner._width) + x)) + owner._channels];
            return 1 - Math.Clamp(alpha * (effects.InteriorAsGroup ? Pending.Info.FillOpacity : 1), 0, 1);
        }

        /// <summary>Returns the layer's box for gradients aligned with it: its vector mask's bounds, else its shape's.</summary>
        public (double Left, double Top, double Right, double Bottom) Box()
        {
            if ((Pending.Record.Blocks.TryGetValue("vmsk", out byte[]? path) || Pending.Record.Blocks.TryGetValue("vsms", out path))
                && PsdVectorMask.Bounds(path, owner._width, owner._height) is { } bounds && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
            {
                return bounds;
            }

            int x0 = W, y0 = H, x1 = -1, y1 = -1;
            for (int y = 0; y < H; y++)
            {
                ReadOnlySpan<float> row = Shape.AsSpan(y * W, W);
                int first = row.IndexOfAnyExcept(0f);
                if (first < 0)
                {
                    continue;
                }

                int last = row.LastIndexOfAnyExcept(0f);
                (x0, x1, y0, y1) = (Math.Min(x0, first), Math.Max(x1, last), Math.Min(y0, y), y);
            }

            return x1 < 0 ? (0, 0, owner._width, owner._height) : (Area.X + x0, Area.Y + y0, Area.X + x1 + 1, Area.Y + y1 + 1);
        }

        /// <summary>
        /// A drop or inner shadow: the shape (its inverse inside) grown by the spread (choke), moved away from the light by the
        /// distance, blurred by the size, through the contour, with noise; a drop shadow is cut by the shape when the layer
        /// knocks it out, an inner shadow kept to the shape.
        /// </summary>
        public float[] Shadow(PsdEffect e)
        {
            PsdDescriptor s = e.Settings;
            bool inner = e.Kind == PsdEffectKind.InnerShadow;
            double size = e.Pixels("blur"), distance = e.Pixels("Dstn");
            double spread = Math.Clamp((s.Number("Ckmt") ?? 0) / 100, 0, 1);
            double angle = (s.Flag("uglg") ?? false ? effects.GlobalAngle : s.Number("lagl") ?? 120) * Math.PI / 180;
            float[] grown = Grown(spread * size, inner);
            float[] plane = owner.Allocate(N);
            Shift(grown, plane, -distance * Math.Cos(angle), distance * Math.Sin(angle), inner ? 1 : 0);
            owner.Release(grown);
            Blur(plane, W, H, size * (1 - spread) * EffectSigma);
            Finish(plane, PsdEffects.Contour(s.Get<PsdDescriptor>("TrnS")), (float)Math.Clamp((s.Number("Nose") ?? 0) / 100, 0, 1), 3);
            if (!inner && (s.Flag("layerConceals") ?? true))
            {
                // Knocked out: the shadow shows only outside the shape, the content inside, as if the two split the pixel; the
                // content then goes over the shadow, so the shadow is divided by what the content leaves open.
                for (int i = 0; i < N; i++)
                {
                    float open = Open(i);
                    plane[i] = open > 1e-6f ? Math.Min(plane[i] * (1 - Shape![i]) / open, 1) : 0;
                }
            }

            return plane;
        }

        /// <summary>
        /// An outer or inner glow: softer, the shape (its inverse inside, from the edge) grown by the spread (choke) and blurred
        /// by the size; precise, falling off linearly with the distance from the edge over the size; a center inner glow is its
        /// edge glow's inverse. Then the range, the contour and noise; an inner glow is kept to the shape.
        /// </summary>
        public float[] Glow(PsdEffect e)
        {
            PsdDescriptor s = e.Settings;
            bool inner = e.Kind == PsdEffectKind.InnerGlow;
            double size = Math.Max(e.Pixels("blur"), 0);
            double spread = Math.Clamp((s.Number("Ckmt") ?? 0) / 100, 0, 1);
            float[] plane;
            if ((s.Items.GetValueOrDefault("GlwT") as string) == "PrBL")
            {
                plane = owner.Allocate(N);
                float[] d = Distance;
                double fall = Math.Max(size * (1 - spread), 1e-3);
                for (int i = 0; i < N; i++)
                {
                    double out_ = inner ? -d[i] : d[i];
                    plane[i] = (float)Math.Clamp((size - out_) / fall, 0, 1);
                }
            }
            else
            {
                // Fitted to Photoshop's: grown a fifth of the size further, then blurred less than a shadow.
                plane = Grown((spread * size) + (0.2 * size * (1 - spread)), inner);
                Blur(plane, W, H, size * (1 - spread) * 0.3);
            }

            if (inner && (s.Items.GetValueOrDefault("glwS") as string) == "SrcC")
            {
                for (int i = 0; i < N; i++)
                {
                    plane[i] = 1 - plane[i];
                }
            }

            double range = Math.Clamp((s.Number("Inpr") ?? 50) / 100, 0.01, 1);
            for (int i = 0; i < N; i++)
            {
                plane[i] = (float)Math.Clamp(1 - ((1 - plane[i]) * 0.5 / range), 0, 1);
            }

            Finish(plane, PsdEffects.Contour(s.Get<PsdDescriptor>("TrnS")), (float)Math.Clamp((s.Number("Nose") ?? 0) / 100, 0, 1), 5);

            return plane;
        }

        /// <summary>
        /// Satin: the shape moved both ways along the angle by the distance, each blurred by the size, their difference through
        /// the contour (inverted when asked), kept to the shape.
        /// </summary>
        public float[] Satin(PsdEffect e)
        {
            PsdDescriptor s = e.Settings;
            double size = e.Pixels("blur"), distance = e.Pixels("Dstn");
            double angle = (s.Number("lagl") ?? 19) * Math.PI / 180;
            double dx = distance * Math.Cos(angle), dy = -distance * Math.Sin(angle);
            float[] a = owner.Allocate(N), b = owner.Allocate(N);
            Shift(Shape!, a, dx, dy, 0);
            Shift(Shape!, b, -dx, -dy, 0);
            Blur(a, W, H, size * EffectSigma);
            Blur(b, W, H, size * EffectSigma);
            float[] contour = PsdEffects.Contour(s.Get<PsdDescriptor>("MpgS"));
            bool invert = s.Flag("Invr") ?? true;
            for (int i = 0; i < N; i++)
            {
                float v = PsdEffects.Look(contour, Math.Abs(a[i] - b[i]));
                a[i] = invert ? 1 - v : v;
            }

            owner.Release(b);
            return a;
        }

        /// <summary>
        /// A stroke's coverage, the size wide outside the shape, inside it or centered on its edge, by the distance field: the
        /// part outside the shape (divided by what the content leaves open, as it goes under the content), or the part inside
        /// (<paramref name="inside"/>) as a share of the shape's coverage.
        /// </summary>
        public float[] Stroke(PsdEffect e, bool inside)
        {
            PsdDescriptor s = e.Settings;
            float size = (float)e.Pixels("Sz  ");
            string style = s.Items.GetValueOrDefault("Styl") as string ?? "OutF";
            float out_ = style switch
            {
                "InsF" => 0,
                "CtrF" => size / 2,
                _ => size,
            };
            float in_ = size - out_;
            float[] d = Distance;
            float[] plane = owner.Allocate(N);
            for (int i = 0; i < N; i++)
            {
                float shape = Shape![i];
                if (!inside)
                {
                    float open = Open(i);
                    float v = Math.Max(0, Math.Clamp(0.5f + out_ - d[i], 0, 1) - shape);
                    plane[i] = open > 1e-6f ? Math.Min(v / open, 1) : 0;
                }
                else
                {
                    // As a share of the shape's coverage.
                    plane[i] = shape > Empty ? Math.Clamp(1 - (Math.Clamp(0.5f - in_ - d[i], 0, 1) / shape), 0, 1) : 0;
                }
            }

            return plane;
        }

        /// <summary>Returns where across a stroke each pixel is, 0 at its outer edge to 1 at its inner, for a shape burst gradient.</summary>
        public float[] Burst(PsdEffect e, float[] stroke)
        {
            float size = Math.Max((float)e.Pixels("Sz  "), 1e-3f);
            string style = e.Settings.Items.GetValueOrDefault("Styl") as string ?? "OutF";
            float outer = style switch
            {
                "InsF" => 0,
                "CtrF" => size / 2,
                _ => size,
            };
            float[] d = Distance;
            float[] place = owner.Allocate(N);
            for (int i = 0; i < N; i++)
            {
                place[i] = stroke[i] > 0 ? Math.Clamp((outer - d[i]) / size, 0, 1) : 0;
            }

            return place;
        }

        /// <summary>
        /// Bevel and emboss: a height field from the distance to the edge over the size (inside for an inner bevel, outside for
        /// an outer one, across the edge for emboss, sinking at the edge for pillow emboss), rounded for the smooth technique,
        /// shaped by the contour, raised by the texture, scaled by the depth (down turns it over); lit from the angle and
        /// altitude, where it faces the light more than flat ground does is highlight, less is shadow, both through the gloss
        /// contour and softened.
        /// </summary>
        public (float[] Highlight, float[] Shadow) Bevel(PsdEffect e, bool inside)
        {
            PsdDescriptor s = e.Settings;
            double size = Math.Max(e.Pixels("blur"), 1e-3);
            string style = s.Items.GetValueOrDefault("bvlS") as string ?? "InrB";
            string technique = s.Items.GetValueOrDefault("bvlT") as string ?? "SfBL";
            double depth = Math.Clamp((s.Number("srgR") ?? 100) / 100, 0, 10) * ((s.Items.GetValueOrDefault("bvlD") as string) == "Out " ? -1 : 1);
            bool global = s.Flag("uglg") ?? false;
            double angle = (global ? effects.GlobalAngle : s.Number("lagl") ?? 120) * Math.PI / 180;
            double altitude = (global ? effects.GlobalAltitude : s.Number("Lald") ?? 30) * Math.PI / 180;
            float[]? shape = s.Flag("useShape") == true ? PsdEffects.Contour(s.Get<PsdDescriptor>("MpgS")) : null;
            float[] d = Distance;
            float[] height = owner.Allocate(N);
            for (int i = 0; i < N; i++)
            {
                double t = style switch
                {
                    "OtrB" => 1 - (d[i] / size),
                    "Embs" or "strokeEmboss" => 0.5 - (d[i] / size),
                    "PlEb" => Math.Abs(d[i]) * 2 / size,
                    _ => -d[i] / size,
                };
                t = Math.Clamp(t, 0, 1);
                if (technique == "SfBL")
                {
                    t = Math.Sin(t * Math.PI / 2);
                }

                if (shape is not null)
                {
                    t = PsdEffects.Look(shape, (float)t);
                }

                height[i] = (float)(t * size * depth);
            }

            if (technique == "Slmt")
            {
                Blur(height, W, H, 1);
            }
            else if (technique == "SfBL")
            {
                // The smooth technique rounds the field's ridges and steps a little further (fitted: as a shadow of the size is
                // blurred, two pixels at most).
                Blur(height, W, H, Math.Min(size * EffectSigma, 2));
            }

            if (s.Flag("useTexture") == true && owner.PatternPainterOf(s) is Painter texture)
            {
                Texture(height, texture, Math.Clamp((s.Number("textureDepth") ?? 100) / 100, -10, 10) * size * (s.Flag("InvT") == true ? -1 : 1));
            }

            float[] gloss = PsdEffects.Contour(s.Get<PsdDescriptor>("TrnS"));
            float[] highlight = owner.Allocate(N), shadow = owner.Allocate(N);
            double lx = Math.Cos(altitude) * Math.Cos(angle), ly = -Math.Cos(altitude) * Math.Sin(angle), lz = Math.Sin(altitude);
            float flat = PsdEffects.Look(gloss, (float)lz);
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W) + x;
                    double nx = -(height[(y * W) + Math.Min(x + 1, W - 1)] - height[(y * W) + Math.Max(x - 1, 0)]) / 2;
                    double ny = -(height[(Math.Min(y + 1, H - 1) * W) + x] - height[(Math.Max(y - 1, 0) * W) + x]) / 2;
                    double shade = ((nx * lx) + (ny * ly) + lz) / Math.Sqrt((nx * nx) + (ny * ny) + 1);
                    float v = PsdEffects.Look(gloss, (float)Math.Clamp(shade, 0, 1));
                    highlight[i] = flat < 1 ? Math.Max(0, v - flat) / (1 - flat) : 0;
                    shadow[i] = flat > 0 ? Math.Max(0, flat - v) / flat : 0;
                }
            }

            owner.Release(height);
            double soften = e.Pixels("Sftn");
            if (soften > 0)
            {
                Blur(highlight, W, H, soften * EffectSigma);
                Blur(shadow, W, H, soften * EffectSigma);
            }

            // Inside: as a share of the shape's coverage, for Atop; outside: what the shape leaves, under the content.
            for (int i = 0; i < N; i++)
            {
                float cover = Shape![i], open = Open(i);
                float region = inside
                    ? (style == "OtrB" || cover <= Empty ? 0 : 1)
                    : (style == "InrB" || open <= 1e-6f ? 0 : Math.Min((1 - cover) / open, 1));
                highlight[i] *= region;
                shadow[i] *= region;
            }

            return (highlight, shadow);
        }

        /// <summary>Raises the height field inside the shape by the texture's luminance about mid gray, times <paramref name="depth"/>.</summary>
        private void Texture(float[] height, Painter texture, double depth)
        {
            int stride = owner._stride;
            float[] row = ArrayPool<float>.Shared.Rent(stride * W);
            try
            {
                for (int y = 0; y < H; y++)
                {
                    texture.Row(Area.Y + y, Area.X, row.AsSpan(0, stride * W));
                    for (int x = 0; x < W; x++)
                    {
                        float lum = stride == 5
                            ? 1 - Math.Min(1, (0.3f * row[stride * x]) + (0.59f * row[(stride * x) + 1]) + (0.11f * row[(stride * x) + 2]) + row[(stride * x) + 3])
                            : owner._lab ? row[stride * x] : (0.3f * row[stride * x]) + (0.59f * row[(stride * x) + 1]) + (0.11f * row[(stride * x) + 2]);
                        height[(y * W) + x] += (float)(depth * (lum - 0.5) * Shape![(y * W) + x]);
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(row);
            }
        }

        /// <summary>Returns the shape (or its inverse) grown by <paramref name="by"/> pixels, as a new plane.</summary>
        private float[] Grown(double by, bool inverse)
        {
            float[] plane = owner.Allocate(N);
            if (by <= 0)
            {
                for (int i = 0; i < N; i++)
                {
                    plane[i] = inverse ? 1 - Shape![i] : Shape![i];
                }

                return plane;
            }

            float[] d = Distance;
            for (int i = 0; i < N; i++)
            {
                plane[i] = Math.Clamp(0.5f + (float)by + (inverse ? d[i] : -d[i]), 0, 1);
            }

            return plane;
        }

        /// <summary>Moves a plane by (dx, dy) pixels, between pixels by bilinear weights, <paramref name="outside"/> coming in from beyond its edges.</summary>
        private void Shift(float[] source, float[] target, double dx, double dy, float outside)
        {
            int ix = (int)Math.Floor(dx), iy = (int)Math.Floor(dy);
            float fx = (float)(dx - ix), fy = (float)(dy - iy);
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    // target(x, y) = source(x − dx, y − dy): between (x − ix − 1, ...) and (x − ix, ...).
                    int sx = x - ix, sy = y - iy;
                    float a = At(sx - 1, sy - 1), b = At(sx, sy - 1), c = At(sx - 1, sy), d = At(sx, sy);
                    float top = b + ((a - b) * fx), bottom = d + ((c - d) * fx);
                    target[(y * W) + x] = bottom + ((top - bottom) * fy);
                }
            }

            float At(int x, int y) => x >= 0 && y >= 0 && x < W && y < H ? source[(y * W) + x] : outside;
        }

        /// <summary>Puts a plane through a contour and adds seeded noise of <paramref name="noise"/> to it.</summary>
        private void Finish(float[] plane, float[] contour, float noise, int salt)
        {
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W) + x;
                    float v = PsdEffects.Look(contour, plane[i]);
                    if (noise > 0 && v > 0)
                    {
                        v = Math.Clamp(v * (1 + (noise * ((2 * Noise(Area.X + x, Area.Y + y, salt)) - 1))), 0, 1);
                    }

                    plane[i] = v;
                }
            }
        }
    }
}
