using System.Buffers;

using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Compose;

/// <summary>Blends one constant color over every pixel with a <see cref="BlendMode"/>, in place.</summary>
internal sealed class BlendColorOperation(Rgba8 color, BlendMode mode, float opacity, bool linear, Colors.LinearCurve? curve = null)
    : IRasterOperation, ILinearLight, Scopes.IRunsOnViews
{
    /// <summary>Gets the mode.</summary>
    internal BlendMode Mode => mode;

    public bool UsesLinearLight => linear;

    public IRasterOperation WithCurve(Colors.LinearCurve curve) => new BlendColorOperation(color, mode, opacity, linear, curve);

    public Size OutputSize(Size input)
    {
        return input;
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        OperationRows.CopyIfSeparate(input);
        if (mode == BlendMode.Destination || (opacity == 0 && BlendReference.KeepsDestinationAtZeroOpacity(mode)))
        {
            return;
        }

        BlendRows<TPixel> rows = new(mode, opacity, linear ? curve ?? Colors.LinearCurve.Srgb : null);
        input.ForEachBand((start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                rows.BlendSolid(color, input.Destination.Row(y), default, 0, y);
            }
        });
    }
}

/// <summary>
/// Draws another raster's first frame with the arithmetic composite of Skia's arithmetic blender and SVG's feComposite: every
/// premultiplied channel becomes k1·S·D + k2·S + k3·D + k4, then, with clamping, is kept in 0..1 with color no higher than alpha.
/// Only the overlap changes, as with DrawImage. The source is read as <see cref="RgbaF32"/>, so its full precision takes part.
/// </summary>
internal sealed class DrawArithmeticOperation(Raster image, Point position, float k1, float k2, float k3, float k4, bool clamp)
    : IRasterOperation, Scopes.IAreaTranslatable
{
    public IRasterOperation? InArea(Rect area)
    {
        return new DrawArithmeticOperation(image, new Point(position.X - area.X, position.Y - area.Y), k1, k2, k3, k4, clamp);
    }

    public Size OutputSize(Size input)
    {
        return input;
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        OperationRows.CopyIfSeparate(input);
        Rect area = new Rect(position.X, position.Y, image.Width, image.Height).Intersect(new Rect(0, 0, input.Destination.Width, input.Destination.Height));
        if (area.IsEmpty)
        {
            return;
        }

        Raster source = image.HasPlane(input.Source) || image.HasPlane(input.Destination) ? image.Derive(_ => { }) : image;
        try
        {
            RowConverter<TPixel> converter = new(null, inksAsRgb: true);
            RowConverter<RgbaF32> sourceConverter = new(null);
            int sx = area.X - position.X, sy = area.Y - position.Y;
            input.ForEachBand((start, end) =>
            {
                int from = Math.Max(start, area.Y), to = Math.Min(end, area.Bottom);
                if (from >= to)
                {
                    return;
                }

                RgbaF32[] row = ArrayPool<RgbaF32>.Shared.Rent(area.Width);
                float[] floats = ArrayPool<float>.Shared.Rent(8 * area.Width);
                try
                {
                    Span<float> s = floats.AsSpan(0, 4 * area.Width), d = floats.AsSpan(4 * area.Width, 4 * area.Width);
                    for (int y = from; y < to; y++)
                    {
                        source.ReadRow(sy + y - area.Y, sx, row.AsSpan(0, area.Width));
                        Span<TPixel> destination = input.Destination.Row(y).Slice(area.X, area.Width);
                        sourceConverter.Load(row.AsSpan(0, area.Width), s);
                        if (typeof(TPixel) == typeof(PremulBgra8))
                        {
                            // Premultiplied 8-bit rows load as stored; put the source in the same blue-first order.
                            for (int i = 0; i < s.Length; i += 4)
                            {
                                (s[i], s[i + 2]) = (s[i + 2], s[i]);
                            }
                        }

                        converter.Load(destination, d);
                        Combine(s, d);
                        converter.Store(d, destination, opaque: false);
                    }
                }
                finally
                {
                    ArrayPool<RgbaF32>.Shared.Return(row);
                    ArrayPool<float>.Shared.Return(floats);
                }
            });
        }
        finally
        {
            if (!ReferenceEquals(source, image))
            {
                source.Dispose();
            }
        }
    }

    /// <summary>Applies the arithmetic composite to premultiplied float pixels, writing into <paramref name="d"/>.</summary>
    internal void Combine(ReadOnlySpan<float> s, Span<float> d)
    {
        for (int i = 0; i < d.Length; i += 4)
        {
            for (int c = 0; c < 4; c++)
            {
                float sc = s[i + c], dc = d[i + c];
                d[i + c] = (k1 * sc * dc) + (k2 * sc) + (k3 * dc) + k4;
            }

            if (clamp)
            {
                float a = Math.Clamp(d[i + 3], 0f, 1f);
                d[i + 3] = a;
                d[i] = Math.Clamp(d[i], 0f, a);
                d[i + 1] = Math.Clamp(d[i + 1], 0f, a);
                d[i + 2] = Math.Clamp(d[i + 2], 0f, a);
            }
        }
    }
}

/// <summary>
/// Maps the four-character blend keys of Photoshop layer records to <see cref="BlendMode"/>, for 2.3.3's PSD layers. Keys
/// Egami has no mode for (Dissolve, the Linear, Vivid, Pin and Hard Mix lights, Darker and Lighter Color, Divide, pass-through)
/// map to <see langword="null"/>.
/// </summary>
internal static class PsdBlendKeys
{
    internal static BlendMode? ToBlendMode(string key)
    {
        return key switch
        {
            "norm" => BlendMode.SourceOver,
            "mul " => BlendMode.Multiply,
            "scrn" => BlendMode.Screen,
            "over" => BlendMode.Overlay,
            "dark" => BlendMode.Darken,
            "lite" => BlendMode.Lighten,
            "div " => BlendMode.ColorDodge,
            "idiv" => BlendMode.ColorBurn,
            "hLit" => BlendMode.HardLight,
            "sLit" => BlendMode.SoftLight,
            "diff" => BlendMode.Difference,
            "smud" => BlendMode.Exclusion,
            "lddg" => BlendMode.Add,
            "fsub" => BlendMode.Subtract,
            "hue " => BlendMode.Hue,
            "sat " => BlendMode.Saturation,
            "colr" => BlendMode.Color,
            "lum " => BlendMode.Luminosity,
            _ => null,
        };
    }
}
