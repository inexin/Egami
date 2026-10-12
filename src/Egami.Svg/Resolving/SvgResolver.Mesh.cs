using System.Numerics;

using Egami.Css;
using Egami.Drawing;
using Egami.Pixels;
using Egami.Svg.Reading;
using Egami.Svg.Styling;
using Egami.Svg.Values;

namespace Egami.Svg.Resolving;

internal sealed partial class SvgResolver
{
    /// <summary>
    /// A meshgradient (question 10, the mesh Inkscape writes) as a picture of Coons patches: rows of meshpatch elements whose stops
    /// each give one edge (a c, C, l or L path from the end of the edge before) and the color of the corner it starts at. Edges
    /// and corners shared with the patch to the left or above come from that patch; outside the patches the paint is transparent.
    /// </summary>
    private PaintResult Mesh(SvgElement mesh, RectF bounds, in SvgContext context, out Paint? paint)
    {
        paint = null;
        List<SvgElement> chain = Chain(mesh, "meshgradient");
        SvgElement? source = chain.Find(e => e.Elements("meshrow").Any());
        if (source is null)
        {
            return PaintResult.None;
        }

        bool boundingBox = Inherited(chain, "gradientUnits") is not "userSpaceOnUse";
        if (boundingBox && (bounds.Width <= 0 || bounds.Height <= 0))
        {
            return PaintResult.None;
        }

        // The grid starts at the mesh's own x and y, not inherited through href, as Inkscape reads a mesh that names another.
        SvgLengthContext lengths = context.Lengths;
        float Start(string name, SvgAxis axis)
        {
            string text = mesh.Attributes[name] ?? "0";
            if (!SvgLength.TryParse(text, out SvgLength l))
            {
                _report.Warn("Svg.BadAttribute", $"The mesh gradient's {name} \"{SvgGeometry.Clip(text)}\" is not a length; 0 is used.", mesh);
                l = SvgLength.Zero;
            }

            return boundingBox ? l.ToFraction() : l.ToPixels(lengths, axis);
        }

        Vector2 origin = new(Start("x", SvgAxis.X), Start("y", SvgAxis.Y));
        List<List<SvgMeshPatch>> rows = Patches(source, origin);
        if (rows.Count == 0)
        {
            return PaintResult.None;
        }

        Matrix3x2 transform = Matrix3x2.Identity;
        if ((Inherited(chain, "transform") ?? Inherited(chain, "gradientTransform")) is string t && !SvgTransforms.TryParse(t, out transform))
        {
            _report.Warn("Svg.BadAttribute", $"The mesh gradient's transform \"{SvgGeometry.Clip(t)}\" cannot be read; it is ignored.", mesh);
            transform = Matrix3x2.Identity;
        }

        if (!Matrix3x2.Invert(transform, out _))
        {
            transform = Matrix3x2.Identity;
        }

        transform = AboutOrigin(mesh, transform, context);
        Matrix3x2 local = boundingBox ? transform * new Matrix3x2(bounds.Width, 0, 0, bounds.Height, bounds.X, bounds.Y) : transform;

        List<Vector2> all = [.. rows.SelectMany(r => r).SelectMany(p => p.Points)];
        RectF area = RectF.FromPoints(all.ToArray());
        if (!(area.Width > 0 && area.Height > 0))
        {
            return PaintResult.None;
        }

        // A transparent margin keeps the tile's own edge, which the paint samples softly, away from the mesh's edge.
        float margin = Math.Max(area.Width, area.Height) / 16;
        area = area.Inflate(margin, margin);
        using PictureRecorder recorder = new(area);
        SvgMesh.Draw(recorder, rows, Inherited(chain, "type") == "bicubic");
        paint = new PicturePaint(recorder.Finish(), TileMode.Decal, TileMode.Decal, area) { LocalMatrix = local };
        return PaintResult.Paint;
    }

    /// <summary>The patches of a mesh, row by row; a patch whose stops cannot be read ends its row, and a short row ends the mesh.</summary>
    private List<List<SvgMeshPatch>> Patches(SvgElement mesh, Vector2 origin)
    {
        List<List<SvgMeshPatch>> rows = [];
        foreach (SvgElement rowElement in mesh.Elements("meshrow"))
        {
            int r = rows.Count;
            List<SvgMeshPatch> row = [];
            foreach (SvgElement patchElement in rowElement.Elements("meshpatch"))
            {
                int c = row.Count;
                if (r > 0 && c >= rows[r - 1].Count)
                {
                    break;
                }

                SvgMeshPatch? patch = MeshPatch(patchElement, origin, r > 0 ? rows[r - 1][c] : null, c > 0 ? row[c - 1] : null);
                if (patch is null)
                {
                    break;
                }

                row.Add(patch.Value);
            }

            if (row.Count == 0 || (r > 0 && row.Count != rows[0].Count))
            {
                if (row.Count > 0 || rowElement.Elements("meshpatch").Any())
                {
                    _report.Warn("Svg.BadAttribute", "A mesh row has fewer readable patches than the first row; the mesh ends before it.", rowElement);
                }

                break;
            }

            rows.Add(row);
        }

        // Every row must be as long as the first, or the grid of corners has holes.
        int width = rows.Count > 0 ? rows[0].Count : 0;
        rows.RemoveAll(row => row.Count != width);
        return rows;
    }

    /// <summary>One patch from its stops, the patch above and the patch to the left.</summary>
    private SvgMeshPatch? MeshPatch(SvgElement patch, Vector2 origin, SvgMeshPatch? above, SvgMeshPatch? left)
    {
        // Edges clockwise: 0 top (corner 0 to 1), 1 right (1 to 2), 2 bottom (2 to 3), 3 left (3 to 0).
        Vector2[]?[] edges = new Vector2[4][];
        RgbaF32?[] colors = new RgbaF32?[4];
        Vector2?[] corners = new Vector2?[4];
        if (above is SvgMeshPatch a)
        {
            edges[0] = [a.Points[9], a.Points[8], a.Points[7], a.Points[6]];
            (corners[0], corners[1]) = (a.Points[9], a.Points[6]);
            (colors[0], colors[1]) = (a.Colors[3], a.Colors[2]);
        }

        if (left is SvgMeshPatch l)
        {
            edges[3] = [l.Points[6], l.Points[5], l.Points[4], l.Points[3]];
            (corners[3], corners[0]) = (l.Points[6], l.Points[3]);
            (colors[3], colors[0]) = (l.Colors[2], l.Colors[1]);
        }

        corners[0] ??= origin;
        List<SvgElement> stops = [.. patch.Elements("stop")];
        int stop = 0;
        for (int e = 0; e < 4; e++)
        {
            if (edges[e] is not null)
            {
                continue;
            }

            if (stop >= stops.Count)
            {
                _report.Warn("Svg.BadAttribute", "A mesh patch has too few stops; the mesh ends at it.", patch);
                return null;
            }

            SvgElement s = stops[stop++];
            Vector2 start = corners[e] ?? edges[(e + 3) % 4]![3];
            if (!SvgMesh.TryEdge(s.Attributes["path"], start, corners[(e + 1) % 4], out Vector2[] edge))
            {
                _report.Warn("Svg.BadAttribute", $"A mesh stop's path \"{SvgGeometry.Clip(s.Attributes["path"] ?? "")}\" is not one c, C, l or L command; the mesh ends at it.", s);
                return null;
            }

            edges[e] = edge;
            corners[e] ??= start;
            corners[(e + 1) % 4] ??= edge[3];
            if (colors[e] is null)
            {
                SvgStyle style = OriginalStyle(s).Style;
                CssColor color = style.StopColor;
                colors[e] = new RgbaF32(color.R, color.G, color.B, color.A * style.StopOpacity);
            }
        }

        // A corner whose stop was shared takes the color the stop before gave it; the first patch always has all four.
        Vector2[] points =
        [
            edges[0]![0], edges[0]![1], edges[0]![2],
            edges[1]![0], edges[1]![1], edges[1]![2],
            edges[2]![0], edges[2]![1], edges[2]![2],
            edges[3]![0], edges[3]![1], edges[3]![2],
        ];
        RgbaF32[] cornerColors = [colors[0] ?? default, colors[1] ?? default, colors[2] ?? default, colors[3] ?? default];
        return new SvgMeshPatch(points, cornerColors);
    }
}
