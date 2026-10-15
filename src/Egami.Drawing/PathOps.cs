using Egami.Drawing.Geometry;

namespace Egami.Drawing;

/// <summary>How <see cref="Path.Op"/> and <see cref="Path.Combine"/> combine the areas of two paths: Skia's five operations.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="booleans"/>
/// </example>
public enum PathOp
{
    /// <summary>The first area without the second.</summary>
    Difference = 0,

    /// <summary>Where both areas are.</summary>
    Intersect = 1,

    /// <summary>Where either area is.</summary>
    Union = 2,

    /// <summary>Where exactly one of the areas is.</summary>
    Xor = 3,

    /// <summary>The second area without the first.</summary>
    ReverseDifference = 4,
}

/// <summary>The constants and checks of boolean operations.</summary>
internal static class PathOps
{
    /// <summary>The default tolerance of boolean operations, in path units: a twentieth of a pixel.</summary>
    internal const float DefaultTolerance = 0.05f;

    /// <summary>
    /// Combines any number of paths with one operation in a single pass, as if it were applied in order, left to right:
    /// ((a op b) op c) and so on. Far faster than chaining <see cref="Path.Op"/> for many paths, since every outline is cut and
    /// sorted once. The result is as <see cref="Path.Op"/> describes.
    /// </summary>
    /// <param name="paths">The paths; each keeps its own fill rule.</param>
    /// <param name="op">The operation.</param>
    /// <param name="tolerance">How close, in path units, points must be to count as the same, as <see cref="Path.Op"/> describes.</param>
    /// <returns>The combined path; empty when there are no paths.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="op"/> is not defined, or <paramref name="tolerance"/> is not positive and finite.</exception>
    internal static Path Combine(IEnumerable<Path> paths, PathOp op, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Check(op, tolerance);
        Path[] inputs = [.. paths];
        foreach (Path path in inputs)
        {
            ArgumentNullException.ThrowIfNull(path, nameof(paths));
        }

        return inputs.Length == 0 ? Path.Empty : PathBoolean.Run(inputs, op, tolerance);
    }

    internal static void Check(PathOp op, float tolerance)
    {
        if (!Enum.IsDefined(op))
        {
            throw new ArgumentOutOfRangeException(nameof(op), op, null);
        }

        if (!(tolerance > 0 && float.IsFinite(tolerance)))
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "The tolerance must be positive and finite.");
        }
    }
}

public sealed partial class Path
{

    /// <summary>
    /// Combines any number of paths with one operation in a single pass, as if it were applied in order, left to right:
    /// ((a op b) op c) and so on. Far faster than chaining <see cref="Op"/> for many paths, since every outline is cut and
    /// sorted once. The result is as <see cref="Op"/> describes.
    /// </summary>
    /// <param name="paths">The paths; each keeps its own fill rule.</param>
    /// <param name="op">The operation.</param>
    /// <param name="tolerance">How close, in path units, points must be to count as the same, as <see cref="Op"/> describes.</param>
    /// <returns>The combined path; empty when there are no paths.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="op"/> is not defined, or <paramref name="tolerance"/> is not positive and finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="booleans"/>
    /// </example>
    public static Path Combine(IEnumerable<Path> paths, PathOp op, float tolerance = PathOps.DefaultTolerance) => PathOps.Combine(paths, op, tolerance);
    /// <summary>
    /// Combines this path's area with another's. Each keeps its own fill rule; open contours count as closed, as fills close them.
    /// The result keeps the inputs' lines, quadratics and cubics, split exactly where outlines cross, so it is exact for PDF and SVG
    /// output; its outer contours run clockwise on screen and its holes counterclockwise, with the nonzero fill rule, and none of
    /// them cross.
    /// </summary>
    /// <param name="other">The other path.</param>
    /// <param name="op">The operation.</param>
    /// <param name="tolerance">
    /// How close, in path units, points must be to count as the same: crossings are found to well within it, and ends, crossings
    /// and curves closer than it merge, which keeps nearly touching or overlapping outlines from making slivers. A caller who will
    /// draw the result ten times larger passes a tenth of it.
    /// </param>
    /// <returns>The combined path.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="op"/> is not defined, or <paramref name="tolerance"/> is not positive and finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="booleans"/>
    /// </example>
    public Path Op(Path other, PathOp op, float tolerance = PathOps.DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(other);
        PathOps.Check(op, tolerance);
        return PathBoolean.Run([this, other], op, tolerance);
    }

    /// <summary>Returns the area of this path or the other, as <see cref="Op"/> describes.</summary>
    /// <param name="other">The other path.</param>
    /// <param name="tolerance">How close points must be to count as the same.</param>
    /// <returns>The union.</returns>
    public Path Union(Path other, float tolerance = PathOps.DefaultTolerance) => Op(other, PathOp.Union, tolerance);

    /// <summary>Returns the area of both this path and the other, as <see cref="Op"/> describes.</summary>
    /// <param name="other">The other path.</param>
    /// <param name="tolerance">How close points must be to count as the same.</param>
    /// <returns>The intersection.</returns>
    public Path Intersect(Path other, float tolerance = PathOps.DefaultTolerance) => Op(other, PathOp.Intersect, tolerance);

    /// <summary>Returns the area of this path without the other, as <see cref="Op"/> describes.</summary>
    /// <param name="other">The path to cut away.</param>
    /// <param name="tolerance">How close points must be to count as the same.</param>
    /// <returns>The difference.</returns>
    public Path Difference(Path other, float tolerance = PathOps.DefaultTolerance) => Op(other, PathOp.Difference, tolerance);

    /// <summary>Returns the area of exactly one of this path and the other, as <see cref="Op"/> describes.</summary>
    /// <param name="other">The other path.</param>
    /// <param name="tolerance">How close points must be to count as the same.</param>
    /// <returns>The exclusive or.</returns>
    public Path Xor(Path other, float tolerance = PathOps.DefaultTolerance) => Op(other, PathOp.Xor, tolerance);

    /// <summary>
    /// Returns the area grown outwards by a distance, or for a negative distance shrunk inwards: the union of the path and the
    /// outline of a pen twice the distance wide, or the difference. Corners grow by the join: round keeps the distance everywhere,
    /// miter keeps them sharp up to the miter limit. Open contours count as closed, as fills close them. The grown or cut parts
    /// are straight pieces within a hundredth of the distance (at most a tenth of a unit); the rest keeps its curves.
    /// </summary>
    /// <param name="distance">How far to grow; negative to shrink.</param>
    /// <param name="join">How grown corners look.</param>
    /// <param name="miterLimit">The longest miter, in multiples of twice the distance, as <see cref="Stroke.MiterLimit"/> defines it.</param>
    /// <returns>The offset path, simplified; empty when it shrinks away.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is not finite, <paramref name="join"/> is not defined, or <paramref name="miterLimit"/> is below 1.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="booleans"/>
    /// </example>
    public Path Inflate(float distance, LineJoin join = LineJoin.Round, float miterLimit = 4)
    {
        if (!float.IsFinite(distance))
        {
            throw new ArgumentOutOfRangeException(nameof(distance), distance, "The distance must be finite.");
        }

        Stroke pen = new(2 * MathF.Abs(distance)) { Join = join, MiterLimit = miterLimit };
        if (distance == 0)
        {
            return Simplify();
        }

        // Every point within the distance of the outline: each contour stroked open twice, starting at its first and at its second
        // point, so every corner gets its join. A closed contour's own stroke is two loops whose hole would stay empty where the
        // pen is wider than the shape.
        PathBuilder open = new();
        Segment.ReadContours(this, true, (segments, _, _, _) =>
        {
            for (int start = 0; start < Math.Min(2, segments.Count); start++)
            {
                open.MoveTo(segments[start].P0.ToVector2());
                for (int k = 0; k < segments.Count; k++)
                {
                    segments[(start + k) % segments.Count].AddTo(open);
                }
            }
        });

        Path ring = Stroker.Outline(open.ToPathUnchecked(), pen, Math.Clamp(MathF.Abs(distance) * 0.01f, 1e-4f, 0.1f));
        return PathBoolean.Run([this, ring], distance > 0 ? PathOp.Union : PathOp.Difference, PathOps.DefaultTolerance);
    }

    /// <summary>
    /// Returns the same area as this path, by its fill rule, as contours that neither overlap nor cross themselves or each other:
    /// outer contours clockwise on screen, holes counterclockwise, with the nonzero fill rule, curves kept as <see cref="Op"/> keeps
    /// them.
    /// </summary>
    /// <param name="tolerance">How close points must be to count as the same, as <see cref="Op"/> describes.</param>
    /// <returns>The simplified path.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tolerance"/> is not positive and finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="booleans"/>
    /// </example>
    public Path Simplify(float tolerance = PathOps.DefaultTolerance)
    {
        PathOps.Check(PathOp.Union, tolerance);
        return PathBoolean.Run([this], PathOp.Union, tolerance);
    }
}
