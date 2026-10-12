using System.Numerics;

namespace Egami.Svg.Values;

/// <summary>
/// The transform attribute (SVG 1.1 section 7.6): matrix, translate, scale, rotate (with an optional center), skewX and skewY,
/// separated by whitespace or commas, applied as written (the first function is outermost). Numbers carry no units.
/// </summary>
internal static class SvgTransforms
{
    public static bool TryParse(ReadOnlySpan<char> text, out Matrix3x2 matrix)
    {
        matrix = Matrix3x2.Identity;
        int i = 0;
        Span<double> args = stackalloc double[6];
        SvgNumbers.SkipSeparator(text, ref i);
        while (i < text.Length)
        {
            int nameStart = i;
            while (i < text.Length && char.IsAsciiLetter(text[i]))
            {
                i++;
            }

            ReadOnlySpan<char> name = text[nameStart..i];
            SvgNumbers.SkipSpace(text, ref i);
            if (i >= text.Length || text[i] != '(')
            {
                return false;
            }

            i++;
            int count = 0;
            SvgNumbers.SkipSpace(text, ref i);
            while (i < text.Length && text[i] != ')')
            {
                if (count == 6 || !SvgNumbers.TryRead(text, ref i, out args[count]))
                {
                    return false;
                }

                count++;
                SvgNumbers.SkipSeparator(text, ref i);
            }

            if (i >= text.Length)
            {
                return false;
            }

            i++;
            if (!TryFunction(name, args[..count], out Matrix3x2 m))
            {
                return false;
            }

            matrix = m * matrix;
            SvgNumbers.SkipSeparator(text, ref i);
        }

        return true;
    }

    private static bool TryFunction(ReadOnlySpan<char> name, ReadOnlySpan<double> a, out Matrix3x2 m)
    {
        m = Matrix3x2.Identity;
        switch (name)
        {
            case "matrix" when a.Length == 6:
                m = new Matrix3x2((float)a[0], (float)a[1], (float)a[2], (float)a[3], (float)a[4], (float)a[5]);
                return true;
            case "translate" when a.Length is 1 or 2:
                m = Matrix3x2.CreateTranslation((float)a[0], a.Length == 2 ? (float)a[1] : 0);
                return true;
            case "scale" when a.Length is 1 or 2:
                m = Matrix3x2.CreateScale((float)a[0], a.Length == 2 ? (float)a[1] : (float)a[0]);
                return true;
            case "rotate" when a.Length is 1 or 3:
                m = Rotate(a[0]);
                if (a.Length == 3)
                {
                    m = Matrix3x2.CreateTranslation((float)-a[1], (float)-a[2]) * m * Matrix3x2.CreateTranslation((float)a[1], (float)a[2]);
                }

                return true;
            case "skewX" when a.Length == 1:
                m = new Matrix3x2(1, 0, (float)Math.Tan(a[0] * Math.PI / 180), 1, 0, 0);
                return true;
            case "skewY" when a.Length == 1:
                m = new Matrix3x2(1, (float)Math.Tan(a[0] * Math.PI / 180), 0, 1, 0, 0);
                return true;
            default:
                return false;
        }
    }

    /// <summary>A rotation by degrees, exact at multiples of 90 so right angles stay pixel-aligned.</summary>
    public static Matrix3x2 Rotate(double degrees)
    {
        double turns = degrees % 360;
        if (turns < 0)
        {
            turns += 360;
        }

        (float sin, float cos) = turns switch
        {
            0 => (0f, 1f),
            90 => (1f, 0f),
            180 => (0f, -1f),
            270 => (-1f, 0f),
            _ => ((float)Math.Sin(degrees * Math.PI / 180), (float)Math.Cos(degrees * Math.PI / 180)),
        };
        return new Matrix3x2(cos, sin, -sin, cos, 0, 0);
    }
}
