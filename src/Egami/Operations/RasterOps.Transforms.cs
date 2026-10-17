using System.Numerics;

using Egami.Operations.Geometry;

namespace Egami.Operations;

/// <content>Affine and perspective transforms (Phase 2.5.2, stage A).</content>
public sealed partial class RasterOps
{
    /// <summary>
    /// Transforms the image by an affine matrix that maps source positions to destination positions, as
    /// <see cref="Matrix3x2"/>'s factories build them (rotation is clockwise on screen). Pixels have the origin at the image's
    /// top-left corner and their centers at +0.5. The result is the transformed image's bounding box unless
    /// <see cref="TransformOptions.Viewport"/> picks a rectangle; a pure scale equals <see cref="Resize"/> and a pure rotation
    /// equals <see cref="Rotate"/>.
    /// </summary>
    /// <param name="matrix">The forward matrix.</param>
    /// <param name="options">The filter, edges, background and viewport, or <see langword="null"/> for the defaults.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The matrix is singular or not finite, or the viewport is empty.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Transforms.cs" region="transform"/>
    /// </example>
    public RasterOps Transform(Matrix3x2 matrix, TransformOptions? options = null)
    {
        return Transform(Homography.FromAffine(matrix), options);
    }

    /// <summary>
    /// Transforms the image by a perspective (or affine) matrix that maps source positions to destination positions. Where the
    /// image shrinks or tilts away, each output pixel averages the ellipse its footprint covers in the source, so fine detail
    /// fades instead of aliasing.
    /// </summary>
    /// <param name="matrix">The forward matrix.</param>
    /// <param name="options">The filter, edges, background and viewport, or <see langword="null"/> for the defaults.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">
    /// The matrix is singular or not finite, or the viewport is empty. Without a viewport, the chain also throws when it is planned
    /// if the perspective sends a corner of the image to infinity or behind the viewer, naming the corner.
    /// </exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Transforms.cs" region="transform"/>
    /// </example>
    public RasterOps Transform(Homography matrix, TransformOptions? options = null)
    {
        CheckMatrix(matrix, nameof(matrix));
        options = CheckOptions(options);
        Homography forward = matrix;
        return Add(new TransformOperation(_ => forward, options));
    }

    /// <summary>
    /// Transforms the image by a 3D matrix applied to the image's plane at z = 0 and projected, as CSS <c>matrix3d</c> and Skia's
    /// <c>SkM44</c> draw a flat image; see <see cref="Homography.FromMatrix4x4"/>.
    /// </summary>
    /// <param name="matrix">The 3D matrix, in <see cref="Matrix4x4"/>'s row-vector convention.</param>
    /// <param name="options">The filter, edges, background and viewport, or <see langword="null"/> for the defaults.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The projected matrix is singular or not finite, or the viewport is empty.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Transforms.cs" region="transform"/>
    /// </example>
    public RasterOps Transform(Matrix4x4 matrix, TransformOptions? options = null)
    {
        return Transform(Homography.FromMatrix4x4(matrix), options);
    }

    /// <summary>
    /// Shears the image about its center: vertical lines lean by <paramref name="degreesX"/> (x moves by y × tan) and horizontal
    /// lines by <paramref name="degreesY"/>. The canvas grows to the sheared bounds unless a viewport is given.
    /// </summary>
    /// <param name="degreesX">The horizontal shear angle, strictly between −90 and 90.</param>
    /// <param name="degreesY">The vertical shear angle, strictly between −90 and 90.</param>
    /// <param name="options">The filter, edges, background and viewport, or <see langword="null"/> for the defaults.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An angle is not finite or not strictly between −90 and 90.</exception>
    /// <exception cref="ArgumentException">The two shears together collapse the image (tan x × tan y = 1), or the viewport is empty.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Transforms.cs" region="skew"/>
    /// </example>
    public RasterOps Skew(float degreesX, float degreesY, TransformOptions? options = null)
    {
        CheckShear(degreesX, nameof(degreesX));
        CheckShear(degreesY, nameof(degreesY));
        double ax = degreesX * Math.PI / 180;
        double ay = degreesY * Math.PI / 180;
        CheckMatrix(Homography.CreateSkew(ax, ay), "degreesX");
        options = CheckOptions(options);
        return Add(new TransformOperation(size => Homography.CreateSkew(ax, ay, new Vector2(size.Width / 2f, size.Height / 2f)), options));
    }

    /// <summary>
    /// Moves the image's four corners to four points, bending it in perspective: ImageMagick's Perspective
    /// distort. Corners are pixel edges, so the top-right corner of a 100-pixel-wide image is (100, 0).
    /// </summary>
    /// <param name="topLeft">Where the top-left corner goes.</param>
    /// <param name="topRight">Where the top-right corner goes.</param>
    /// <param name="bottomRight">Where the bottom-right corner goes.</param>
    /// <param name="bottomLeft">Where the bottom-left corner goes.</param>
    /// <param name="options">The filter, edges, background and viewport, or <see langword="null"/> for the defaults.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">A point is not finite, three of them lie on one line, or the viewport is empty.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Transforms.cs" region="quad-distort"/>
    /// </example>
    public RasterOps QuadDistort(Vector2 topLeft, Vector2 topRight, Vector2 bottomRight, Vector2 bottomLeft, TransformOptions? options = null)
    {
        Vector2[] points = [topLeft, topRight, bottomRight, bottomLeft];
        _ = Homography.FromQuad(new Rect(0, 0, 1, 1), points);
        options = CheckOptions(options);
        return Add(new TransformOperation(size => Homography.FromQuad(new Rect(0, 0, size.Width, size.Height), points), options));
    }

    private static void CheckMatrix(Homography matrix, string name)
    {
        if (!matrix.IsFinite() || !matrix.TryInvert(out _))
        {
            throw new ArgumentException("The matrix must be finite and invertible; a singular matrix collapses the image onto a line or a point.", name);
        }
    }

    private static TransformOptions CheckOptions(TransformOptions? options)
    {
        options ??= new TransformOptions();
        if (options.Viewport is Rect { IsEmpty: true } viewport)
        {
            throw new ArgumentException($"The viewport {viewport} is empty.", nameof(options));
        }

        return options;
    }

    private static void CheckShear(float degrees, string name)
    {
        if (!float.IsFinite(degrees) || degrees <= -90 || degrees >= 90)
        {
            throw new ArgumentOutOfRangeException(name, degrees, "A shear angle must be strictly between −90 and 90 degrees.");
        }
    }
}
