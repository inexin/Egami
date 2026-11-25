using Egami.Operations;

namespace Egami.Formats.Psd;

/// <summary>The flattener's distance field: how far each pixel's center is from a shape's edge, for strokes, glows and bevels.</summary>
internal sealed partial class PsdFlattener
{
    /// <summary>Coverage at or under which a pixel counts as wholly outside a shape.</summary>
    private const float Empty = EdgeDistance.Empty;

    /// <summary>
    /// Writes the signed distance from each pixel's center to the edge of <paramref name="shape"/> into <paramref name="distance"/>
    /// (positive outside), by <see cref="EdgeDistance.Signed"/> with pooled scratch planes.
    /// </summary>
    private void SignedDistance(float[] shape, int width, int height, float[] distance)
    {
        int n = width * height;
        float[] squared = Allocate(n);
        float[] nearestRow = Allocate(n);
        try
        {
            EdgeDistance.Signed(shape, width, height, distance, squared, nearestRow);
        }
        finally
        {
            Release(squared);
            Release(nearestRow);
        }
    }
}
