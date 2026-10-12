using System.Numerics;
using System.Runtime.CompilerServices;

using Egami.Drawing;

namespace Egami.Svg.Rendering;

/// <summary>
/// A conservative rectangle, in a node's own coordinates, that holds everything the node draws: its geometry grown by the stroke
/// (miters and square caps included), its markers, and its children's filter regions. Layers are limited to it, so a faded or
/// blended element allocates and merges only its own area. <see langword="null"/> when it cannot be known (a non-scaling or
/// hairline stroke); the layer then covers the clip.
/// </summary>
internal static class SvgVisualBounds
{
    private static readonly ConditionalWeakTable<SvgNode, Box> s_cache = [];

    public static RectF? Of(SvgNode node) => s_cache.GetValue(node, n => new Box(Compute(n))).Value;

    /// <summary>The visual bounds in the parent's coordinates: the node's transform applied, or its filter region when it has one.</summary>
    private static RectF? InParent(SvgNode node)
    {
        RectF? own = node.Filter is SvgFilter filter ? filter.Region : Of(node);
        return own is RectF r ? Transform(r, node.Transform) : null;
    }

    private static RectF? Compute(SvgNode node)
    {
        switch (node)
        {
            case SvgPath path:
            {
                RectF bounds = path.Bounds;
                if (path.Stroke is Stroke stroke && path.StrokePaint is not null)
                {
                    if (path.NonScalingStroke || !(stroke.Width > 0))
                    {
                        return null;
                    }

                    float miter = stroke.Join == LineJoin.Miter ? Math.Max(stroke.MiterLimit, 1) : 1;
                    float reach = stroke.Width / 2 * Math.Max(miter, MathF.Sqrt(2));
                    bounds = bounds.Inflate(reach, reach);
                }
                else if (path.Fill is null)
                {
                    bounds = RectF.Empty;
                }

                if (path.Markers is SvgGroup markers)
                {
                    if (InParent(markers) is not RectF m)
                    {
                        return null;
                    }

                    bounds = Union(bounds, m);
                }

                return bounds;
            }

            case SvgGroup group:
            {
                RectF bounds = RectF.Empty;
                foreach (SvgNode child in group.Children)
                {
                    if (InParent(child) is not RectF b)
                    {
                        return null;
                    }

                    bounds = Union(bounds, b);
                }

                return group.ClipRect is RectF clip ? Intersect(bounds, clip) : bounds;
            }

            case SvgImage image:
                return image.ClipRect is RectF c ? Intersect(image.Destination, c) : image.Destination;
            default:
                return null;
        }
    }

    private static RectF Union(RectF a, RectF b) => a.Width <= 0 && a.Height <= 0 ? b : b.Width <= 0 && b.Height <= 0 ? a : a.Union(b);

    private static RectF Intersect(RectF a, RectF b)
    {
        float x0 = Math.Max(a.Left, b.Left), y0 = Math.Max(a.Top, b.Top), x1 = Math.Min(a.Right, b.Right), y1 = Math.Min(a.Bottom, b.Bottom);
        return x1 > x0 && y1 > y0 ? new RectF(x0, y0, x1 - x0, y1 - y0) : RectF.Empty;
    }

    private static RectF Transform(RectF r, Matrix3x2 m)
    {
        if (m.IsIdentity)
        {
            return r;
        }

        return RectF.FromPoints(
        [
            Vector2.Transform(new Vector2(r.Left, r.Top), m),
            Vector2.Transform(new Vector2(r.Right, r.Top), m),
            Vector2.Transform(new Vector2(r.Left, r.Bottom), m),
            Vector2.Transform(new Vector2(r.Right, r.Bottom), m),
        ]);
    }

    private sealed class Box(RectF? value)
    {
        public RectF? Value { get; } = value;
    }
}
