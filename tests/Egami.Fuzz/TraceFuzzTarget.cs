using System.Numerics;

using Egami.Drawing;
using Egami.Drawing.Tracing;
using Egami.Pixels;

namespace Egami.Fuzz;

/// <summary>
/// The trace fuzz target (Bitmap Tracing PR F): the first eight bytes pick the options, the rest is an image (any format Egami
/// reads) of at most 256 by 256 pixels, which is traced in each of the five modes with those options. Logo mode may refuse an
/// image that is not flat-color art with an <see cref="ArgumentException"/>; nothing else may throw, every point traced must be
/// finite and every curve lie near the image (within the corner reach and the fit tolerance; a centre-line dot's centre on
/// it). The result is then drawn back.
/// </summary>
internal static class TraceFuzzTarget
{
    private const int Head = 8;

    private static readonly EgamiContext s_context = EgamiContext.Default with
    {
        MaxParallelism = 1,
        Limits = SafetyLimits.Default with { MaxPixels = 256 * 256, MaxWidth = 256, MaxHeight = 256, MaxTotalBytes = 64L * 1024 * 1024 },
    };

    public static void Run(byte[] data)
    {
        if (data.Length <= Head)
        {
            return;
        }

        Raster<Rgba8> raster;
        try
        {
            using Raster loaded = Raster.Load(data.AsSpan(Head).ToArray(), s_context);
            raster = loaded.As<Rgba8>();
        }
        catch (EgamiException)
        {
            return;
        }

        using (raster)
        {
            ReadOnlySpan<byte> head = data.AsSpan(0, Head);
            foreach (TraceMode mode in Enum.GetValues<TraceMode>())
            {
                Trace(raster, Options(mode, head));
            }
        }
    }

    /// <summary>The options the head bytes pick, in <paramref name="mode"/>.</summary>
    internal static TraceOptions Options(TraceMode mode, ReadOnlySpan<byte> head)
    {
        byte a = head[0], b = head[1], c = head[2], d = head[3], e = head[4], f = head[5], g = head[6], h = head[7];
        Rgba8[]? palette = null;
        if ((h & 0x80) != 0)
        {
            palette = new Rgba8[1 + (h & 7)];
            for (int i = 0; i < palette.Length; i++)
            {
                byte v = (byte)((i * 97) + f);
                palette[i] = new Rgba8(v, (byte)(v ^ g), (byte)(v + e), 255);
            }
        }

        return new TraceOptions
        {
            Mode = mode,
            Threshold = (a & 1) != 0 ? b / 255f : null,
            PathFidelity = c / 255f,
            Corners = d / 255f,
            Noise = e % 40,
            IgnoreWhite = (a & 2) != 0,
            Colors = 2 + (f % 30),
            Palette = palette,
            Layering = (a & 4) != 0 ? TraceLayering.Abutting : TraceLayering.Stacked,
            SnapToLines = (a & 8) != 0,
            SnapToArcs = (a & 16) != 0,
            Smoothing = (a & 32) != 0 ? g / 255f : 0,
            StrokeWidth = (a & 64) != 0 ? 0.25f + (h & 31) : null,
        };
    }

    /// <summary>Traces the raster, checks every point, and draws the result back.</summary>
    internal static void Trace(Raster<Rgba8> raster, TraceOptions options)
    {
        TraceResult result;
        try
        {
            result = raster.Trace(options, s_context);
        }
        catch (ArgumentException) when (options.Mode == TraceMode.Logo)
        {
            // Logo mode refuses images that are not flat-color art, and too large a palette with transparency.
            return;
        }

        if (result.Width != raster.Width || result.Height != raster.Height)
        {
            throw new InvalidOperationException($"The result is {result.Width}x{result.Height}, the image {raster.Width}x{raster.Height}.");
        }

        foreach (TracedShape shape in result.Shapes)
        {
            if (!float.IsFinite(shape.StrokeWidth) || shape.StrokeWidth < 0)
            {
                throw new InvalidOperationException($"A stroke width of {shape.StrokeWidth}.");
            }

            foreach (Vector2 p in shape.Path.Points)
            {
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y))
                {
                    throw new InvalidOperationException($"A point at {p} in a {options.Mode} trace.");
                }
            }

            // Control points may lie off the image, but the curves stay near it: a restored corner moves at most 1.5 px from the
            // boundary, which lies on the image, and a fitted curve strays at most the fit tolerance from it (0.2 to 1.4 px as
            // PathFidelity goes from 1 to 0). A centre-line dot is a disc of the ink's area (or the set stroke width) around the
            // ink's centre, so only its centre need be on the image.
            RectF bounds = shape.Path.TightBounds;
            float tolerance = 0.2f + (1.2f * (1 - options.PathFidelity) * (1 - options.PathFidelity));
            float margin = 1.5f + tolerance;
            bool dot = options.Mode == TraceMode.Centerline && shape.Kind == TracedShapeKind.Fill;
            if (dot)
            {
                bounds = new RectF(bounds.Center.X, bounds.Center.Y, 0, 0);
            }

            if (shape.Path.Points.Length > 0
                && (bounds.Left < -margin || bounds.Top < -margin || bounds.Right > raster.Width + margin || bounds.Bottom > raster.Height + margin))
            {
                throw new InvalidOperationException($"A shape reaching {bounds} in a {raster.Width}x{raster.Height} trace ({options.Mode}).");
            }
        }

        using Raster<Rgba8> drawn = Raster.Create(Math.Max(1, raster.Width), Math.Max(1, raster.Height), Rgba8.White);
        using RasterCanvas<Rgba8> canvas = drawn.CreateCanvas();
        result.Draw(canvas);
    }
}
