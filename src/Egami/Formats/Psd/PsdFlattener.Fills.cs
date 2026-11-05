namespace Egami.Formats.Psd;

/// <summary>The flattener's fill layers: solid color (SoCo), gradient (GdFl) and pattern (PtFl) fills drawn afresh.</summary>
internal sealed partial class PsdFlattener
{
    /// <summary>
    /// Returns what draws a fill layer's colors, or <see langword="null"/> when it is no fill layer or one Egami cannot draw
    /// (a noise gradient, a pattern the document does not hold, damaged settings).
    /// </summary>
    private Painter? FillOf(PsdLayerRecord record)
    {
        if (FillColor(record) is float[] color)
        {
            return new SolidPainter(color);
        }

        if (record.Blocks.TryGetValue("GdFl", out byte[]? gradient) && PsdDescriptor.ReadVersioned(gradient) is PsdDescriptor settings)
        {
            return GradientPainterOf(settings, record);
        }

        if (record.Blocks.TryGetValue("PtFl", out byte[]? pattern) && PsdDescriptor.ReadVersioned(pattern) is PsdDescriptor fill)
        {
            return PatternPainterOf(fill);
        }

        return null;
    }

    /// <summary>
    /// Makes the painter of a pattern fill's settings: its pattern from the document's pattern table, tiled from the canvas's
    /// origin moved by the phase, scaled and turned by the angle.
    /// </summary>
    private PatternPainter? PatternPainterOf(PsdDescriptor settings)
    {
        if (settings.Get<PsdDescriptor>("Ptrn")?.Items.GetValueOrDefault("Idnt") is not string id)
        {
            return null;
        }

        Dictionary<string, PsdPattern> patterns = new(StringComparer.Ordinal);
        foreach (PsdLayerRecord holder in _file.Records)
        {
            foreach ((string key, byte[] block) in holder.Blocks)
            {
                if (key.StartsWith(PsdFile.PatternKeyPrefix, StringComparison.Ordinal))
                {
                    PsdPattern.Read(block, patterns);
                }
            }
        }

        if (!patterns.TryGetValue(id, out PsdPattern? pattern) || (long)pattern.Width * pattern.Height > 1L << 26)
        {
            return null;
        }

        // The decoded planes, the sRGB tile and the document-space tile count against the context's byte limit.
        int n = pattern.Width * pattern.Height;
        long bytes = (long)n * 4 * (4 + 4 + _stride);
        if (_bytes + bytes > _context.Limits.MaxTotalBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), _context.Limits.MaxTotalBytes, _bytes + bytes);
        }

        float[] rgba = new float[4 * n];
        if (!pattern.Decode(rgba, PsdLab.ToSrgb))
        {
            return null;
        }

        // The tile in the document's space: gray by luminosity, CMYK inks by the plain formula, encoded Lab.
        float[] tile = new float[_stride * n];
        bool gray = IsGray(_file);
        for (int i = 0; i < n; i++)
        {
            float r = rgba[4 * i], g = rgba[(4 * i) + 1], b = rgba[(4 * i) + 2];
            Span<float> pixel = tile.AsSpan(_stride * i, _stride);
            pixel[_channels] = rgba[(4 * i) + 3];
            if (_channels == 4)
            {
                float k = 1 - Math.Max(r, Math.Max(g, b));
                pixel[0] = k < 1 ? (1 - r - k) / (1 - k) : 0;
                pixel[1] = k < 1 ? (1 - g - k) / (1 - k) : 0;
                pixel[2] = k < 1 ? (1 - b - k) / (1 - k) : 0;
                pixel[3] = k;
            }
            else if (_lab)
            {
                (pixel[0], pixel[1], pixel[2]) = LabOf(r, g, b);
            }
            else if (gray)
            {
                pixel[0] = pixel[1] = pixel[2] = (0.3f * r) + (0.59f * g) + (0.11f * b);
            }
            else
            {
                (pixel[0], pixel[1], pixel[2]) = (r, g, b);
            }
        }

        PsdDescriptor? phase = settings.Get<PsdDescriptor>("phase");
        return new PatternPainter(
            tile,
            pattern.Width,
            pattern.Height,
            _stride,
            (phase?.Number("Hrzn") ?? 0, phase?.Number("Vrtc") ?? 0),
            Math.Max((settings.Number("Scl ") ?? 100) / 100, 1e-3),
            settings.Number("Angl") ?? 0);
    }

    /// <summary>Makes the painter of a gradient fill's settings: the gradient laid over the layer's box (its vector mask's bounds when aligned with the layer, else the canvas).</summary>
    private GradientPainter? GradientPainterOf(PsdDescriptor settings, PsdLayerRecord record)
    {
        (double left, double top, double right, double bottom) box = (0, 0, _width, _height);
        if ((settings.Flag("Algn") ?? true)
            && (record.Blocks.TryGetValue("vmsk", out byte[]? path) || record.Blocks.TryGetValue("vsms", out path))
            && PsdVectorMask.Bounds(path, _width, _height) is { } bounds
            && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
        {
            box = bounds;
        }

        return GradientPainterIn(settings, box);
    }

    /// <summary>Makes the painter of a gradient's settings (Grad, Type, Angl, Scl, Rvrs, Ofst and the method) over <paramref name="box"/>.</summary>
    private GradientPainter? GradientPainterIn(PsdDescriptor settings, (double Left, double Top, double Right, double Bottom) box)
    {
        if (PsdGradient.Read(settings.Get<PsdDescriptor>("Grad"), settings) is not { IsNoise: false } gradient)
        {
            return null;
        }

        float[] table = new float[(PsdGradient.Steps + 1) * _stride];
        gradient.Table(table, _channels, ColorOf, _channels == 3 && !_lab);
        return new GradientPainter(
            table,
            _stride,
            box,
            ShapeOf(settings.Items.GetValueOrDefault("Type") as string),
            settings.Number("Angl") ?? 90,
            (settings.Number("Scl ") ?? 100) / 100,
            settings.Flag("Rvrs") ?? false,
            settings.Get<PsdDescriptor>("Ofst") is PsdDescriptor offset ? ((offset.Number("Hrzn") ?? 0) / 100, (offset.Number("Vrtc") ?? 0) / 100) : (0, 0));
    }

    private static PsdGradientShape ShapeOf(string? key) => key switch
    {
        "Rdl " => PsdGradientShape.Radial,
        "Angl" => PsdGradientShape.Angle,
        "Rflc" => PsdGradientShape.Reflected,
        "Dmnd" => PsdGradientShape.Diamond,
        _ => PsdGradientShape.Linear,
    };

    /// <summary>Draws a fill layer's straight colors, a stride of color channels and alpha a pixel, row by row.</summary>
    private abstract class Painter
    {
        /// <summary>Writes the colors of canvas row <paramref name="y"/> from column <paramref name="x0"/> into <paramref name="colors"/>.</summary>
        public abstract void Row(int y, int x0, Span<float> colors);
    }

    /// <summary>One color everywhere.</summary>
    private sealed class SolidPainter(float[] color) : Painter
    {
        public override void Row(int y, int x0, Span<float> colors)
        {
            for (int at = 0; at < colors.Length; at += color.Length)
            {
                color.CopyTo(colors.Slice(at, color.Length));
            }
        }
    }

    /// <summary>
    /// A gradient over a box: centered on it (moved by the offset, a fraction of its size), at an angle counterclockwise from
    /// the right, reaching from the center to where that direction leaves the box, times the scale. Linear runs across twice
    /// that; radial, reflected and diamond run out from the center by it; angle turns once round the center.
    /// </summary>
    private sealed class GradientPainter : Painter
    {
        private readonly float[] _table;
        private readonly int _stride;
        private readonly PsdGradientShape _shape;
        private readonly double _cx, _cy, _cos, _sin, _reach;
        private readonly bool _reverse;

        public GradientPainter(float[] table, int stride, (double Left, double Top, double Right, double Bottom) box, PsdGradientShape shape, double angle, double scale, bool reverse, (double X, double Y) offset)
        {
            _table = table;
            _stride = stride;
            _shape = shape;
            _reverse = reverse;
            double w = box.Right - box.Left, h = box.Bottom - box.Top;
            _cx = ((box.Left + box.Right) / 2) + (offset.X * w);
            _cy = ((box.Top + box.Bottom) / 2) + (offset.Y * h);
            double radians = angle * Math.PI / 180;
            (_cos, _sin) = (Math.Cos(radians), Math.Sin(radians));
            double across = Math.Abs(_cos) > 1e-9 ? w / 2 / Math.Abs(_cos) : double.MaxValue;
            double down = Math.Abs(_sin) > 1e-9 ? h / 2 / Math.Abs(_sin) : double.MaxValue;
            _reach = Math.Max(Math.Min(across, down) * scale, 1e-6);
        }

        public override void Row(int y, int x0, Span<float> colors)
        {
            double py = _cy - y;
            for (int i = 0, at = 0; at < colors.Length; i++, at += _stride)
            {
                double px = x0 + i - _cx;
                double u = (px * _cos) + (py * _sin), v = (py * _cos) - (px * _sin);
                double t = _shape switch
                {
                    PsdGradientShape.Radial => Math.Sqrt((px * px) + (py * py)) / _reach,
                    PsdGradientShape.Reflected => Math.Abs(u) / _reach,
                    PsdGradientShape.Diamond => (Math.Abs(u) + Math.Abs(v)) / _reach,
                    PsdGradientShape.Angle => Turn(Math.Atan2(v, u)),
                    _ => (u / (2 * _reach)) + 0.5,
                };
                t = Math.Clamp(t, 0, 1);
                if (_reverse)
                {
                    t = 1 - t;
                }

                double x = t * PsdGradient.Steps;
                int k = Math.Min((int)x, PsdGradient.Steps - 1);
                float f = (float)(x - k);
                Span<float> a = _table.AsSpan(k * _stride, _stride), b = _table.AsSpan((k + 1) * _stride, _stride);
                for (int c = 0; c < _stride; c++)
                {
                    colors[at + c] = a[c] + ((b[c] - a[c]) * f);
                }
            }

            static double Turn(double radians)
            {
                double t = -radians / (2 * Math.PI);
                return t - Math.Floor(t);
            }
        }
    }

    /// <summary>
    /// A pattern tiled over the canvas from an origin: at 100 % and no angle pixel for pixel, else sampled between its pixels,
    /// scaled and turned counterclockwise.
    /// </summary>
    private sealed class PatternPainter(float[] tile, int width, int height, int stride, (double X, double Y) origin, double scale, double angle) : Painter
    {
        private readonly double _cos = Math.Cos(angle * Math.PI / 180), _sin = Math.Sin(angle * Math.PI / 180);
        private readonly bool _plain = Math.Abs(scale - 1) < 1e-9 && Math.Abs(angle % 360) < 1e-9;

        public override void Row(int y, int x0, Span<float> colors)
        {
            for (int i = 0, at = 0; at < colors.Length; i++, at += stride)
            {
                double dx = x0 + i - origin.X, dy = y - origin.Y;
                if (_plain)
                {
                    int px = Wrap((int)Math.Floor(dx), width), py = Wrap((int)Math.Floor(dy), height);
                    tile.AsSpan(stride * ((py * width) + px), stride).CopyTo(colors.Slice(at, stride));
                    continue;
                }

                // Into the pattern's own frame: turned back by the angle (y down), shrunk by the scale, sampled at pixel centers.
                double u = ((((dx + 0.5) * _cos) - ((dy + 0.5) * _sin)) / scale) - 0.5;
                double v = ((((dx + 0.5) * _sin) + ((dy + 0.5) * _cos)) / scale) - 0.5;
                int u0 = (int)Math.Floor(u), v0 = (int)Math.Floor(v);
                float fu = (float)(u - u0), fv = (float)(v - v0);
                int a = stride * ((Wrap(v0, height) * width) + Wrap(u0, width)), b = stride * ((Wrap(v0, height) * width) + Wrap(u0 + 1, width));
                int c = stride * ((Wrap(v0 + 1, height) * width) + Wrap(u0, width)), d = stride * ((Wrap(v0 + 1, height) * width) + Wrap(u0 + 1, width));
                for (int k = 0; k < stride; k++)
                {
                    float top = tile[a + k] + ((tile[b + k] - tile[a + k]) * fu), bottom = tile[c + k] + ((tile[d + k] - tile[c + k]) * fu);
                    colors[at + k] = top + ((bottom - top) * fv);
                }
            }
        }

        private static int Wrap(int v, int n) => ((v % n) + n) % n;
    }
}

/// <summary>A gradient's shape.</summary>
internal enum PsdGradientShape
{
    Linear,
    Radial,
    Angle,
    Reflected,
    Diamond,
}
