using System.Buffers;
using System.Buffers.Binary;

namespace Egami.Formats.Psd;

/// <summary>
/// A layer's vector mask (vmsk or vsms), rasterized: the path records of the specification's "Path resource format" (subpath
/// length records, Bézier knots in signed 8.24 fractions of the canvas's height and width, the initial fill rule), filled
/// with the non-zero rule and antialiased (sixteen sub-scanlines a row, exact coverage across), the subpaths combined by the
/// shape operation Photoshop keeps in each length record (union, subtract, intersect, exclude), as psd-tools reads it.
/// </summary>
internal static class PsdVectorMask
{
    private const int SubScanlines = 16;

    /// <summary>
    /// Returns the mask's coverage, 0 to 1, a value a canvas pixel (width × height of them, in an array rented from the shared
    /// pool, which the caller returns), or <see langword="null"/> when the mask is disabled or its data is unreadable. An inverted
    /// mask comes back inverted.
    /// </summary>
    public static float[]? Rasterize(ReadOnlySpan<byte> data, int width, int height)
    {
        if (data.Length < 8 || BinaryPrimitives.ReadUInt32BigEndian(data) != 3)
        {
            return null;
        }

        uint flags = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if ((flags & 4) != 0)
        {
            return null;
        }

        List<(int Operation, List<List<Knot>> Paths)> groups = [];
        bool startFilled = false;
        List<Knot>? current = null;
        int remaining = 0;
        for (int at = 8; at + 26 <= data.Length; at += 26)
        {
            ReadOnlySpan<byte> record = data.Slice(at, 26);
            int selector = BinaryPrimitives.ReadUInt16BigEndian(record);
            switch (selector)
            {
                case 0 or 3:
                    {
                        remaining = BinaryPrimitives.ReadUInt16BigEndian(record[2..]);
                        int operation = BinaryPrimitives.ReadInt16BigEndian(record[4..]);
                        current = [];
                        if (operation == -1 && groups.Count > 0)
                        {
                            groups[^1].Paths.Add(current);
                        }
                        else
                        {
                            groups.Add((operation, [current]));
                        }

                        break;
                    }

                case 1 or 2 or 4 or 5 when current is not null && remaining > 0:
                    current.Add(new Knot(Point(record[2..], width, height), Point(record[10..], width, height), Point(record[18..], width, height)));
                    remaining--;
                    break;
                case 8:
                    startFilled = BinaryPrimitives.ReadUInt16BigEndian(record[2..]) != 0;
                    break;
            }
        }

        int count = width * height;
        float[] mask = ArrayPool<float>.Shared.Rent(count);
        Array.Fill(mask, startFilled && groups.Count == 0 ? 1f : 0f, 0, count);
        float[] plane = ArrayPool<float>.Shared.Rent(count);
        bool first = true;
        foreach ((int operation, List<List<Knot>> paths) in groups)
        {
            Array.Clear(plane, 0, count);
            Fill(paths, plane, width, height);
            for (int i = 0; i < count; i++)
            {
                float m = mask[i], p = plane[i];
                mask[i] = operation switch
                {
                    0 => m + p - (2 * m * p),
                    2 => Math.Max(0, (first ? 1 - m : m) - p),
                    3 => (first ? 1 - m : m) * p,
                    _ => m + p - (m * p),
                };
            }

            first = false;
        }

        ArrayPool<float>.Shared.Return(plane);
        if ((flags & 1) != 0)
        {
            for (int i = 0; i < count; i++)
            {
                mask[i] = 1 - mask[i];
            }
        }

        return mask;
    }

    private static (double X, double Y) Point(ReadOnlySpan<byte> bytes, int width, int height)
    {
        double y = BinaryPrimitives.ReadInt32BigEndian(bytes) / 16777216.0 * height;
        double x = BinaryPrimitives.ReadInt32BigEndian(bytes[4..]) / 16777216.0 * width;
        return (x, y);
    }

    /// <summary>Fills the closed outlines of <paramref name="paths"/> with the non-zero rule into <paramref name="plane"/>, antialiased.</summary>
    private static void Fill(List<List<Knot>> paths, float[] plane, int width, int height)
    {
        List<Edge> edges = [];
        foreach (List<Knot> path in paths)
        {
            if (path.Count < 2)
            {
                continue;
            }

            for (int i = 0; i < path.Count; i++)
            {
                Knot a = path[i], b = path[(i + 1) % path.Count];
                Flatten(a.Anchor, a.Leaving, b.Preceding, b.Anchor, edges);
            }
        }

        edges.RemoveAll(e => e.Y0 == e.Y1);
        edges.Sort((a, b) => a.Top.CompareTo(b.Top));
        double[] row = new double[width + 1];
        List<(double X, int Winding)> crossings = [];
        List<Edge> active = [];
        int next = 0;
        for (int y = 0; y < height; y++)
        {
            Array.Clear(row);
            bool any = false;
            for (int k = 0; k < SubScanlines; k++)
            {
                double sy = y + ((k + 0.5) / SubScanlines);
                while (next < edges.Count && edges[next].Top <= sy)
                {
                    active.Add(edges[next++]);
                }

                active.RemoveAll(e => e.Bottom <= sy);
                crossings.Clear();
                foreach (Edge e in active)
                {
                    if (e.Top <= sy && sy < e.Bottom)
                    {
                        crossings.Add((e.X0 + ((sy - e.Y0) * (e.X1 - e.X0) / (e.Y1 - e.Y0)), e.Y1 > e.Y0 ? 1 : -1));
                    }
                }

                if (crossings.Count < 2)
                {
                    continue;
                }

                crossings.Sort((a, b) => a.X.CompareTo(b.X));
                int winding = 0;
                for (int c = 0; c < crossings.Count - 1; c++)
                {
                    winding += crossings[c].Winding;
                    if (winding != 0)
                    {
                        Span(row, crossings[c].X, crossings[c + 1].X, width);
                        any = true;
                    }
                }
            }

            if (!any)
            {
                continue;
            }

            int at = y * width;
            for (int x = 0; x < width; x++)
            {
                plane[at + x] = (float)Math.Clamp(row[x], 0, 1);
            }
        }
    }

    /// <summary>Adds one sub-scanline's span from <paramref name="x0"/> to <paramref name="x1"/> to the row's coverage, exactly across pixels.</summary>
    private static void Span(double[] row, double x0, double x1, int width)
    {
        const double weight = 1.0 / SubScanlines;
        x0 = Math.Clamp(x0, 0, width);
        x1 = Math.Clamp(x1, 0, width);
        if (x1 <= x0)
        {
            return;
        }

        int i0 = (int)x0, i1 = (int)x1;
        if (i0 == i1)
        {
            row[i0] += (x1 - x0) * weight;
            return;
        }

        row[i0] += (i0 + 1 - x0) * weight;
        for (int i = i0 + 1; i < i1; i++)
        {
            row[i] += weight;
        }

        if (i1 < width)
        {
            row[i1] += (x1 - i1) * weight;
        }
    }

    /// <summary>Flattens a cubic Bézier into line segments, more of them the longer its control polygon.</summary>
    private static void Flatten((double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3, List<Edge> edges)
    {
        double length = Distance(p0, p1) + Distance(p1, p2) + Distance(p2, p3);
        int steps = Math.Clamp((int)Math.Ceiling(Math.Sqrt(length) * 2), 1, 256);
        (double X, double Y) previous = p0;
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps, u = 1 - t;
            double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
            (double X, double Y) point = ((a * p0.X) + (b * p1.X) + (c * p2.X) + (d * p3.X), (a * p0.Y) + (b * p1.Y) + (c * p2.Y) + (d * p3.Y));
            edges.Add(new Edge(previous.X, previous.Y, point.X, point.Y));
            previous = point;
        }

        static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
    }

    /// <summary>A path point: the control point before it, the anchor and the control point after it, in pixels.</summary>
    private readonly record struct Knot((double X, double Y) Preceding, (double X, double Y) Anchor, (double X, double Y) Leaving);

    private readonly record struct Edge(double X0, double Y0, double X1, double Y1)
    {
        public double Top => Math.Min(Y0, Y1);

        public double Bottom => Math.Max(Y0, Y1);
    }
}
