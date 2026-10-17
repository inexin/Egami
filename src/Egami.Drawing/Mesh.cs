using System.Numerics;

using Egami.Colors;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>How a <see cref="Mesh"/> reads its vertices (or its indices, when it has them) as triangles.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="mesh"/>
/// </example>
public enum MeshMode
{
    /// <summary>Every three vertices are one triangle; a remainder of one or two is ignored.</summary>
    Triangles = 0,

    /// <summary>Each vertex after the second makes a triangle with the two before it.</summary>
    Strip = 1,

    /// <summary>Each vertex after the second makes a triangle with the one before it and the first.</summary>
    Fan = 2,
}

/// <summary>
/// Triangles with an optional color, texture coordinate or value at each vertex, for <see cref="Canvas.DrawMesh"/>: Gouraud-shaded
/// triangles, textured triangles (a paint mapped through the texture coordinates) and PDF's free-form and lattice-form shadings
/// (one value per vertex, mapped through gradient stops). Immutable: the constructor copies what it is given.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="mesh"/>
/// </example>
public sealed class Mesh
{
    private readonly Vector2[] _positions;
    private readonly RgbaF32[]? _colors;
    private readonly Vector2[]? _texCoords;
    private readonly float[]? _values;
    private readonly int[]? _indices;
    private InkColor[]? _inks;

    /// <summary>Initializes a new instance of the <see cref="Mesh"/> class.</summary>
    /// <param name="mode">How the vertices make triangles.</param>
    /// <param name="positions">The vertices, in the canvas's local coordinates.</param>
    /// <param name="colors">One straight-alpha color per vertex, or empty for none.</param>
    /// <param name="texCoords">One point in the paint's coordinates per vertex, or empty for none: the paint is mapped onto each triangle through them.</param>
    /// <param name="values">One value per vertex, or empty for none: interpolated, then mapped to a color through the stops given to <see cref="Canvas.DrawMesh"/>. A mesh has colors or values, not both.</param>
    /// <param name="indices">Indices into the vertices, read by <paramref name="mode"/> in place of the vertices themselves, or empty to read the vertices in order.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not defined, or an index is outside the vertices.</exception>
    /// <exception cref="ArgumentException">A per-vertex list's length differs from the positions', both colors and values are given, or a number is not finite.</exception>
    public Mesh(MeshMode mode, ReadOnlySpan<Vector2> positions, ReadOnlySpan<RgbaF32> colors = default, ReadOnlySpan<Vector2> texCoords = default, ReadOnlySpan<float> values = default, ReadOnlySpan<int> indices = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }

        Mode = mode;
        int n = positions.Length;
        foreach (Vector2 p in positions)
        {
            CheckPoint(p, nameof(positions));
        }

        CheckLength(colors.Length, n, nameof(colors));
        CheckLength(texCoords.Length, n, nameof(texCoords));
        CheckLength(values.Length, n, nameof(values));
        if (!colors.IsEmpty && !values.IsEmpty)
        {
            throw new ArgumentException("A mesh has colors or values, not both.", nameof(values));
        }

        foreach (RgbaF32 c in colors)
        {
            if (!(float.IsFinite(c.R) && float.IsFinite(c.G) && float.IsFinite(c.B) && float.IsFinite(c.A)))
            {
                throw new ArgumentException("A vertex color must be finite.", nameof(colors));
            }
        }

        foreach (Vector2 t in texCoords)
        {
            CheckPoint(t, nameof(texCoords));
        }

        foreach (float v in values)
        {
            if (!float.IsFinite(v))
            {
                throw new ArgumentException("A vertex value must be finite.", nameof(values));
            }
        }

        foreach (int i in indices)
        {
            if ((uint)i >= (uint)n)
            {
                throw new ArgumentOutOfRangeException(nameof(indices), i, "An index is outside the vertices.");
            }
        }

        _positions = positions.ToArray();
        _colors = colors.IsEmpty ? null : colors.ToArray();
        _texCoords = texCoords.IsEmpty ? null : texCoords.ToArray();
        _values = values.IsEmpty ? null : values.ToArray();
        _indices = indices.IsEmpty ? null : indices.ToArray();
        Bounds = n == 0 ? RectF.Empty : RectF.FromPoints(_positions);
        TriangleCount = Math.Max(0, mode == MeshMode.Triangles ? VertexReads / 3 : VertexReads - 2);
    }

    /// <summary>Gets how the vertices make triangles.</summary>
    public MeshMode Mode { get; }

    /// <summary>Gets the vertices.</summary>
    public ReadOnlyMemory<Vector2> Positions => _positions;

    /// <summary>Gets the vertex colors, or empty when there are none: for a mesh with inks, their appearances in sRGB.</summary>
    public ReadOnlyMemory<RgbaF32> Colors => _colors;

    /// <summary>Gets the vertex colours as inks, or empty when the mesh has none (see <see cref="WithInks"/>).</summary>
    public ReadOnlyMemory<InkColor> Inks => _inks;

    /// <summary>Gets the texture coordinates, or empty when there are none.</summary>
    public ReadOnlyMemory<Vector2> TexCoords => _texCoords;

    /// <summary>Gets the vertex values, or empty when there are none.</summary>
    public ReadOnlyMemory<float> Values => _values;

    /// <summary>Gets the indices, or empty when the vertices are read in order.</summary>
    public ReadOnlyMemory<int> Indices => _indices;

    /// <summary>Gets the bounds of the vertices.</summary>
    public RectF Bounds { get; }

    /// <summary>Gets the number of triangles the mode makes, degenerate ones included.</summary>
    public int TriangleCount { get; }

    private int VertexReads => _indices?.Length ?? _positions.Length;

    /// <summary>
    /// Returns this mesh with a colour given as inks for each vertex: an ink canvas blends their tints across each triangle (an ink
    /// a vertex does not name counts as 0 there); other canvases use the appearances in sRGB, which become <see cref="Colors"/>.
    /// </summary>
    /// <param name="inks">One colour per vertex.</param>
    /// <returns>The new mesh.</returns>
    /// <exception cref="ArgumentException">The count differs from the vertices', or the mesh has values.</exception>
    /// <exception cref="ArgumentNullException">A colour is <see langword="null"/>.</exception>
    public Mesh WithInks(ReadOnlySpan<InkColor> inks)
    {
        if (_values is not null)
        {
            throw new ArgumentException("A mesh has colors or values, not both.", nameof(inks));
        }

        RgbaF32[] colors = new RgbaF32[inks.Length];
        for (int i = 0; i < inks.Length; i++)
        {
            colors[i] = Drawing.Inks.Appearance(inks[i] ?? throw new ArgumentNullException(nameof(inks)));
        }

        Mesh mesh = new(Mode, _positions, colors, _texCoords, default, _indices);
        mesh._inks = inks.ToArray();
        return mesh;
    }

    /// <summary>Returns the vertex indices of triangle <paramref name="t"/>, in the order the mode gives them.</summary>
    internal (int A, int B, int C) Triangle(int t)
    {
        (int a, int b, int c) = Mode switch
        {
            MeshMode.Triangles => (3 * t, (3 * t) + 1, (3 * t) + 2),
            MeshMode.Strip => (t, t + 1, t + 2),
            _ => (0, t + 1, t + 2),
        };
        return _indices is null ? (a, b, c) : (_indices[a], _indices[b], _indices[c]);
    }

    private static void CheckLength(int length, int expected, string name)
    {
        if (length != 0 && length != expected)
        {
            throw new ArgumentException($"There must be one entry per vertex ({expected}), or none.", name);
        }
    }

    private static void CheckPoint(Vector2 p, string name)
    {
        if (!(float.IsFinite(p.X) && float.IsFinite(p.Y)))
        {
            throw new ArgumentException("The points must be finite.", name);
        }
    }
}
