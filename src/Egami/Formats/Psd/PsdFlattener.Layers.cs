using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Psd;

/// <summary>The flattener's per-layer work: rendering a layer's coverage and colors, its masks, blending ranges and the output.</summary>
internal sealed partial class PsdFlattener
{
    private float[]? _shape;
    private float[]? _maskRow;

    /// <summary>
    /// Renders a layer into <paramref name="buffer"/> (premultiplied color and alpha the canvas's size): its colors (its ink for
    /// CMYK) times its coverage and fill opacity, and its coverage alone into <paramref name="clip"/> for what is clipped to it.
    /// Returns the area it covers.
    /// </summary>
    private Rect Render(PsdLayer info, PsdLayerRecord record, Raster? pixels, InkRaster? ink, PsdMask? mask, float[] buffer, float[]? clip)
    {
        Rect canvas = new(0, 0, _width, _height);
        Rect area;
        float[]? solid = null;
        if (pixels is not null && _channels == 3)
        {
            area = Intersect(new Rect(record.Left, record.Top, pixels.Width, pixels.Height), canvas);
        }
        else if (ink is not null && _channels == 4)
        {
            area = Intersect(new Rect(record.Left, record.Top, ink.Width, ink.Height), canvas);
        }
        else if (FillColor(record) is float[] color)
        {
            solid = color;
            (int w, int h) = record.Size;
            area = w > 0 && h > 0 ? Intersect(new Rect(record.Left, record.Top, w, h), canvas) : canvas;
        }
        else
        {
            if (record.Has("GdFl") || record.Has("PtFl"))
            {
                Skip("Psd.FillSkipped", info.Name);
            }

            return default;
        }

        // A shape or fill layer's stored pixels already have its vector mask's edge; any other layer is cut by it here.
        bool vector = solid is not null || !(record.Has("SoCo") || record.Has("GdFl") || record.Has("PtFl") || record.Has("vscg"));
        float fill = info.FillOpacity;
        Span<float> colors = _row.AsSpan(0, _stride * area.Width);
        Span<float> shape = (_shape ??= new float[_width]).AsSpan(0, area.Width);
        for (int y = area.Y; y < area.Bottom; y++)
        {
            if (solid is not null)
            {
                for (int x = 0; x < area.Width; x++)
                {
                    solid.CopyTo(colors.Slice(_stride * x, _stride));
                }
            }
            else if (ink is not null)
            {
                InkRowOf(ink, y - record.Top, area.X - record.Left, colors);
            }
            else
            {
                pixels!.ReadRow(y - record.Top, area.X - record.Left, MemoryMarshal.Cast<float, RgbaF32>(colors));
            }

            MaskRow(record, mask, y, area.X, shape, vector);
            int row = y * _width;
            for (int x = 0; x < area.Width; x++)
            {
                Span<float> c = colors.Slice(_stride * x, _stride);
                float a = Math.Clamp(c[_channels], 0, 1) * shape[x];
                float content = a * fill;
                int at = _stride * (row + area.X + x);
                for (int k = 0; k < _channels; k++)
                {
                    buffer[at + k] = c[k] * content;
                }

                buffer[at + _channels] = content;
                if (clip is not null)
                {
                    clip[row + area.X + x] = a;
                }
            }
        }

        return area;
    }

    /// <summary>Reads a row of an ink raster as straight tints, four inks then alpha a pixel, 0 to 1.</summary>
    private static void InkRowOf(InkRaster ink, int y, int x0, Span<float> row)
    {
        int n = row.Length / 5;
        bool wide = ink.Depth == InkDepth.Unorm16;
        float full = wide ? 65535 : 255;
        for (int x = 0; x < n; x++)
        {
            float a = (wide ? ink.Alpha.Row16(y)[x0 + x] : ink.Alpha.Row8(y)[x0 + x]) / full;
            row[(5 * x) + 4] = a;
            for (int p = 0; p < 4; p++)
            {
                float v = (wide ? ink.Plates[p].Row16(y)[x0 + x] : ink.Plates[p].Row8(y)[x0 + x]) / full;
                row[(5 * x) + p] = a > 0 ? Math.Clamp(v / a, 0, 1) : 0;
            }
        }
    }

    /// <summary>
    /// Writes a row of a layer's mask coverage from canvas column <paramref name="x0"/>: its user mask (1 where disabled; the
    /// default color outside its bounds; thinned by its density) times its vector mask when <paramref name="vector"/>.
    /// </summary>
    private void MaskRow(PsdLayerRecord record, PsdMask? mask, int y, int x0, Span<float> shape, bool vector = true)
    {
        shape.Fill(1);
        // A shape layer's pixel mask that Photoshop rendered from its vector mask is already in its stored pixels.
        bool rendered = (record.Mask?.Flags & 0x08) != 0 && record.RealMask is null && (record.Has("SoCo") || record.Has("GdFl") || record.Has("PtFl") || record.Has("vscg")) && !vector;
        if (mask is { IsDisabled: false } && !rendered)
        {
            Rect bounds = mask.Bounds;
            float outside = mask.DefaultColor / 255f;
            float density = mask.Density;
            int from = Math.Clamp(bounds.X - x0, 0, shape.Length), to = Math.Clamp(bounds.Right - x0, 0, shape.Length);
            if (y < bounds.Y || y >= bounds.Bottom || from >= to)
            {
                shape.Fill(1 - (density * (1 - outside)));
            }
            else
            {
                shape[..from].Fill(1 - (density * (1 - outside)));
                shape[to..].Fill(1 - (density * (1 - outside)));
                Span<GrayF32> values = MemoryMarshal.Cast<float, GrayF32>((_maskRow ??= new float[_width]).AsSpan(0, to - from));
                mask.Pixels.ReadRow(y - bounds.Y, x0 + from - bounds.X, values);
                for (int i = 0; i < values.Length; i++)
                {
                    shape[from + i] = 1 - (density * (1 - Math.Clamp(values[i].L, 0, 1)));
                }
            }
        }

        if (vector)
        {
            VectorRow(record, y, x0, shape);
        }
    }

    /// <summary>Returns a fill layer's color (SoCo) straight in the document's space (RGB, or CMYK tints), alpha 1, or <see langword="null"/>.</summary>
    private float[]? FillColor(PsdLayerRecord record)
    {
        if (!record.Blocks.TryGetValue("SoCo", out byte[]? data) || PsdDescriptor.ReadVersioned(data)?.Get<PsdDescriptor>("Clr ") is not PsdDescriptor color)
        {
            return null;
        }

        if (_channels == 4)
        {
            if (color.ClassId == "CMYC")
            {
                return [(float)((color.Number("Cyn ") ?? 0) / 100), (float)((color.Number("Mgnt") ?? 0) / 100), (float)((color.Number("Ylw ") ?? 0) / 100), (float)((color.Number("Blck") ?? 0) / 100), 1];
            }
        }

        (double r, double g, double b) = color.ClassId switch
        {
            "Grsc" => Gray(1 - ((color.Number("Gry ") ?? 0) / 100)),
            "CMYC" => Cmyk(color.Number("Cyn ") ?? 0, color.Number("Mgnt") ?? 0, color.Number("Ylw ") ?? 0, color.Number("Blck") ?? 0),
            "HSBC" => Hsb(color.Number("H   ") ?? 0, color.Number("Strt") ?? 0, color.Number("Brgh") ?? 0),
            _ => ((color.Number("Rd  ") ?? 0) / 255, (color.Number("Grn ") ?? 0) / 255, (color.Number("Bl  ") ?? color.Number("Bl ") ?? 0) / 255),
        };
        if (IsGray(_file))
        {
            double v = color.ClassId == "Grsc" ? r : (0.3 * r) + (0.59 * g) + (0.11 * b);
            (r, g, b) = (v, v, v);
        }

        if (_channels == 4)
        {
            // RGB to inks by the plain formula Egami's CMYK pixels use.
            double k = 1 - Math.Max(r, Math.Max(g, b));
            double Ink(double v) => k < 1 ? Math.Clamp((1 - v - k) / (1 - k), 0, 1) : 0;
            return [(float)Ink(r), (float)Ink(g), (float)Ink(b), (float)k, 1];
        }

        return [(float)Math.Clamp(r, 0, 1), (float)Math.Clamp(g, 0, 1), (float)Math.Clamp(b, 0, 1), 1];

        static (double, double, double) Gray(double v) => (v, v, v);

        static (double, double, double) Cmyk(double c, double m, double y, double k) =>
            ((1 - (c / 100)) * (1 - (k / 100)), (1 - (m / 100)) * (1 - (k / 100)), (1 - (y / 100)) * (1 - (k / 100)));

        static (double, double, double) Hsb(double h, double s, double v)
        {
            s /= 100;
            v /= 100;
            double sector = ((h % 360) + 360) % 360 / 60;
            int i = (int)sector;
            double f = sector - i, p = v * (1 - s), q = v * (1 - (s * f)), t = v * (1 - (s * (1 - f)));
            return i switch
            {
                0 => (v, t, p),
                1 => (q, v, p),
                2 => (p, v, t),
                3 => (p, q, v),
                4 => (t, p, v),
                _ => (v, p, q),
            };
        }
    }

    private static bool IsGray(PsdFile file) => file.ColorChannels == 1;

    private static bool HasRanges(uint[] ranges) => Array.Exists(ranges, r => r != 0x0000FFFFu);

    /// <summary>
    /// Applies the blending ranges ("blend if") to premultiplied <paramref name="s"/>: each pixel's coverage times, for the
    /// composite gray and each channel, this layer's range on its own value and the underlying range on the backdrop's (values
    /// on Photoshop's 0 to 255 scale, ink amounts counted down from 255 for CMYK; between a split slider's two halves the weight
    /// ramps linearly).
    /// </summary>
    private void BlendIf(uint[] ranges, Span<float> s, ReadOnlySpan<float> d, int channels)
    {
        bool ink = _channels == 4;
        Span<float> cs = stackalloc float[4], cb = stackalloc float[4];
        for (int i = 0; i < s.Length; i += _stride)
        {
            float sa = s[i + _channels];
            if (sa <= 0)
            {
                continue;
            }

            float da = d[i + _channels];
            for (int c = 0; c < _channels; c++)
            {
                cs[c] = s[i + c] / sa;
                cb[c] = da > 0 ? d[i + c] / da : 0;
                if (ink)
                {
                    (cs[c], cb[c]) = (1 - cs[c], 1 - cb[c]);
                }
            }

            float weight = Range(ranges, 0, Gray(cs, ink)) * Range(ranges, 1, Gray(cb, ink));
            for (int c = 0; c < channels && weight > 0; c++)
            {
                weight *= Range(ranges, 2 + (2 * c), cs[c]) * Range(ranges, 3 + (2 * c), cb[c]);
            }

            if (weight < 1)
            {
                for (int c = 0; c < _stride; c++)
                {
                    s[i + c] *= weight;
                }
            }
        }

        // The composite gray: Rec. 601 luminosity, of the inks' complements through black for CMYK.
        static float Gray(ReadOnlySpan<float> c, bool ink) => ink
            ? ((0.3f * c[0]) + (0.59f * c[1]) + (0.11f * c[2])) * c[3]
            : (0.3f * c[0]) + (0.59f * c[1]) + (0.11f * c[2]);

        static float Range(uint[] ranges, int index, float value)
        {
            if (index >= ranges.Length)
            {
                return 1;
            }

            uint r = ranges[index];
            float v = Math.Clamp(value, 0, 1) * 255;
            float b0 = r >> 24, b1 = (r >> 16) & 0xFF, w0 = (r >> 8) & 0xFF, w1 = r & 0xFF;
            if (v < b0)
            {
                return 0;
            }

            if (v < b1)
            {
                return (v - b0) / (b1 - b0);
            }

            if (v <= w0)
            {
                return 1;
            }

            return v < w1 ? (w1 - v) / (w1 - w0) : 0;
        }
    }

    /// <summary>Returns the flattened canvas in the document's natural type: with alpha only when some pixel is not opaque.</summary>
    private Raster Output(float[] canvas)
    {
        int depth = _file.Depth;
        float full = depth == 8 ? 255 : depth == 16 ? 65535 : 1;
        bool opaque = true;
        for (int i = _channels, n = _stride * _width * _height; i < n; i += _stride)
        {
            if (MathF.Round(Math.Clamp(canvas[i], 0, 1) * (depth == 32 ? 65535 : full)) < (depth == 32 ? 65535 : full))
            {
                opaque = false;
                break;
            }
        }

        bool gray = IsGray(_file);
        if (_channels == 4)
        {
            // Opaque: the inks as Load gives CMYK; with transparency, RGBA by the plain formula, as Load does too.
            return (depth, opaque) switch
            {
                (8, true) => WriteInk<Cmyk8>(canvas),
                (8, false) => WriteInkAsRgba<Rgba8>(canvas),
                (_, true) => WriteInk<Cmyk16>(canvas),
                _ => WriteInkAsRgba<Rgba16>(canvas),
            };
        }

        return (depth, gray, opaque) switch
        {
            (8, true, true) => Write<Gray8>(canvas, 1, false, 255),
            (8, true, false) => Write<GrayAlpha8>(canvas, 1, true, 255),
            (8, false, true) => Write<Rgb8>(canvas, 3, false, 255),
            (8, false, false) => Write<Rgba8>(canvas, 3, true, 255),
            (16, true, true) => Write<Gray16>(canvas, 1, false, 65535),
            (16, true, false) => Write<GrayAlpha16>(canvas, 1, true, 65535),
            (16, false, true) => Write<Rgb16>(canvas, 3, false, 65535),
            (16, false, false) => Write<Rgba16>(canvas, 3, true, 65535),
            (_, true, true) => Write<GrayF32>(canvas, 1, false, 1),
            (_, false, true) => Write<RgbF32>(canvas, 3, false, 1),
            _ => Write<RgbaF32>(canvas, 3, true, 1),
        };
    }

    /// <summary>Writes the canvas, straight, into a raster of <paramref name="colors"/> channels (and alpha), as bytes, 16-bit words or floats.</summary>
    private Raster<TPixel> Write<TPixel>(float[] canvas, int colors, bool alpha, float full)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = Raster.Create<TPixel>(_width, _height, _context);
        int step = colors + (alpha ? 1 : 0);
        for (int y = 0; y < _height; y++)
        {
            Span<byte> bytes = MemoryMarshal.AsBytes(raster.Row(y));
            for (int x = 0; x < _width; x++)
            {
                int at = 4 * ((y * _width) + x);
                float a = Math.Clamp(canvas[at + 3], 0, 1);
                for (int c = 0; c <= colors; c++)
                {
                    if (c == colors && !alpha)
                    {
                        break;
                    }

                    float v = c == colors ? a : a > 0 ? canvas[at + c] / a : 0;
                    if (full == 1)
                    {
                        MemoryMarshal.Cast<byte, float>(bytes)[(x * step) + c] = c == colors ? a : v;
                    }
                    else if (full == 255)
                    {
                        bytes[(x * step) + c] = (byte)MathF.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);
                    }
                    else
                    {
                        MemoryMarshal.Cast<byte, ushort>(bytes)[(x * step) + c] = (ushort)MathF.Round(Math.Clamp(v, 0, 1) * 65535, MidpointRounding.AwayFromZero);
                    }
                }
            }
        }

        return raster;
    }

    /// <summary>Writes an ink canvas as CMYK pixels, the inks straight (premultiplied by an alpha of 1).</summary>
    private Raster<TPixel> WriteInk<TPixel>(float[] canvas)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = Raster.Create<TPixel>(_width, _height, _context);
        bool wide = typeof(TPixel) == typeof(Cmyk16);
        for (int y = 0; y < _height; y++)
        {
            Span<byte> bytes = MemoryMarshal.AsBytes(raster.Row(y));
            for (int x = 0; x < _width; x++)
            {
                int at = 5 * ((y * _width) + x);
                float a = Math.Clamp(canvas[at + 4], 0, 1);
                for (int p = 0; p < 4; p++)
                {
                    float v = a > 0 ? Math.Clamp(canvas[at + p] / a, 0, 1) : 0;
                    if (wide)
                    {
                        MemoryMarshal.Cast<byte, ushort>(bytes)[(4 * x) + p] = (ushort)MathF.Round(v * 65535, MidpointRounding.AwayFromZero);
                    }
                    else
                    {
                        bytes[(4 * x) + p] = (byte)MathF.Round(v * 255, MidpointRounding.AwayFromZero);
                    }
                }
            }
        }

        return raster;
    }

    /// <summary>Writes an ink canvas as straight RGBA by the plain CMYK formula: (1 − ink)(1 − black).</summary>
    private Raster<TPixel> WriteInkAsRgba<TPixel>(float[] canvas)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        float[] rgba = Allocate(4L * _width * _height);
        for (int i = 0, n = _width * _height; i < n; i++)
        {
            float a = Math.Clamp(canvas[(5 * i) + 4], 0, 1);
            float k = a > 0 ? Math.Clamp(canvas[(5 * i) + 3] / a, 0, 1) : 0;
            for (int c = 0; c < 3; c++)
            {
                float v = a > 0 ? Math.Clamp(canvas[(5 * i) + c] / a, 0, 1) : 0;
                rgba[(4 * i) + c] = (1 - v) * (1 - k) * a;
            }

            rgba[(4 * i) + 3] = a;
        }

        try
        {
            return Write<TPixel>(rgba, 3, true, typeof(TPixel) == typeof(Rgba8) ? 255 : 65535);
        }
        finally
        {
            Release(rgba);
        }
    }

    /// <summary>Returns the flattened ink canvas as an ink raster of the document's process inks, the planes premultiplied by alpha.</summary>
    private InkRaster OutputInk(float[] canvas, Colors.InkSet inks)
    {
        bool wide = _file.Depth == 16;
        InkRaster raster = new(inks, _width, _height, wide ? InkDepth.Unorm16 : InkDepth.Unorm8, _context);
        for (int y = 0; y < _height; y++)
        {
            for (int p = 0; p < 5; p++)
            {
                InkPlate plate = p < 4 ? raster.Plates[p] : raster.Alpha;
                for (int x = 0; x < _width; x++)
                {
                    float v = Math.Clamp(canvas[(5 * ((y * _width) + x)) + p], 0, 1);
                    if (wide)
                    {
                        plate.Row16(y)[x] = (ushort)MathF.Round(v * 65535, MidpointRounding.AwayFromZero);
                    }
                    else
                    {
                        plate.Row8(y)[x] = (byte)MathF.Round(v * 255, MidpointRounding.AwayFromZero);
                    }
                }
            }
        }

        return raster;
    }
}
