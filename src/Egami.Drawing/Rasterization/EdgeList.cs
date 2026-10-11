using System.Numerics;

namespace Egami.Drawing.Rasterization;

/// <summary>The straight edges of a flattened outline in device pixels, with their bounds. Reused from call to call.</summary>
internal sealed class EdgeList
{
    private const float Far = 1e30f;

    private float[] _coordinates = new float[256];

    /// <summary>Gets the number of edges.</summary>
    internal int Count { get; private set; }

    /// <summary>Gets the edges as x0, y0, x1, y1 quadruples.</summary>
    internal ReadOnlySpan<float> Coordinates => _coordinates.AsSpan(0, Count * 4);

    internal float MinX { get; private set; }

    internal float MinY { get; private set; }

    internal float MaxX { get; private set; }

    internal float MaxY { get; private set; }

    internal void Clear()
    {
        Count = 0;
        MinX = MinY = float.MaxValue;
        MaxX = MaxY = float.MinValue;
    }

    /// <summary>
    /// Adds an edge; horizontal ones are kept for bounds but add no coverage, so they are dropped. An edge with a NaN coordinate
    /// (arithmetic that overflowed on its way here) is dropped, and infinities are moved to a far finite value the rasterizer clips.
    /// </summary>
    internal void Add(Vector2 a, Vector2 b)
    {
        if (float.IsNaN(a.X) || float.IsNaN(a.Y) || float.IsNaN(b.X) || float.IsNaN(b.Y))
        {
            return;
        }

        a = Vector2.Clamp(a, new Vector2(-Far), new Vector2(Far));
        b = Vector2.Clamp(b, new Vector2(-Far), new Vector2(Far));
        MinX = MathF.Min(MinX, MathF.Min(a.X, b.X));
        MaxX = MathF.Max(MaxX, MathF.Max(a.X, b.X));
        MinY = MathF.Min(MinY, MathF.Min(a.Y, b.Y));
        MaxY = MathF.Max(MaxY, MathF.Max(a.Y, b.Y));
        if (a.Y == b.Y)
        {
            return;
        }

        if ((Count + 1) * 4 > _coordinates.Length)
        {
            Array.Resize(ref _coordinates, _coordinates.Length * 2);
        }

        Span<float> c = _coordinates.AsSpan(Count * 4, 4);
        c[0] = a.X;
        c[1] = a.Y;
        c[2] = b.X;
        c[3] = b.Y;
        Count++;
    }

    /// <summary>Gets the bounds of everything added, or an empty rectangle.</summary>
    internal RectF Bounds => MaxX >= MinX ? RectF.FromEdges(MinX, MinY, MaxX, MaxY) : RectF.Empty;
}
