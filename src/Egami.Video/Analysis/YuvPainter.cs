using Egami.Pixels;

namespace Egami.Video.Analysis;

/// <summary>
/// Paints rectangles of R'G'B' colors straight into YUV planes, for the test sources: each color is turned into Y, Cb and Cr code
/// values by the raster's matrix, range and bit depth, so bars come out at exactly their specified levels with no conversion
/// pass. Colors are fractions of full scale and may fall outside 0 to 1 (PLUGE's -2 % is below black), which limited range can
/// carry and full range clips.
/// </summary>
internal sealed class YuvPainter
{
    private readonly YuvRaster _raster;
    private readonly double _kr;
    private readonly double _kb;
    private readonly int _max;
    private readonly bool _limited;
    private readonly int _shift;

    public YuvPainter(YuvRaster raster)
    {
        _raster = raster;
        (_kr, _kb) = raster.Color.Matrix switch
        {
            YuvMatrix.Bt601 => (0.299, 0.114),
            YuvMatrix.Bt709 => (0.2126, 0.0722),
            YuvMatrix.Bt2020Ncl => (0.2627, 0.0593),
            _ => throw new ArgumentException($"Test sources need a YUV matrix, not {raster.Color.Matrix}.", nameof(raster)),
        };
        _max = (1 << raster.BitDepth) - 1;
        _limited = raster.Color.Range == YuvRange.Limited;
        _shift = raster.BitDepth - 8;
    }

    public int Width => _raster.Width;

    public int Height => _raster.Height;

    /// <summary>Creates the planes for a test frame.</summary>
    public static YuvRaster Create(TestVideoFormat format) => format.BitDepth == 8
        ? YuvRaster.Create(format.Width, format.Height, format.ChromaFormat, format.Color, context: format.Context?.Egami)
        : YuvRaster.Create(format.Width, format.Height, format.ChromaFormat, format.BitDepth, format.Color, context: format.Context?.Egami);

    /// <summary>Turns an R'G'B' color, as fractions of full scale, into code values.</summary>
    public (int Y, int Cb, int Cr) Code(double r, double g, double b)
    {
        double y = (_kr * r) + ((1 - _kr - _kb) * g) + (_kb * b);
        double cb = (b - y) / (2 * (1 - _kb));
        double cr = (r - y) / (2 * (1 - _kr));
        if (_limited)
        {
            double unit = 1 << _shift;
            return (Clip((16 + (219 * y)) * unit), Clip((128 + (224 * cb)) * unit), Clip((128 + (224 * cr)) * unit));
        }

        double mid = 1 << (_raster.BitDepth - 1);
        return (Clip(y * _max), Clip(mid + (cb * _max)), Clip(mid + (cr * _max)));
    }

    /// <summary>Fills a rectangle, given in luma samples, with an R'G'B' color.</summary>
    public void Fill(int x0, int y0, int x1, int y1, double r, double g, double b)
    {
        (int y, int cb, int cr) = Code(r, g, b);
        FillCodes(x0, y0, x1, y1, y, cb, cr);
    }

    /// <summary>Fills a rectangle with code values.</summary>
    public void FillCodes(int x0, int y0, int x1, int y1, int luma, int cb, int cr)
    {
        x0 = Math.Clamp(x0, 0, Width);
        x1 = Math.Clamp(x1, 0, Width);
        y0 = Math.Clamp(y0, 0, Height);
        y1 = Math.Clamp(y1, 0, Height);
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        if (_raster is YuvRaster<byte> bytes)
        {
            FillPlane(bytes.Y, x0, y0, x1, y1, (byte)luma);
            FillPlane(bytes.U, x0, y0, x1, y1, (byte)cb);
            FillPlane(bytes.V, x0, y0, x1, y1, (byte)cr);
        }
        else if (_raster is YuvRaster<ushort> words)
        {
            FillPlane(words.Y, x0, y0, x1, y1, (ushort)luma);
            FillPlane(words.U, x0, y0, x1, y1, (ushort)cb);
            FillPlane(words.V, x0, y0, x1, y1, (ushort)cr);
        }
    }

    /// <summary>Fills a rectangle with a horizontal luma ramp from <paramref name="from"/> to <paramref name="to"/> (gray levels) and neutral chroma.</summary>
    public void Ramp(int x0, int y0, int x1, int y1, double from, double to)
    {
        int span = Math.Max(1, x1 - x0 - 1);
        for (int x = x0; x < x1; x++)
        {
            double level = from + ((to - from) * (x - x0) / span);
            Fill(x, y0, x + 1, y1, level, level, level);
        }
    }

    /// <summary>Copies every plane of <paramref name="source"/>, which has the same layout, into this painter's raster.</summary>
    public static void Copy(YuvRaster source, YuvRaster target)
    {
        if (source is YuvRaster<byte> a && target is YuvRaster<byte> b)
        {
            CopyPlane(a.Y, b.Y);
            CopyPlane(a.U, b.U);
            CopyPlane(a.V, b.V);
        }
        else if (source is YuvRaster<ushort> c && target is YuvRaster<ushort> d)
        {
            CopyPlane(c.Y, d.Y);
            CopyPlane(c.U, d.U);
            CopyPlane(c.V, d.V);
        }
    }

    private int Clip(double value) => (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, _max);

    private void FillPlane<T>(YuvPlane<T>? plane, int x0, int y0, int x1, int y1, T value)
        where T : unmanaged
    {
        if (plane is null)
        {
            return;
        }

        // Chroma planes cover the same picture at their own size; a rectangle maps to the samples whose centers it covers.
        int px0 = Scale(x0, plane.Width, Width), px1 = Scale(x1, plane.Width, Width);
        int py0 = Scale(y0, plane.Height, Height), py1 = Scale(y1, plane.Height, Height);
        if (px1 == px0 && px0 < plane.Width)
        {
            px1 = px0 + 1;
        }

        if (py1 == py0 && py0 < plane.Height)
        {
            py1 = py0 + 1;
        }

        for (int y = py0; y < py1; y++)
        {
            plane.Row(y)[px0..px1].Fill(value);
        }
    }

    private static int Scale(int position, int size, int full) => size == full ? position : (int)(((long)position * size) + (full / 2)) / full;

    private static void CopyPlane<T>(YuvPlane<T>? source, YuvPlane<T>? target)
        where T : unmanaged
    {
        if (source is null || target is null)
        {
            return;
        }

        for (int y = 0; y < source.Height; y++)
        {
            source.Row(y).CopyTo(target.Row(y));
        }
    }
}
