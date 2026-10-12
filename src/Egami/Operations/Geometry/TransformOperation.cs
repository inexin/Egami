using System.Buffers;
using System.Numerics;

using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Geometry;

/// <summary>
/// An affine or perspective transform. Each output pixel center maps back through the inverse matrix to a source point, sampled
/// with the plain kernel where nothing shrinks and by elliptical weighted averaging where it does (<see cref="FootprintSampler{TPixel}"/>).
/// A pure scale runs through the Resize engine and a pure rotation through Rotate's, so both equal those steps exactly.
/// </summary>
/// <param name="matrixFor">The forward matrix for an input of a given size (Skew and QuadDistort depend on the size).</param>
/// <param name="options">The sampling settings and viewport.</param>
/// <param name="curve">The linear-light curve the pipeline bound, for <see cref="ColorSpace.Linear"/>.</param>
internal sealed class TransformOperation(Func<Size, Homography> matrixFor, TransformOptions options, Colors.LinearCurve? curve = null)
    : IRasterOperation, INeedsNewPlane, IUpdatesMetadata, ILinearLight, IHoldsBuffers
{
    private Plan? _plan;

    /// <summary>Gets the options.</summary>
    internal TransformOptions Options => options;

    public bool UsesLinearLight => options.ColorSpace == ColorSpace.Linear;

    public IRasterOperation WithCurve(Colors.LinearCurve linear) => new TransformOperation(matrixFor, options, linear);

    public Size OutputSize(Size input)
    {
        return PlanFor(input).Output;
    }

    public bool NeedsNewPlane(Size input)
    {
        Plan plan = PlanFor(input);
        return plan.Inner is not IRasterOperation inner || (inner as INeedsNewPlane)?.NeedsNewPlane(input) != false;
    }

    public void ReleaseBuffers()
    {
        (_plan?.Inner as IHoldsBuffers)?.ReleaseBuffers();
    }

    public void UpdateMetadata(Metadata.RasterMetadata metadata, Size input, Size output)
    {
        Plan plan = PlanFor(input);
        ExifUpkeep.Upright(metadata);
        if (plan.Inner is IUpdatesMetadata inner)
        {
            inner.UpdateMetadata(metadata, input, output);
            return;
        }

        ExifUpkeep.Dimensions(metadata, output);
        ExifUpkeep.DropThumbnail(metadata);
        ExifUpkeep.Subject(metadata, SubjectMap.Transform(plan.Forward, plan.Offset, input, output), input);
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Plan plan = PlanFor(new Size(input.Source.Width, input.Source.Height));
        if (plan.Inner is IRasterOperation inner)
        {
            inner.Run(input);
            return;
        }

        SamplerSettings settings = new(options.Filter, options.Edges, options.Background, Curve);
        if (plan.Inverse.IsAffine)
        {
            RunAffine(input, plan, settings);
        }
        else
        {
            RunPerspective(input, plan, settings);
        }
    }

    private Colors.LinearCurve? Curve => options.ColorSpace == ColorSpace.Linear ? curve ?? Colors.LinearCurve.Srgb : null;

    /// <summary>
    /// Affine: the source position advances by a constant per output pixel and the footprint is the same everywhere, so it is
    /// found once.
    /// </summary>
    private static void RunAffine<TPixel>(OperationInput<TPixel> input, Plan plan, SamplerSettings settings)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Homography g = plan.Inverse.Normalized();
        Footprint footprint = settings.FootprintOf(g.M11, g.M21, g.M12, g.M22);
        PixelPlane<TPixel> source = input.Source;
        PixelPlane<TPixel> destination = input.Destination;
        RowConverter<TPixel> converter = new(settings.Curve);
        int width = destination.Width;
        TPixel fill = TPixel.FromRgba8(settings.Background);
        input.ForEachBand((start, end) =>
        {
            float[] rowBuffer = ArrayPool<float>.Shared.Rent(width * 4);
            try
            {
                FootprintSampler<TPixel> sampler = new(source, settings);
                Span<float> row = rowBuffer.AsSpan(0, width * 4);
                for (int y = start; y < end; y++)
                {
                    // Output pixel centers are at +0.5; source pixel i's center is at index i, hence the final −0.5.
                    double cy = y + 0.5;
                    double u0 = (0.5 * g.M11) + (cy * g.M21) + g.M31 - 0.5;
                    double v0 = (0.5 * g.M12) + (cy * g.M22) + g.M32 - 0.5;
                    if (settings.Taps == 1)
                    {
                        sampler.PointRow(u0, v0, g.M11, g.M12, destination.Row(y), fill);
                        continue;
                    }

                    if (!footprint.IsEwa)
                    {
                        sampler.KernelRow(u0, v0, g.M11, g.M12, row);
                        converter.Store(row, destination.Row(y), opaque: false);
                        continue;
                    }

                    for (int x = 0; x < width; x++)
                    {
                        Vector4 sum = sampler.Sample(u0 + (x * g.M11), v0 + (x * g.M12), footprint);
                        sum.CopyTo(row[(x * 4)..]);
                    }

                    converter.Store(row, destination.Row(y), opaque: false);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(rowBuffer);
            }
        });
    }

    /// <summary>
    /// Perspective: X, Y and W advance by constants along a row and one divide gives the source point; the footprint comes from
    /// the inverse's Jacobian at each pixel. Pixels whose W is not positive map to no source point and read the background.
    /// </summary>
    private static void RunPerspective<TPixel>(OperationInput<TPixel> input, Plan plan, SamplerSettings settings)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Homography g = plan.Inverse;
        PixelPlane<TPixel> source = input.Source;
        PixelPlane<TPixel> destination = input.Destination;
        RowConverter<TPixel> converter = new(settings.Curve);
        int width = destination.Width;
        input.ForEachBand((start, end) =>
        {
            float[] rowBuffer = ArrayPool<float>.Shared.Rent(width * 4);
            try
            {
                FootprintSampler<TPixel> sampler = new(source, settings);
                Span<float> row = rowBuffer.AsSpan(0, width * 4);
                for (int y = start; y < end; y++)
                {
                    double cy = y + 0.5;
                    double x0 = (0.5 * g.M11) + (cy * g.M21) + g.M31;
                    double y0 = (0.5 * g.M12) + (cy * g.M22) + g.M32;
                    double w0 = (0.5 * g.M13) + (cy * g.M23) + g.M33;
                    for (int x = 0; x < width; x++)
                    {
                        double w = w0 + (x * g.M13);
                        Vector4 sum;
                        if (w <= 0)
                        {
                            sum = sampler.Background;
                        }
                        else
                        {
                            double r = 1 / w;
                            double su = (x0 + (x * g.M11)) * r;
                            double sv = (y0 + (x * g.M12)) * r;
                            Footprint footprint = settings.Taps == 1
                                ? Footprint.Plain
                                : settings.FootprintOf((g.M11 - (su * g.M13)) * r, (g.M21 - (su * g.M23)) * r, (g.M12 - (sv * g.M13)) * r, (g.M22 - (sv * g.M23)) * r);
                            sum = sampler.Sample(su - 0.5, sv - 0.5, footprint);
                        }

                        sum.CopyTo(row[(x * 4)..]);
                    }

                    converter.Store(row, destination.Row(y), opaque: false);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(rowBuffer);
            }
        });
    }

    private Plan PlanFor(Size input)
    {
        if (_plan is Plan cached && cached.Input == input)
        {
            return cached;
        }

        Homography forward = matrixFor(input).Normalized();
        if (!forward.IsFinite() || !forward.TryInvert(out Homography inverse))
        {
            throw new ArgumentException("The transform's matrix is singular: it collapses the image onto a line or a point.");
        }

        Size output;
        (double X, double Y) offset;
        if (options.Viewport is Rect viewport)
        {
            output = viewport.Size;
            offset = (viewport.X, viewport.Y);
        }
        else
        {
            (double minX, double minY, double maxX, double maxY) = Bounds(forward, input);
            output = new Size(Dimension(maxX - minX, "width"), Dimension(maxY - minY, "height"));

            // The rounded canvas is centered on the exact bounds, as Rotate centers its canvas on the rotated image.
            offset = (((minX + maxX) / 2) - (output.Width / 2.0), ((minY + maxY) / 2) - (output.Height / 2.0));
        }

        // Output pixel (x, y) sits at destination point (x + offset.X, y + offset.Y) + the half-pixel the loops add.
        Homography outputToSource = Homography.CreateTranslation(offset.X, offset.Y) * inverse;
        _plan = new Plan(input, output, forward, outputToSource, offset, Shortcut(forward, input, output, offset));
        return _plan;
    }

    /// <summary>
    /// Returns the step that does this transform exactly, when one exists: Resize for a pure positive scale whose window lies
    /// inside the image, Rotate (or an exact quarter turn) for a pure rotation with the default canvas.
    /// </summary>
    private IRasterOperation? Shortcut(Homography forward, Size input, Size output, (double X, double Y) offset)
    {
        if (!forward.IsAffine)
        {
            return null;
        }

        // A pure scale and move: the output window maps back to an axis-aligned source window.
        if (forward.M12 == 0 && forward.M21 == 0 && forward.M11 > 0 && forward.M22 > 0)
        {
            double sx = (offset.X - forward.M31) / forward.M11;
            double sy = (offset.Y - forward.M32) / forward.M22;
            double sw = output.Width / forward.M11;
            double sh = output.Height / forward.M22;
            if (options.Viewport is null)
            {
                return new ResizeOperation(ResizeKind.Stretch, output.Width, output.Height, options.Filter, options.ColorSpace, curve: curve);
            }

            const double Tolerance = 1e-9;
            if (sx >= -Tolerance && sy >= -Tolerance && sx + sw <= input.Width + Tolerance && sy + sh <= input.Height + Tolerance)
            {
                if (output == input && Math.Abs(sx) <= Tolerance && Math.Abs(sy) <= Tolerance && Math.Abs(sw - input.Width) <= Tolerance && Math.Abs(sh - input.Height) <= Tolerance)
                {
                    return new ResizeOperation(ResizeKind.Stretch, output.Width, output.Height, options.Filter, options.ColorSpace, curve: curve);
                }

                return ResizeOperation.Window(Math.Max(0, sx), Math.Max(0, sy), Math.Min(sw, input.Width), Math.Min(sh, input.Height), output, options.Filter, options.ColorSpace, curve);
            }

            return null;
        }

        // A pure rotation with the default canvas, sampled as Rotate samples: Rotate's own kernel, so the pixels are identical.
        double cos = forward.M11;
        double sin = forward.M12;
        if (options.Viewport is null && options.Edges == EdgeMode.Transparent && options.ColorSpace == ColorSpace.Encoded
            && forward.M22 == cos && forward.M21 == -sin && Math.Abs((cos * cos) + (sin * sin) - 1) <= 1e-6)
        {
            // An angle within a float's rounding of a multiple of 1e-4 degrees snaps to it, so Matrix3x2.CreateRotation of 17°
            // (16.99999…°) rotates exactly as Rotate(17) does; other angles are kept as they are.
            double degrees = Math.Atan2(sin, cos) * 180 / Math.PI;
            if (degrees < 0)
            {
                degrees += 360;
            }

            double snapped = Math.Round(degrees, 4);
            if (Math.Abs(snapped - degrees) <= 2e-5)
            {
                degrees = snapped;
            }

            return degrees switch
            {
                0 or 360 => new ResizeOperation(ResizeKind.Stretch, input.Width, input.Height, options.Filter, options.ColorSpace),
                90 => RotateQuarterOperation.Clockwise,
                180 => new FlipOperation(horizontal: true, vertical: true),
                270 => RotateQuarterOperation.CounterClockwise,
                _ => new RotateOperation(degrees, options.Filter, options.Background),
            };
        }

        return null;
    }

    /// <summary>
    /// Returns the bounds of the transformed image: its four corners, which must all be in front of the viewer (W &gt; 0). W is
    /// linear across the image, so then the whole image is.
    /// </summary>
    internal static (double MinX, double MinY, double MaxX, double MaxY) Bounds(Homography forward, Size input)
    {
        ReadOnlySpan<(double X, double Y, string Name)> corners =
        [
            (0, 0, "top-left"), (input.Width, 0, "top-right"), (input.Width, input.Height, "bottom-right"), (0, input.Height, "bottom-left"),
        ];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach ((double x, double y, string name) in corners)
        {
            if (forward.W(x, y) <= 0)
            {
                throw new ArgumentException($"The perspective sends the image's {name} corner to infinity or behind the viewer, so its bounds are not finite; give the transform a Viewport.");
            }

            (double px, double py) = forward.Map(x, y);
            minX = Math.Min(minX, px);
            minY = Math.Min(minY, py);
            maxX = Math.Max(maxX, px);
            maxY = Math.Max(maxY, py);
        }

        return (minX, minY, maxX, maxY);
    }

    /// <summary>Rounds a bounds dimension to the nearest pixel, as Rotate does, and at least 1.</summary>
    private static int Dimension(double extent, string name)
    {
        if (!(extent < int.MaxValue / 2))
        {
            throw new ArgumentException($"The transformed image would be {extent:0} pixels in {name}; give the transform a Viewport.");
        }

        return Math.Max(1, (int)Math.Round(extent, MidpointRounding.AwayFromZero));
    }

    /// <summary>The transform for one input size.</summary>
    /// <param name="Input">The input size.</param>
    /// <param name="Output">The canvas size.</param>
    /// <param name="Forward">The forward matrix, scaled so M33 is 1.</param>
    /// <param name="Inverse">Maps output positions (pixel centers at +0.5) to continuous source positions.</param>
    /// <param name="Offset">The destination point of the canvas's top-left corner.</param>
    /// <param name="Inner">The exact step that does the work instead, if any.</param>
    private sealed record Plan(Size Input, Size Output, Homography Forward, Homography Inverse, (double X, double Y) Offset, IRasterOperation? Inner);
}
