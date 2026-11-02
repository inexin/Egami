using Egami.Pixels;

namespace Egami.Video.Tests.Filters;

/// <summary>
/// The synthetic deinterlacing set: progressive clips drawn at the field rate, then woven two pictures to a frame, so each
/// deinterlaced field has a true picture to be measured against. The scenes hold still detail one line high, a texture that
/// pans, a bright bar with slanted edges that crosses the picture, and a slowly turning line.
/// </summary>
internal static class WovenClips
{
    /// <summary>The scenes of the set.</summary>
    public static readonly string[] Scenes = ["pan", "bar", "still", "mixed"];

    /// <summary>Draws progressive picture <paramref name="t"/> (a field time) of a scene.</summary>
    public static YuvRaster Picture(string scene, int t, int width, int height, ChromaFormat chroma = ChromaFormat.Yuv420, int depth = 8, bool alpha = false)
    {
        YuvColor color = new(YuvMatrix.Bt709, YuvRange.Limited, ChromaSiting.Left);
        YuvRaster raster = depth == 8
            ? YuvRaster.Create(width, height, chroma, color, alpha)
            : YuvRaster.Create(width, height, chroma, depth, color, alpha);
        int max = (1 << depth) - 1;
        if (raster is YuvRaster<byte> bytes)
        {
            Draw(bytes, scene, t, max);
        }
        else
        {
            Draw((YuvRaster<ushort>)raster, scene, t, max);
        }

        return raster;
    }

    /// <summary>Weaves pictures 2n and 2n+1 into frame n: the first field in time from 2n.</summary>
    public static YuvVideoFrame Weave(YuvRaster first, YuvRaster second, int n, FieldOrder order, Rational frameRate)
    {
        YuvRaster woven = Copy(first);
        int firstParity = order == FieldOrder.BottomFieldFirst ? 1 : 0;
        if (woven is YuvRaster<byte> bytes)
        {
            WeaveInto(bytes, (YuvRaster<byte>)second, 1 - firstParity);
        }
        else
        {
            WeaveInto((YuvRaster<ushort>)woven, (YuvRaster<ushort>)second, 1 - firstParity);
        }

        MediaTime step = new(1, frameRate.Reciprocal());
        return new YuvVideoFrame(woven, new MediaTime(n, frameRate.Reciprocal()), step) { FieldOrder = order };
    }

    /// <summary>Makes a clip of <paramref name="frames"/> woven frames and the progressive pictures, two per frame.</summary>
    public static (List<YuvVideoFrame> Frames, List<YuvRaster> Pictures) Clip(string scene, int frames, int width, int height, FieldOrder order = FieldOrder.TopFieldFirst,
        ChromaFormat chroma = ChromaFormat.Yuv420, int depth = 8, bool alpha = false)
    {
        List<YuvRaster> pictures = [];
        List<YuvVideoFrame> woven = [];
        for (int t = 0; t < 2 * frames; t++)
        {
            pictures.Add(Picture(scene, t, width, height, chroma, depth, alpha));
        }

        for (int n = 0; n < frames; n++)
        {
            woven.Add(Weave(pictures[2 * n], pictures[(2 * n) + 1], n, order, new Rational(25, 1)));
        }

        return (woven, pictures);
    }

    /// <summary>The PSNR of every plane together, in dB, against the largest sample value.</summary>
    public static double Psnr(YuvRaster a, YuvRaster b)
    {
        (double sum, long count) = a is YuvRaster<byte> bytes ? Squares(bytes, (YuvRaster<byte>)b) : Squares((YuvRaster<ushort>)a, (YuvRaster<ushort>)b);
        double mse = sum / count;
        double max = (1 << a.BitDepth) - 1;
        return mse == 0 ? 100 : 10 * Math.Log10(max * max / mse);
    }

    public static YuvRaster Copy(YuvRaster source)
    {
        YuvRaster copy = source.BitDepth == 8
            ? YuvRaster.Create(source.Width, source.Height, source.ChromaFormat, source.Color, source.HasAlpha)
            : YuvRaster.Create(source.Width, source.Height, source.ChromaFormat, source.BitDepth, source.Color, source.HasAlpha);
        if (source is YuvRaster<byte> bytes)
        {
            ForPlanes(bytes, (YuvRaster<byte>)copy, (s, t) => CopyRows(s, t, 0, 1));
        }
        else
        {
            ForPlanes((YuvRaster<ushort>)source, (YuvRaster<ushort>)copy, (s, t) => CopyRows(s, t, 0, 1));
        }

        return copy;
    }

    private static void WeaveInto<T>(YuvRaster<T> target, YuvRaster<T> second, int parity)
        where T : unmanaged => ForPlanes(second, target, (s, t) => CopyRows(s, t, parity, 2));

    private static void CopyRows<T>(YuvPlane<T> source, YuvPlane<T> target, int start, int step)
        where T : unmanaged
    {
        for (int y = start; y < source.Height; y += step)
        {
            source.Row(y)[..source.Width].CopyTo(target.Row(y));
        }
    }

    private static void ForPlanes<T>(YuvRaster<T> source, YuvRaster<T> target, Action<YuvPlane<T>, YuvPlane<T>> action)
        where T : unmanaged
    {
        action(source.Y, target.Y);
        if (source.U is not null)
        {
            action(source.U, target.U!);
            action(source.V!, target.V!);
        }

        if (source.A is not null)
        {
            action(source.A, target.A!);
        }
    }

    private static (double Sum, long Count) Squares<T>(YuvRaster<T> a, YuvRaster<T> b)
        where T : unmanaged, System.Numerics.INumberBase<T>
    {
        double sum = 0;
        long count = 0;
        void Plane(YuvPlane<T> p, YuvPlane<T> q)
        {
            for (int y = 0; y < p.Height; y++)
            {
                ReadOnlySpan<T> r = p.Row(y);
                ReadOnlySpan<T> s = q.Row(y);
                for (int x = 0; x < p.Width; x++)
                {
                    double d = double.CreateTruncating(r[x]) - double.CreateTruncating(s[x]);
                    sum += d * d;
                }

                count += p.Width;
            }
        }

        Plane(a.Y, b.Y);
        if (a.U is not null)
        {
            Plane(a.U, b.U!);
            Plane(a.V!, b.V!);
        }

        if (a.A is not null)
        {
            Plane(a.A, b.A!);
        }

        return (sum, count);
    }

    private static void Draw<T>(YuvRaster<T> raster, string scene, int t, int max)
        where T : unmanaged, System.Numerics.INumberBase<T>
    {
        int motion = scene == "still" ? 0 : t;
        int sx = raster.U is null ? 1 : raster.Width / raster.U.Width;
        int sy = raster.U is null ? 1 : raster.Height / raster.U.Height;
        Fill(raster.Y, 1, 1, (x, y) => Luma(scene, t, x, y, raster.Width, raster.Height), max);
        if (raster.U is not null)
        {
            Fill(raster.U, sx, sy, (x, y) => 0.5 + (0.25 * Math.Sin((x * 0.05) + (motion * 0.1))) + (Bar(scene, t, x, y, raster.Width, raster.Height) * 0.2), max);
            Fill(raster.V!, sx, sy, (x, y) => 0.5 + (0.25 * Math.Cos((y * 0.04) - (motion * 0.07))) - (Bar(scene, t, x, y, raster.Width, raster.Height) * 0.2), max);
        }

        if (raster.A is not null)
        {
            Fill(raster.A, 1, 1, (x, y) => 0.6 + (0.4 * Bar(scene, t, x, y, raster.Width, raster.Height)), max);
        }
    }

    private static void Fill<T>(YuvPlane<T> plane, int sx, int sy, Func<double, double, double> value, int max)
        where T : unmanaged, System.Numerics.INumberBase<T>
    {
        for (int y = 0; y < plane.Height; y++)
        {
            Span<T> row = plane.Row(y);
            for (int x = 0; x < plane.Width; x++)
            {
                double v = Math.Clamp(value((x * sx) + ((sx - 1) * 0.5), (y * sy) + ((sy - 1) * 0.5)), 0, 1);
                row[x] = T.CreateTruncating((int)Math.Round(v * max));
            }
        }
    }

    /// <summary>The luma of a scene at a point, from 0 to 1.</summary>
    private static double Luma(string scene, int t, double x, double y, int width, int height)
    {
        double texture = 0.45 + (0.2 * Math.Sin((x * 0.11) + (y * 0.07) - (t * (scene == "still" ? 0 : 0.35)))) + (0.08 * Math.Sin((x * 0.31) - (y * 0.23)));
        double value = scene == "bar" ? 0.3 : texture;
        if (scene is "still" or "mixed" && x > width * 0.6 && y < height * 0.45)
        {
            // Still detail one line high: woven lines must survive this exactly.
            value = ((int)y & 1) == 0 ? 0.85 : 0.2;
        }

        double bar = Bar(scene, t, x, y, width, height);
        value = (value * (1 - bar)) + (0.95 * bar);
        if (scene is "mixed" or "still")
        {
            // A slowly turning thin line through the middle (still in "still").
            double angle = scene == "still" ? 0.6 : 0.6 + (t * 0.02);
            double distance = Math.Abs(((x - (width / 2.0)) * Math.Sin(angle)) - ((y - (height / 2.0)) * Math.Cos(angle)));
            value = distance < 1.5 ? 0.05 : value;
        }

        return value;
    }

    /// <summary>How much of a bright slanted bar covers a point, from 0 to 1, moving 3 pixels a field.</summary>
    private static double Bar(string scene, int t, double x, double y, int width, int height)
    {
        if (scene is not ("bar" or "mixed" or "pan"))
        {
            return 0;
        }

        double left = ((t * 3) % (width + 40)) - 20 + (y * 0.5);
        double edge = Math.Min(x - left, left + 18 - x);
        return y > height * 0.2 && y < height * 0.8 ? Math.Clamp(edge + 0.5, 0, 1) : 0;
    }
}
