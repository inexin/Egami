using System.Globalization;

using Egami.Metadata;

namespace Egami.Operations.Geometry;

/// <summary>
/// Where a geometry step moves a point of the input, in pixel coordinates (a pixel's index, so its center is at +0.5), and
/// what it does to a width and height. <see cref="Clip"/> says the output shows only part of the input, so shapes are clipped
/// to it and dropped when their center leaves it.
/// </summary>
internal sealed class SubjectMap(Func<double, double, (double X, double Y)> point, Func<double, double, (double W, double H)> size, double diameterScale, Size bounds, bool clip)
{
    public Size Bounds => bounds;

    public bool Clip => clip;

    public double DiameterScale => diameterScale;

    public (double X, double Y) Point(double x, double y) => point(x, y);

    public (double W, double H) Size(double w, double h) => size(w, h);

    public static SubjectMap Crop(Rect rect) =>
        new((x, y) => (x - rect.X, y - rect.Y), (w, h) => (w, h), 1, rect.Size, true);

    public static SubjectMap Pad(Point offset, Size output) =>
        new((x, y) => (x + offset.X, y + offset.Y), (w, h) => (w, h), 1, output, false);

    /// <summary>A resize of the source window (<paramref name="sx"/>, <paramref name="sy"/>, <paramref name="sw"/>, <paramref name="sh"/>) to <paramref name="output"/>.</summary>
    public static SubjectMap Resize(Size input, double sx, double sy, double sw, double sh, Size output)
    {
        double fx = output.Width / sw, fy = output.Height / sh;
        bool window = sx > 0 || sy > 0 || sw < input.Width || sh < input.Height;
        return new(((x, y) => (((x + 0.5 - sx) * fx) - 0.5, ((y + 0.5 - sy) * fy) - 0.5)), (w, h) => (w * fx, h * fy), Math.Min(fx, fy), output, window);
    }

    public static SubjectMap Flip(Size input, bool horizontal, bool vertical) =>
        new((x, y) => (horizontal ? input.Width - 1 - x : x, vertical ? input.Height - 1 - y : y), (w, h) => (w, h), 1, input, false);

    /// <summary>A quarter turn or transpose: dest(x, y) = source(reversed ? W - 1 - y : y, fromLastRow ? H - 1 - x : x).</summary>
    public static SubjectMap Quarter(Size input, bool fromLastRow, bool reversed) =>
        new((x, y) => (fromLastRow ? input.Height - 1 - y : y, reversed ? input.Width - 1 - x : x), (w, h) => (h, w), 1, new Size(input.Height, input.Width), false);

    /// <summary>A rotation by any angle, clockwise, about the image center onto a canvas of <paramref name="output"/>.</summary>
    public static SubjectMap Rotate(Size input, double degrees, Size output)
    {
        double cos = Math.Cos(degrees * Math.PI / 180), sin = Math.Sin(degrees * Math.PI / 180);
        return new(
            (x, y) =>
            {
                double u = x + 0.5 - (input.Width / 2.0), v = y + 0.5 - (input.Height / 2.0);
                return ((cos * u) - (sin * v) + (output.Width / 2.0) - 0.5, (sin * u) + (cos * v) + (output.Height / 2.0) - 0.5);
            },
            (w, h) => ((w * Math.Abs(cos)) + (h * Math.Abs(sin)), (w * Math.Abs(sin)) + (h * Math.Abs(cos))),
            1,
            output,
            false);
    }
}

/// <summary>
/// Keeps EXIF SubjectArea (0x9214: a point, a circle or a rectangle) and SubjectLocation (0xA214: a point), and their XMP copies
/// (exif:SubjectArea, exif:SubjectLocation), where a geometry step moved the picture (Metadata design, "Keeping metadata in
/// step"). Values are rounded to the nearest pixel and keep their EXIF type (SHORT, or LONG when a value no longer fits); a
/// shape whose size rounds to zero becomes a point. A tag absent from the file is never added.
/// </summary>
internal static class SubjectUpkeep
{
    private static readonly ExifKey SubjectArea = new(0x9214, ExifIfd.Exif);
    private static readonly ExifKey SubjectLocation = new(0xA214, ExifIfd.Exif);
    private static readonly XmpName XmpSubjectArea = new(XmpNamespaces.Exif, "SubjectArea");
    private static readonly XmpName XmpSubjectLocation = new(XmpNamespaces.Exif, "SubjectLocation");

    public static void Apply(RasterMetadata metadata, SubjectMap? map)
    {
        if (metadata.Exif is ExifData exif)
        {
            foreach (ExifKey key in (ExifKey[])[SubjectArea, SubjectLocation])
            {
                if (exif.TryGet(key, out ExifEntry? entry) && entry.Type is ExifType.Short or ExifType.Long && entry.Count is >= 2 and <= 4)
                {
                    long[] values = [.. Enumerable.Range(0, entry.Count).Select(i => entry.AsInteger(i))];
                    if (Move(values, map) is long[] moved)
                    {
                        if (!moved.SequenceEqual(values))
                        {
                            Write(exif, key, entry.Type, moved);
                        }
                    }
                    else
                    {
                        exif.Remove(key);
                    }
                }
            }
        }

        if (XmpUpkeep.View(metadata, "Subject"u8) is XmpData xmp)
        {
            foreach (XmpName name in (XmpName[])[XmpSubjectArea, XmpSubjectLocation])
            {
                if (xmp.TryGet(name, out object? value) && value is XmpArray array && array.Count is >= 2 and <= 4
                    && array.Texts.Count == array.Count && array.Texts.All(t => long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
                {
                    long[] values = [.. array.Texts.Select(t => long.Parse(t, CultureInfo.InvariantCulture))];
                    if (Move(values, map) is long[] moved)
                    {
                        if (!moved.SequenceEqual(values))
                        {
                            xmp.Set(name, XmpArray.Of(array.Kind, moved.Select(v => v.ToString(CultureInfo.InvariantCulture))));
                        }
                    }
                    else
                    {
                        xmp.Remove(name);
                    }
                }
            }
        }
    }

    /// <summary>The shape moved; null when it leaves the picture or the step's effect is unknown.</summary>
    internal static long[]? Move(long[] values, SubjectMap? map)
    {
        if (map is null)
        {
            return null;
        }

        (double x, double y) = map.Point(values[0], values[1]);
        double width = map.Bounds.Width, height = map.Bounds.Height;
        if (map.Clip && (x < 0 || y < 0 || x > width - 1 || y > height - 1))
        {
            return null;
        }

        switch (values.Length)
        {
            case 3:
                {
                    double d = values[2] * map.DiameterScale;
                    if (map.Clip)
                    {
                        // The circle keeps its center and shrinks to fit.
                        d = Math.Min(d, 2 * Math.Min(Math.Min(x, width - x), Math.Min(y, height - y)));
                    }

                    long diameter = Round(d);
                    return diameter == 0 ? [Round(x), Round(y)] : [Round(x), Round(y), diameter];
                }

            case 4:
                {
                    (double w, double h) = map.Size(values[2], values[3]);
                    if (map.Clip)
                    {
                        (x, w) = Clipped(x, w, width);
                        (y, h) = Clipped(y, h, height);
                    }

                    long rw = Round(w), rh = Round(h);
                    return rw == 0 || rh == 0 ? [Round(x), Round(y)] : [Round(x), Round(y), rw, rh];
                }

            default:
                return [Round(x), Round(y)];
        }
    }

    // A span centered at c with the given size, clipped to 0..limit: its new center and size.
    private static (double Center, double Size) Clipped(double c, double size, double limit)
    {
        double low = Math.Max(0, c - (size / 2)), high = Math.Min(limit, c + (size / 2));
        return ((low + high) / 2, Math.Max(0, high - low));
    }

    private static long Round(double v) => (long)Math.Round(Math.Max(0, v), MidpointRounding.AwayFromZero);

    private static void Write(ExifData exif, ExifKey key, ExifType type, long[] values)
    {
        bool fits = type == ExifType.Short && values.All(v => v <= ushort.MaxValue);
        byte[] bytes = new byte[values.Length * (fits ? 2 : 4)];
        for (int i = 0; i < values.Length; i++)
        {
            if (fits && exif.IsBigEndian)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), (ushort)values[i]);
            }
            else if (fits)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)values[i]);
            }
            else if (exif.IsBigEndian)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), (uint)Math.Min(values[i], uint.MaxValue));
            }
            else
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), (uint)Math.Min(values[i], uint.MaxValue));
            }
        }

        exif.Set(key, fits ? ExifType.Short : ExifType.Long, bytes);
    }
}

/// <summary>The XMP copies of what EXIF upkeep changes: tiff:Orientation, exif:PixelXDimension and exif:PixelYDimension.</summary>
internal static class XmpUpkeep
{
    /// <summary>
    /// The parsed packet, when there is one that may hold <paramref name="localName"/>; a quick look at the bytes first, so an
    /// operation never parses a packet it has nothing to change in.
    /// </summary>
    public static XmpData? View(RasterMetadata metadata, ReadOnlySpan<byte> localName) =>
        !metadata.Xmp.IsEmpty && metadata.Xmp.Span.IndexOf(localName) >= 0 ? metadata.XmpData : null;

    public static void Upright(RasterMetadata metadata)
    {
        if (View(metadata, "Orientation"u8) is XmpData xmp && xmp.TryGet(new XmpName(XmpNamespaces.Tiff, "Orientation"), out object? value)
            && value is string text && text.Trim() != "1")
        {
            xmp.Orientation = ImageOrientation.Normal;
        }
    }

    public static void Dimensions(RasterMetadata metadata, Size size)
    {
        if (View(metadata, "Dimension"u8) is not XmpData xmp)
        {
            return;
        }

        foreach ((string name, int value) in ((string, int)[])[("PixelXDimension", size.Width), ("PixelYDimension", size.Height)])
        {
            XmpName key = new(XmpNamespaces.Exif, name);
            string text = value.ToString(CultureInfo.InvariantCulture);
            if (xmp.TryGet(key, out object? old) && old is string s && s.Trim() != text)
            {
                xmp.Set(key, text);
            }
        }
    }
}
