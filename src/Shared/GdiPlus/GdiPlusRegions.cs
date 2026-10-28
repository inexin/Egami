using Egami.Drawing;

namespace Egami.GdiPlus;

/// <summary>GDI+ region semantics: regions as paths in world units, with null for the infinite region.</summary>
internal static class GdiPlusRegions
{
    /// <summary>CombineModeReplace: the right region replaces the left.</summary>
    public const int Replace = 0;

    /// <summary>CombineModeIntersect.</summary>
    public const int Intersect = 1;

    /// <summary>CombineModeUnion.</summary>
    public const int Union = 2;

    /// <summary>CombineModeXor.</summary>
    public const int Xor = 3;

    /// <summary>CombineModeExclude: the left region less the right.</summary>
    public const int Exclude = 4;

    /// <summary>CombineModeComplement: the right region less the left.</summary>
    public const int Complement = 5;

    /// <summary>
    /// Combines two regions (null is infinite) by a GDI+ combine mode: 0 Replace, 1 Intersect, 2 Union, 3 Xor, 4 Exclude,
    /// 5 Complement. An infinite operand of Xor, Exclude or Complement stands for a square two million units wide.
    /// </summary>
    public static Path? Combine(Path? left, Path? right, int mode)
    {
        Path huge = Path.Rect(new RectF(-1e6f, -1e6f, 2e6f, 2e6f));
        Path l = left ?? huge, r = right ?? huge;
        return mode switch
        {
            Replace => right,
            Intersect => left is null ? right : right is null ? left : l.Intersect(r),
            Union => left is null || right is null ? null : l.Union(r),
            Xor => l.Xor(r),
            Exclude => l.Difference(r),
            Complement => r.Difference(l),
            _ => right,
        };
    }
}
