namespace Egami.Fonts.Cff;

/// <summary>
/// A CFF FontMatrix, [a b c d e f] mapping glyph space to text space as PostScript does: x' = a x + c y + e, y' = b x + d y + f.
/// </summary>
internal readonly record struct CffMatrix(double A, double B, double C, double D, double E, double F)
{
    /// <summary>The FontMatrix a CFF font has when its Top DICT gives none (Adobe Technical Note #5176, table 9).</summary>
    public static readonly CffMatrix Default = new(0.001, 0, 0, 0.001, 0, 0);

    public static readonly CffMatrix Identity = new(1, 0, 0, 1, 0, 0);

    /// <summary>Gets the matrix's linear scale, the square root of its determinant's size.</summary>
    public double Scale => Math.Sqrt(Math.Abs((A * D) - (B * C)));

    /// <summary>Gets whether the matrix is invertible and finite.</summary>
    public bool IsUsable => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F) && Scale > 1e-12;

    /// <summary>Gets whether the matrix maps every point to itself, within rounding.</summary>
    public bool IsIdentity => Near(A, 1) && Near(B, 0) && Near(C, 0) && Near(D, 1) && Near(E, 0) && Near(F, 0);

    /// <summary>Reads a DICT's FontMatrix operands, or null when there are not six usable numbers.</summary>
    public static CffMatrix? From(double[]? operands)
    {
        if (operands is not { Length: 6 })
        {
            return null;
        }

        var m = new CffMatrix(operands[0], operands[1], operands[2], operands[3], operands[4], operands[5]);
        return m.IsUsable ? m : null;
    }

    /// <summary>Applies this matrix, then <paramref name="next"/>.</summary>
    public CffMatrix Then(CffMatrix next) => new(
        (A * next.A) + (B * next.C),
        (A * next.B) + (B * next.D),
        (C * next.A) + (D * next.C),
        (C * next.B) + (D * next.D),
        (E * next.A) + (F * next.C) + next.E,
        (E * next.B) + (F * next.D) + next.F);

    public CffMatrix Times(double factor) => new(A * factor, B * factor, C * factor, D * factor, E * factor, F * factor);

    public CffMatrix Invert()
    {
        double det = (A * D) - (B * C);
        return new CffMatrix(D / det, -B / det, -C / det, A / det, ((C * F) - (D * E)) / det, ((B * E) - (A * F)) / det);
    }

    private static bool Near(double value, double target) => Math.Abs(value - target) < 1e-6;
}
