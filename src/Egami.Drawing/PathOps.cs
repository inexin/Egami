using Egami.Drawing.Geometry;

namespace Egami.Drawing;

/// <summary>How <see cref="Path.Op"/> and <see cref="PathOps.Combine"/> combine the areas of two paths: Skia's five operations.</summary>
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

/// <summary>Boolean operations on many paths at once.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="booleans"/>
/// </example>
public static class PathOps
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
    public static Path Combine(IEnumerable<Path> paths, PathOp op, float tolerance = DefaultTolerance)
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
