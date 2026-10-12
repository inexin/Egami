using System.Numerics;

using Egami.Drawing;
using Egami.Pixels;

namespace Egami.Svg.Resolving;

/// <summary>One patch of a mesh gradient: its 12 Coons boundary points clockwise from the top-left corner, and four corner colors.</summary>
internal readonly record struct SvgMeshPatch(Vector2[] Points, RgbaF32[] Colors);

/// <summary>
/// The geometry and color rules of SVG 2's mesh gradients (the 2016 Candidate Recommendation, as Inkscape writes them): patches are
/// Coons patches with corner colors, painted bilinearly, or bicubically with color slopes taken from the neighbouring corners and
/// zero cross slopes. A Coons patch is a bicubic tensor patch, sampled on a fine grid and drawn as one triangle mesh.
/// </summary>
internal static class SvgMesh
{
    /// <summary>How many rows and columns of cells each patch is sampled on.</summary>
    public const int Divisions = 16;

    /// <summary>The cubic edge of a stop's path from a start point: one c, C, l or L command; the end is <paramref name="end"/> when given.</summary>
    public static bool TryEdge(string? path, Vector2 start, Vector2? end, out Vector2[] edge)
    {
        edge = [];
        if (path is null)
        {
            return false;
        }

        ReadOnlySpan<char> text = path.AsSpan().Trim();
        if (text.IsEmpty || text[0] is not ('c' or 'C' or 'l' or 'L'))
        {
            return false;
        }

        char command = text[0];
        List<double> numbers = [];
        if (!Values.SvgNumbers.TryParseList(text[1..].ToString(), numbers))
        {
            return false;
        }

        bool relative = char.IsLower(command);
        Vector2 Point(int i) => numbers.Count >= (2 * i) + 2
            ? new Vector2((float)numbers[2 * i], (float)numbers[(2 * i) + 1]) + (relative ? start : Vector2.Zero)
            : Vector2.Zero;

        if (command is 'l' or 'L')
        {
            if (numbers.Count < 2 && end is null)
            {
                return false;
            }

            Vector2 to = end ?? Point(0);
            edge = [start, Vector2.Lerp(start, to, 1 / 3f), Vector2.Lerp(start, to, 2 / 3f), to];
            return true;
        }

        if (numbers.Count < 4 || (numbers.Count < 6 && end is null))
        {
            return false;
        }

        edge = [start, Point(0), Point(1), end ?? Point(2)];
        return true;
    }

    /// <summary>
    /// Draws the patches as one triangle mesh, so neighbouring patches meet without seams, and without antialiasing, as cairo
    /// draws Inkscape's meshes (the shape the mesh fills is antialiased itself): each patch is sampled on a grid of
    /// <see cref="Divisions"/> by <see cref="Divisions"/> cells, its corner colors interpolated bilinearly or bicubically.
    /// </summary>
    public static void Draw(Canvas canvas, List<List<SvgMeshPatch>> rows, bool bicubic)
    {
        const int n = Divisions;
        int patches = rows.Sum(r => r.Count);
        Vector2[] positions = new Vector2[patches * (n + 1) * (n + 1)];
        RgbaF32[] colors = new RgbaF32[positions.Length];
        int[] indices = new int[patches * n * n * 6];
        (RgbaF32[,] du, RgbaF32[,] dv) = bicubic ? Slopes(rows) : (new RgbaF32[0, 0], new RgbaF32[0, 0]);
        RgbaF32[] zero = new RgbaF32[4];
        int vertex = 0, index = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < rows[r].Count; c++)
            {
                SvgMeshPatch patch = rows[r][c];
                Vector2[] net = Tensor(patch.Points);
                RgbaF32[] cu = bicubic ? [du[r, c], du[r, c + 1], du[r + 1, c + 1], du[r + 1, c]] : zero;
                RgbaF32[] cv = bicubic ? [dv[r, c], dv[r, c + 1], dv[r + 1, c + 1], dv[r + 1, c]] : zero;
                int first = vertex;
                for (int j = 0; j <= n; j++)
                {
                    float v = (float)j / n;
                    for (int i = 0; i <= n; i++)
                    {
                        float u = (float)i / n;
                        positions[vertex] = Point(net, u, v);
                        colors[vertex++] = bicubic ? Bicubic(patch.Colors, cu, cv, u, v) : Bilinear(patch.Colors, u, v);
                    }
                }

                for (int j = 0; j < n; j++)
                {
                    for (int i = 0; i < n; i++)
                    {
                        int a = first + (j * (n + 1)) + i, b = a + 1, d = a + n + 1, e = d + 1;
                        indices[index++] = a;
                        indices[index++] = b;
                        indices[index++] = e;
                        indices[index++] = a;
                        indices[index++] = e;
                        indices[index++] = d;
                    }
                }
            }
        }

        canvas.DrawMesh(new Mesh(MeshMode.Triangles, positions, colors, indices: indices), new SolidPaint(new RgbaF32(1, 1, 1, 1)) { Antialias = false });
    }

    /// <summary>The point at (u, v) on a tensor patch.</summary>
    private static Vector2 Point(Vector2[] net, float u, float v)
    {
        Span<float> bu = [(1 - u) * (1 - u) * (1 - u), 3 * u * (1 - u) * (1 - u), 3 * u * u * (1 - u), u * u * u];
        Span<float> bv = [(1 - v) * (1 - v) * (1 - v), 3 * v * (1 - v) * (1 - v), 3 * v * v * (1 - v), v * v * v];
        Vector2 sum = Vector2.Zero;
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                sum += bv[row] * bu[col] * net[(row * 4) + col];
            }
        }

        return sum;
    }

    private static RgbaF32 Bilinear(RgbaF32[] k, float u, float v) =>
        Color(Vector4.Lerp(Vector4.Lerp(Vec(k[0]), Vec(k[1]), u), Vector4.Lerp(Vec(k[3]), Vec(k[2]), u), v));

    /// <summary>The 4×4 tensor net of a Coons patch, row by row from the top (PDF's conversion of shading type 6 to type 7).</summary>
    public static Vector2[] Tensor(ReadOnlySpan<Vector2> p)
    {
        Vector2[] n = new Vector2[16];
        for (int i = 0; i < 4; i++)
        {
            n[i] = p[i];
            n[(i * 4) + 3] = p[3 + i];
            n[12 + i] = p[9 - i];
        }

        n[4] = p[11];
        n[8] = p[10];
        Vector2 P(int row, int col) => n[(row * 4) + col];
        n[5] = ((-4 * P(0, 0)) + (6 * (P(0, 1) + P(1, 0))) - (2 * (P(0, 3) + P(3, 0))) + (3 * (P(3, 1) + P(1, 3))) - P(3, 3)) / 9;
        n[6] = ((-4 * P(0, 3)) + (6 * (P(0, 2) + P(1, 3))) - (2 * (P(0, 0) + P(3, 3))) + (3 * (P(3, 2) + P(1, 0))) - P(3, 0)) / 9;
        n[9] = ((-4 * P(3, 0)) + (6 * (P(3, 1) + P(2, 0))) - (2 * (P(3, 3) + P(0, 0))) + (3 * (P(0, 1) + P(2, 3))) - P(0, 3)) / 9;
        n[10] = ((-4 * P(3, 3)) + (6 * (P(3, 2) + P(2, 3))) - (2 * (P(3, 0) + P(0, 3))) + (3 * (P(0, 2) + P(2, 0))) - P(0, 0)) / 9;
        return n;
    }

    /// <summary>
    /// The color slopes along u and v at every corner of the grid: the mean of the two neighbouring differences, zero where they
    /// differ in sign, at most three times the smaller (Fritsch and Carlson's monotone rule); the one difference at the mesh's edge.
    /// </summary>
    private static (RgbaF32[,] Du, RgbaF32[,] Dv) Slopes(List<List<SvgMeshPatch>> rows)
    {
        int height = rows.Count, width = rows[0].Count;
        RgbaF32[,] grid = new RgbaF32[height + 1, width + 1];
        for (int r = 0; r < height; r++)
        {
            for (int c = 0; c < width; c++)
            {
                RgbaF32[] k = rows[r][c].Colors;
                grid[r, c] = k[0];
                grid[r, c + 1] = k[1];
                grid[r + 1, c + 1] = k[2];
                grid[r + 1, c] = k[3];
            }
        }

        RgbaF32[,] du = new RgbaF32[height + 1, width + 1], dv = new RgbaF32[height + 1, width + 1];
        for (int r = 0; r <= height; r++)
        {
            for (int c = 0; c <= width; c++)
            {
                du[r, c] = Slope(c > 0 ? grid[r, c - 1] : null, grid[r, c], c < width ? grid[r, c + 1] : null);
                dv[r, c] = Slope(r > 0 ? grid[r - 1, c] : null, grid[r, c], r < height ? grid[r + 1, c] : null);
            }
        }

        return (du, dv);
    }

    private static RgbaF32 Slope(RgbaF32? before, RgbaF32 at, RgbaF32? after)
    {
        Vector4 x = Vec(at);
        Vector4? left = before is RgbaF32 b ? x - Vec(b) : null;
        Vector4? right = after is RgbaF32 a ? Vec(a) - x : null;
        if (left is null || right is null)
        {
            Vector4 one = left ?? right ?? Vector4.Zero;
            return Color(one);
        }

        Vector4 l = left.Value, rr = right.Value, s = Vector4.Zero;
        for (int i = 0; i < 4; i++)
        {
            float p = l[i], q = rr[i];
            if (p * q > 0)
            {
                float mean = (p + q) / 2;
                float limit = 3 * Math.Min(Math.Abs(p), Math.Abs(q));
                s[i] = Math.Sign(mean) * Math.Min(Math.Abs(mean), limit);
            }
        }

        return Color(s);
    }

    /// <summary>The bicubic Hermite color at (u, v) in a patch, from its corner colors and slopes, cross slopes zero.</summary>
    private static RgbaF32 Bicubic(RgbaF32[] colors, RgbaF32[] du, RgbaF32[] dv, float u, float v)
    {
        static float H0(float t) => (((2 * t) - 3) * t * t) + 1;
        static float H1(float t) => (3 - (2 * t)) * t * t;
        static float G0(float t) => ((t - 2) * t * t) + t;
        static float G1(float t) => (t - 1) * t * t;

        float[] hu = [H0(u), H1(u), H1(u), H0(u)], hv = [H0(v), H0(v), H1(v), H1(v)];
        float[] gu = [G0(u), G1(u), G1(u), G0(u)], gv = [G0(v), G0(v), G1(v), G1(v)];
        Vector4 sum = Vector4.Zero;
        for (int k = 0; k < 4; k++)
        {
            sum += (hu[k] * hv[k] * Vec(colors[k])) + (gu[k] * hv[k] * Vec(du[k])) + (hu[k] * gv[k] * Vec(dv[k]));
        }

        return Color(Vector4.Clamp(sum, Vector4.Zero, Vector4.One));
    }

    private static Vector4 Vec(RgbaF32 c) => new(c.R, c.G, c.B, c.A);

    private static RgbaF32 Color(Vector4 v) => new(v.X, v.Y, v.Z, v.W);
}
