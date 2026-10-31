using System.Buffers.Binary;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// DNG opcode lists (DNG specification, chapter 7): a big-endian count, then opcodes, each an id, the DNG version it needs,
/// flags (bit 0 optional, bit 1 may be skipped for previews), a byte count and parameters. List 1 works on the stored values,
/// list 2 on the linear mosaic, list 3 on the demosaiced RGB. Lens opcodes (warps and radial vignetting) are skipped when lens
/// corrections are off. An opcode Egami does not apply is skipped with a warning.
/// </summary>
internal static class DngOpcodes
{
    public const int WarpRectilinear = 1;
    public const int WarpFisheye = 2;
    public const int FixVignetteRadial = 3;
    public const int FixBadPixelsConstant = 4;
    public const int FixBadPixelsList = 5;
    public const int TrimBounds = 6;
    public const int MapTable = 7;
    public const int MapPolynomial = 8;
    public const int GainMap = 9;
    public const int DeltaPerRow = 10;
    public const int DeltaPerColumn = 11;
    public const int ScalePerRow = 12;
    public const int ScalePerColumn = 13;

    /// <summary>Returns the ids of the opcodes in a list, for <see cref="RawDetails"/>.</summary>
    public static List<int> Ids(byte[] list)
    {
        List<int> ids = [];
        Walk(list, (id, _, _) => ids.Add(id));
        return ids;
    }

    public static void Apply(byte[] list, RawPlane plane, int stage, bool lens, EgamiInput input, RawWarnings? warnings = null)
    {
        Walk(list, (id, flags, data) =>
        {
            bool optional = (flags & 1) != 0;
            try
            {
                if (!Run(id, data, plane, stage, lens))
                {
                    warnings?.Add("OpcodeSkipped", $"DNG opcode {id} in list {stage} is not applied{(optional ? " (it is optional)" : "")}.");
                }
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException)
            {
                warnings?.Add("OpcodeDamaged", $"DNG opcode {id} in list {stage} is damaged; it was skipped.");
            }
        });
    }

    private static void Walk(byte[] list, Action<int, int, ReadOnlyMemory<byte>> each)
    {
        if (list.Length < 4)
        {
            return;
        }

        uint count = BinaryPrimitives.ReadUInt32BigEndian(list);
        int p = 4;
        for (uint i = 0; i < count && p + 16 <= list.Length; i++)
        {
            int id = (int)BinaryPrimitives.ReadUInt32BigEndian(list.AsSpan(p));
            int flags = (int)BinaryPrimitives.ReadUInt32BigEndian(list.AsSpan(p + 8));
            uint size = BinaryPrimitives.ReadUInt32BigEndian(list.AsSpan(p + 12));
            p += 16;
            if (size > list.Length - p)
            {
                return;
            }

            each(id, flags, list.AsMemory(p, (int)size));
            p += (int)size;
        }
    }

    private static bool Run(int id, ReadOnlyMemory<byte> data, RawPlane plane, int stage, bool lens)
    {
        Params p = new(data.Span);
        switch (id)
        {
            case WarpRectilinear:
            case WarpFisheye:
                if (lens)
                {
                    Warp(p, plane, id == WarpFisheye);
                }

                return true;
            case FixVignetteRadial:
                if (lens)
                {
                    Vignette(p, plane);
                }

                return true;
            case FixBadPixelsConstant:
                FixConstant(p, plane);
                return true;
            case FixBadPixelsList:
                FixList(p, plane);
                return true;
            case MapTable:
                {
                    Area area = p.Area();
                    int size = (int)Math.Min(p.U32(), 65536);
                    float[] table = new float[size];
                    for (int i = 0; i < size; i++)
                    {
                        table[i] = p.U16() / 65535f;
                    }

                    if (size > 0)
                    {
                        Each(plane, area, (ref float v, int _, int _, int _) => v = table[Math.Clamp((int)MathF.Round(v * 65535), 0, size - 1)]);
                    }

                    return true;
                }

            case MapPolynomial:
                {
                    Area area = p.Area();
                    int degree = (int)Math.Min(p.U32(), 8);
                    double[] c = new double[degree + 1];
                    for (int i = 0; i <= degree; i++)
                    {
                        c[i] = p.F64();
                    }

                    Each(plane, area, (ref float v, int _, int _, int _) =>
                    {
                        double x = v, sum = 0, power = 1;
                        for (int i = 0; i <= degree; i++)
                        {
                            sum += c[i] * power;
                            power *= x;
                        }

                        v = (float)Math.Clamp(sum, 0, 1);
                    });
                    return true;
                }

            case GainMap:
                Gain(p, plane);
                return true;
            case DeltaPerRow:
            case DeltaPerColumn:
            case ScalePerRow:
            case ScalePerColumn:
                {
                    Area area = p.Area();
                    int count = (int)Math.Min(p.U32(), 1 << 20);
                    float[] values = new float[count];
                    for (int i = 0; i < count; i++)
                    {
                        values[i] = p.F32();
                    }

                    bool rows = id is DeltaPerRow or ScalePerRow;
                    bool scale = id is ScalePerRow or ScalePerColumn;
                    Each(plane, area, (ref float v, int x, int y, int c) =>
                    {
                        int i = rows ? (y - area.Top) / Math.Max(area.RowPitch, 1) : (x - area.Left) / Math.Max(area.ColPitch, 1);
                        if ((uint)i < (uint)count)
                        {
                            v = scale ? v * values[i] : v + values[i];
                        }
                    });
                    return true;
                }

            default:
                return false;
        }
    }

    private delegate void Pixel(ref float value, int x, int y, int plane);

    /// <summary>Runs <paramref name="action"/> on the samples of an area, in full-size coordinates.</summary>
    private static void Each(RawPlane plane, Area area, Pixel action)
    {
        int s = plane.Scale;
        int planes = plane.Planes;
        for (int y = 0; y < plane.Height; y++)
        {
            int fy = (y * s) + (s / 2);
            if (fy < area.Top || fy >= area.Bottom || (fy - area.Top) % Math.Max(area.RowPitch, 1) != 0 && s == 1)
            {
                continue;
            }

            Span<float> row = plane.Row(y);
            for (int x = 0; x < plane.Width; x++)
            {
                int fx = (x * s) + (s / 2);
                if (fx < area.Left || fx >= area.Right || (fx - area.Left) % Math.Max(area.ColPitch, 1) != 0 && s == 1)
                {
                    continue;
                }

                for (int c = area.Plane; c < Math.Min(planes, area.Plane + area.Planes); c++)
                {
                    action(ref row[(x * planes) + c], fx, fy, c);
                }
            }
        }
    }

    private static void Gain(Params p, RawPlane plane)
    {
        Area area = p.Area();
        int pointsV = (int)p.U32(), pointsH = (int)p.U32();
        double spacingV = p.F64(), spacingH = p.F64(), originV = p.F64(), originH = p.F64();
        int mapPlanes = (int)p.U32();
        if (pointsV < 1 || pointsH < 1 || mapPlanes < 1 || (long)pointsV * pointsH * mapPlanes > 1 << 22)
        {
            throw new InvalidDataException("The GainMap's size is not valid.");
        }

        float[] gains = new float[pointsV * pointsH * mapPlanes];
        for (int i = 0; i < gains.Length; i++)
        {
            gains[i] = p.F32();
        }

        double fullW = plane.Width * plane.Scale, fullH = plane.Height * plane.Scale;
        Each(plane, area, (ref float v, int x, int y, int c) =>
        {
            double gy = ((y / fullH) - originV) / (spacingV == 0 ? 1 : spacingV);
            double gx = ((x / fullW) - originH) / (spacingH == 0 ? 1 : spacingH);
            gy = Math.Clamp(gy, 0, pointsV - 1);
            gx = Math.Clamp(gx, 0, pointsH - 1);
            int y0 = Math.Min((int)gy, pointsV - 1), x0 = Math.Min((int)gx, pointsH - 1);
            int y1 = Math.Min(y0 + 1, pointsV - 1), x1 = Math.Min(x0 + 1, pointsH - 1);
            double ty = gy - y0, tx = gx - x0;
            v = (float)(v * Sample(gains, pointsH, mapPlanes, Math.Min(c, mapPlanes - 1), x0, x1, y0, y1, tx, ty));
        });
    }

    private static double Sample(float[] g, int w, int planes, int c, int x0, int x1, int y0, int y1, double tx, double ty)
    {
        double a = g[(((y0 * w) + x0) * planes) + c], b = g[(((y0 * w) + x1) * planes) + c];
        double d = g[(((y1 * w) + x0) * planes) + c], e = g[(((y1 * w) + x1) * planes) + c];
        return (((a * (1 - tx)) + (b * tx)) * (1 - ty)) + (((d * (1 - tx)) + (e * tx)) * ty);
    }

    /// <summary>FixVignetteRadial: gain 1 + k0 r² + k1 r⁴ + … + k4 r¹⁰, r normalized to 1 at the farthest corner.</summary>
    private static void Vignette(Params p, RawPlane plane)
    {
        double[] k = [p.F64(), p.F64(), p.F64(), p.F64(), p.F64()];
        double cx = p.F64(), cy = p.F64();
        (double ox, double oy, double scale) = Center(plane, cx, cy);
        for (int y = 0; y < plane.Height; y++)
        {
            Span<float> row = plane.Row(y);
            double dy = ((y * plane.Scale) + (plane.Scale / 2.0) - oy) * scale;
            for (int x = 0; x < plane.Width; x++)
            {
                double dx = ((x * plane.Scale) + (plane.Scale / 2.0) - ox) * scale;
                double r2 = (dx * dx) + (dy * dy);
                double gain = 1 + (r2 * (k[0] + (r2 * (k[1] + (r2 * (k[2] + (r2 * (k[3] + (r2 * k[4])))))))));
                for (int c = 0; c < plane.Planes; c++)
                {
                    row[(x * plane.Planes) + c] *= (float)gain;
                }
            }
        }
    }

    /// <summary>Returns the optical centre in full-size pixels and the factor that makes the distance to the farthest corner 1.</summary>
    private static (double X, double Y, double Scale) Center(RawPlane plane, double cx, double cy)
    {
        double w = plane.Width * plane.Scale, h = plane.Height * plane.Scale;
        double ox = cx * w, oy = cy * h;
        double far = Math.Sqrt(Math.Max(ox * ox, (w - ox) * (w - ox)) + Math.Max(oy * oy, (h - oy) * (h - oy)));
        return (ox, oy, far > 0 ? 1 / far : 1);
    }

    /// <summary>WarpRectilinear (radial and tangential terms) and WarpFisheye: each output pixel samples the source where the model maps it.</summary>
    private static void Warp(Params p, RawPlane plane, bool fisheye)
    {
        int n = (int)p.U32();
        if (n is < 1 or > 16)
        {
            throw new InvalidDataException("The warp opcode's plane count is not valid.");
        }

        int terms = fisheye ? 4 : 6;
        double[][] coefficients = new double[n][];
        for (int i = 0; i < n; i++)
        {
            coefficients[i] = new double[terms];
            for (int t = 0; t < terms; t++)
            {
                coefficients[i][t] = p.F64();
            }
        }

        double cx = p.F64(), cy = p.F64();
        (double ox, double oy, double scale) = Center(plane, cx, cy);
        int planes = plane.Planes, width = plane.Width, height = plane.Height;
        float[] source = System.Buffers.ArrayPool<float>.Shared.Rent(width * height * planes);
        plane.Values.Span[..(width * height * planes)].CopyTo(source);
        double inv = plane.Scale;
        for (int y = 0; y < height; y++)
        {
            Span<float> row = plane.Row(y);
            double dy = ((y * inv) + (inv / 2) - oy) * scale;
            for (int x = 0; x < width; x++)
            {
                double dx = ((x * inv) + (inv / 2) - ox) * scale;
                for (int c = 0; c < planes; c++)
                {
                    double[] k = coefficients[Math.Min(c, n - 1)];
                    double r2 = (dx * dx) + (dy * dy);
                    double sx, sy;
                    if (fisheye)
                    {
                        double r = Math.Sqrt(r2);
                        double theta = Math.Atan(r), t2 = theta * theta;
                        double rs = theta * (k[0] + (t2 * (k[1] + (t2 * (k[2] + (t2 * k[3]))))));
                        double ratio = r > 1e-12 ? rs / r : k[0];
                        sx = dx * ratio;
                        sy = dy * ratio;
                    }
                    else
                    {
                        double f = k[0] + (r2 * (k[1] + (r2 * (k[2] + (r2 * k[3])))));
                        sx = (f * dx) + (k[4] * 2 * dx * dy) + (k[5] * (r2 + (2 * dx * dx)));
                        sy = (f * dy) + (k[5] * 2 * dx * dy) + (k[4] * (r2 + (2 * dy * dy)));
                    }

                    double px = (((sx / scale) + ox) / inv) - 0.5, py = (((sy / scale) + oy) / inv) - 0.5;
                    row[(x * planes) + c] = Bilinear(source, width, height, planes, c, px, py);
                }
            }
        }

        System.Buffers.ArrayPool<float>.Shared.Return(source);
    }

    private static float Bilinear(float[] s, int w, int h, int planes, int c, double x, double y)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = (float)(x - x0), ty = (float)(y - y0);
        float a = s[(((y0 * w) + x0) * planes) + c], b = s[(((y0 * w) + x1) * planes) + c];
        float d = s[(((y1 * w) + x0) * planes) + c], e = s[(((y1 * w) + x1) * planes) + c];
        return (((a * (1 - tx)) + (b * tx)) * (1 - ty)) + (((d * (1 - tx)) + (e * tx)) * ty);
    }

    private static void FixConstant(Params p, RawPlane plane)
    {
        float constant = p.U32() / 65535f;
        p.U32();
        List<(int X, int Y)> bad = [];
        for (int y = 0; y < plane.Height; y++)
        {
            Span<float> row = plane.Row(y);
            for (int x = 0; x < plane.Width; x++)
            {
                if (MathF.Abs(row[x * plane.Planes] - constant) < 0.5f / 65535)
                {
                    bad.Add((x, y));
                }
            }
        }

        Repair(plane, bad);
    }

    private static void FixList(Params p, RawPlane plane)
    {
        p.U32();
        int points = (int)Math.Min(p.U32(), 1 << 22), rects = (int)Math.Min(p.U32(), 1 << 16);
        List<(int X, int Y)> bad = [];
        for (int i = 0; i < points; i++)
        {
            int y = (int)p.U32(), x = (int)p.U32();
            bad.Add((x, y));
        }

        for (int i = 0; i < rects; i++)
        {
            int top = (int)p.U32(), left = (int)p.U32(), bottom = (int)p.U32(), right = (int)p.U32();
            for (int y = Math.Max(top, 0); y < Math.Min(bottom, plane.Height); y++)
            {
                for (int x = Math.Max(left, 0); x < Math.Min(right, plane.Width); x++)
                {
                    bad.Add((x, y));
                }
            }
        }

        Repair(plane, bad);
    }

    /// <summary>Replaces each bad pixel by the mean of the good pixels of its colour around it (2 apart on a Bayer mosaic).</summary>
    private static void Repair(RawPlane plane, List<(int X, int Y)> bad)
    {
        HashSet<(int, int)> set = [.. bad];
        int step = plane.Cfa is null ? 1 : 2;
        foreach ((int x, int y) in bad)
        {
            if ((uint)x >= (uint)plane.Width || (uint)y >= (uint)plane.Height)
            {
                continue;
            }

            for (int c = 0; c < plane.Planes; c++)
            {
                float sum = 0;
                int count = 0;
                for (int dy = -step; dy <= step; dy += step)
                {
                    for (int dx = -step; dx <= step; dx += step)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((dx | dy) == 0 || (uint)nx >= (uint)plane.Width || (uint)ny >= (uint)plane.Height || set.Contains((nx, ny)))
                        {
                            continue;
                        }

                        if (plane.Cfa is RawCfa cfa && cfa.ColorAt(nx, ny) != cfa.ColorAt(x, y))
                        {
                            continue;
                        }

                        sum += plane.Row(ny)[(nx * plane.Planes) + c];
                        count++;
                    }
                }

                if (count > 0)
                {
                    plane.Row(y)[(x * plane.Planes) + c] = sum / count;
                }
            }
        }
    }

    /// <summary>The area an opcode applies to: rows and columns in full-size coordinates, planes, and pitches.</summary>
    private readonly record struct Area(int Top, int Left, int Bottom, int Right, int Plane, int Planes, int RowPitch, int ColPitch);

    /// <summary>Reads an opcode's big-endian parameters; reading past them throws <see cref="InvalidDataException"/>.</summary>
    private ref struct Params(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _p;

        public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

        public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));

        public float F32() => BinaryPrimitives.ReadSingleBigEndian(Take(4));

        public double F64() => BinaryPrimitives.ReadDoubleBigEndian(Take(8));

        public Area Area()
        {
            int top = (int)U32(), left = (int)U32(), bottom = (int)U32(), right = (int)U32();
            int plane = (int)U32(), planes = (int)U32(), rowPitch = (int)U32(), colPitch = (int)U32();
            return new Area(top, left, bottom, right, plane, Math.Max(planes, 1), Math.Max(rowPitch, 1), Math.Max(colPitch, 1));
        }

        private ReadOnlySpan<byte> Take(int n)
        {
            if (_p + n > _data.Length)
            {
                throw new InvalidDataException("The opcode's parameters are cut short.");
            }

            ReadOnlySpan<byte> s = _data.Slice(_p, n);
            _p += n;
            return s;
        }
    }
}

/// <summary>Warnings a develop collects, reported on the loaded raster.</summary>
internal sealed class RawWarnings
{
    private readonly List<(string Id, string Message)> _list = [];

    public IReadOnlyList<(string Id, string Message)> List => _list;

    public void Add(string id, string message)
    {
        lock (_list)
        {
            if (!_list.Exists(w => w.Id == id && w.Message == message))
            {
                _list.Add((id, message));
            }
        }
    }
}
