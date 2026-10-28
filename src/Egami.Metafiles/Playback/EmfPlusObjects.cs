using System.Numerics;
using System.Text;

using Egami.Drawing;
using Egami.GdiPlus;
using Egami.Pixels;

using static Egami.Metafiles.Reading.MetafileReader;

namespace Egami.Metafiles.Playback;

/// <summary>An EMF+ object kept in the 64-entry object table.</summary>
internal abstract class PlusObject;

/// <summary>An EMF+ brush ([MS-EMFPLUS] 2.2.1.1): solid, hatch, texture, path gradient or linear gradient.</summary>
internal sealed class PlusBrush : PlusObject
{
    public const int SolidType = 0, HatchType = 1, TextureType = 2, PathGradientType = 3, LinearGradientType = 4;

    public int Type { get; init; }

    public Rgba8 Color { get; init; }

    public int HatchStyle { get; init; }

    public Rgba8 BackColor { get; init; }

    public int WrapMode { get; init; }

    public Matrix3x2 Transform { get; init; } = Matrix3x2.Identity;

    public PlusImage? Texture { get; init; }

    /// <summary>Gets a linear gradient's rectangle (start and end are its left and right edges before the transform).</summary>
    public RectF Rect { get; init; }

    public Rgba8 EndColor { get; init; }

    /// <summary>Gets the stops of a gradient: positions 0 to 1 and colors, from its blend factors or preset colors.</summary>
    public GradientStop[]? Stops { get; init; }

    public bool GammaCorrected { get; init; }

    /// <summary>Gets a path gradient's center point and color.</summary>
    public Vector2 Center { get; init; }

    /// <summary>Gets a path gradient's surrounding colors, one per boundary point (the last repeats).</summary>
    public Rgba8[] Surround { get; init; } = [];

    /// <summary>Gets a path gradient's boundary, in world units.</summary>
    public Path? Boundary { get; init; }

    /// <summary>Gets a path gradient's boundary points, when it was given as points.</summary>
    public Vector2[]? BoundaryPoints { get; init; }

    /// <summary>Gets a path gradient's focus scales.</summary>
    public Vector2 Focus { get; init; }
}

/// <summary>An EMF+ pen ([MS-EMFPLUS] 2.2.1.7): its GDI+ geometry and its brush.</summary>
internal sealed class PlusPen : PlusObject
{
    public required GdiPlusPen Pen { get; init; }

    public required PlusBrush Brush { get; init; }
}

/// <summary>An EMF+ path ([MS-EMFPLUS] 2.2.1.6), as an Egami path in world units.</summary>
internal sealed class PlusPath : PlusObject
{
    public required Path Path { get; init; }
}

/// <summary>An EMF+ region ([MS-EMFPLUS] 2.2.1.8): a tree of rectangles and paths, as a path in world units, or infinite.</summary>
internal sealed class PlusRegion : PlusObject
{
    /// <summary>Gets the region, or null for an infinite one.</summary>
    public Path? Path { get; init; }
}

/// <summary>An EMF+ image ([MS-EMFPLUS] 2.2.1.4): a bitmap (with its encoded bytes when compressed) or a metafile.</summary>
internal sealed class PlusImage : PlusObject
{
    public Raster<Rgba8>? Bitmap { get; init; }

    /// <summary>Gets a compressed bitmap's file bytes (PNG, JPEG, GIF...), for passing JPEG and PNG through unchanged.</summary>
    public byte[]? Encoded { get; init; }

    public Metafile? Metafile { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }
}

/// <summary>An EMF+ font ([MS-EMFPLUS] 2.2.1.5).</summary>
internal sealed class PlusFont : PlusObject
{
    public float EmSize { get; init; }

    public int Unit { get; init; }

    public int Style { get; init; }

    public string Family { get; init; } = "";
}

/// <summary>An EMF+ string format ([MS-EMFPLUS] 2.2.1.9).</summary>
internal sealed class PlusStringFormat : PlusObject
{
    public GdiPlusStringFormat Format { get; init; } = new();
}

/// <summary>EMF+ image attributes ([MS-EMFPLUS] 2.2.1.5): the wrap mode and clamp color.</summary>
internal sealed class PlusImageAttributes : PlusObject
{
    public required GdiPlusImageAttributes Attributes { get; init; }
}

/// <summary>Reads EMF+ objects from their serialized form; every read is bounded by the data and throws on a short object.</summary>
internal static class PlusObjectReader
{
    /// <summary>Reads an object of the given type (the EmfPlusObject record's flags bits 8 to 14).</summary>
    public static PlusObject? Read(int type, ReadOnlySpan<byte> d, Func<byte[], Metafile?> loadMetafile, Func<byte[], Raster<Rgba8>?> loadImage)
    {
        int at = 0;
        return type switch
        {
            1 => Brush(d, ref at, loadMetafile, loadImage),
            2 => Pen(d, ref at, loadMetafile, loadImage),
            3 => new PlusPath { Path = GdiPlusData.PathData(d, ref at) },
            4 => new PlusRegion { Path = GdiPlusData.Region(d, ref at) },
            5 => Image(d, ref at, loadMetafile, loadImage),
            6 => Font(d, ref at),
            7 => StringFormat(d, ref at),
            8 => new PlusImageAttributes { Attributes = new GdiPlusImageAttributes { WrapMode = I32(d, 8), ClampColor = Argb(U32(d, 12)) } },
            _ => null,
        };
    }

    public static Rgba8 Argb(uint v) => new((byte)(v >> 16), (byte)(v >> 8), (byte)v, (byte)(v >> 24));

    private static float F(ReadOnlySpan<byte> d, ref int at)
    {
        float v = F32(d, at);
        at += 4;
        return v;
    }

    private static int I(ReadOnlySpan<byte> d, ref int at)
    {
        int v = I32(d, at);
        at += 4;
        return v;
    }

    private static Matrix3x2 Matrix(ReadOnlySpan<byte> d, ref int at)
    {
        Matrix3x2 m = new(F32(d, at), F32(d, at + 4), F32(d, at + 8), F32(d, at + 12), F32(d, at + 16), F32(d, at + 20));
        at += 24;
        return m;
    }

    /// <summary>A count read from the data, bounded by how many items of the given size the rest of it holds.</summary>
    private static int Count(ReadOnlySpan<byte> d, ref int at, int itemSize)
    {
        int n = I(d, ref at);
        if (n < 0 || n > (d.Length - at) / Math.Max(1, itemSize))
        {
            throw new ArgumentException("A count in an EMF+ object does not fit its data.");
        }

        return n;
    }

    public static PlusBrush Brush(ReadOnlySpan<byte> d, ref int at, Func<byte[], Metafile?> loadMetafile, Func<byte[], Raster<Rgba8>?> loadImage)
    {
        at += 4; // version
        int type = I(d, ref at);
        switch (type)
        {
            case PlusBrush.SolidType:
                return new PlusBrush { Type = type, Color = Argb(U32(d, at)) };
            case PlusBrush.HatchType:
                return new PlusBrush { Type = type, HatchStyle = I32(d, at), Color = Argb(U32(d, at + 4)), BackColor = Argb(U32(d, at + 8)) };
            case PlusBrush.TextureType:
                {
                    int flags = I(d, ref at), wrap = I(d, ref at);
                    Matrix3x2 transform = (flags & 0x02) != 0 ? Matrix(d, ref at) : Matrix3x2.Identity;
                    PlusImage image = Image(d, ref at, loadMetafile, loadImage);
                    return new PlusBrush { Type = type, WrapMode = wrap, Transform = transform, Texture = image };
                }

            case PlusBrush.LinearGradientType:
                {
                    int flags = I(d, ref at), wrap = I(d, ref at);
                    RectF rect = new(F(d, ref at), F(d, ref at), F(d, ref at), F(d, ref at));
                    Rgba8 start = Argb(U32(d, at)), end = Argb(U32(d, at + 4));
                    at += 16; // the two colors and two reserved colors
                    Matrix3x2 transform = (flags & 0x02) != 0 ? Matrix(d, ref at) : Matrix3x2.Identity;
                    GradientStop[]? stops = null;
                    if ((flags & 0x04) != 0)
                    {
                        stops = PresetColors(d, ref at);
                    }
                    else if ((flags & 0x08) != 0)
                    {
                        stops = BlendFactors(d, ref at, start, end, (flags & 0x80) != 0);
                    }

                    if ((flags & 0x10) != 0 && stops is null)
                    {
                        stops = BlendFactors(d, ref at, start, end, (flags & 0x80) != 0);
                    }

                    return new PlusBrush { Type = type, WrapMode = wrap, Rect = rect, Color = start, EndColor = end, Transform = transform, Stops = stops, GammaCorrected = (flags & 0x80) != 0 };
                }

            case PlusBrush.PathGradientType:
                {
                    int flags = I(d, ref at), wrap = I(d, ref at);
                    Rgba8 center = Argb(U32(d, at));
                    at += 4;
                    Vector2 centerPoint = new(F(d, ref at), F(d, ref at));
                    int n = Count(d, ref at, 4);
                    Rgba8[] surround = new Rgba8[n];
                    for (int i = 0; i < n; i++)
                    {
                        surround[i] = Argb(U32(d, at + (i * 4)));
                    }

                    at += n * 4;
                    Path? boundary = null;
                    Vector2[]? points = null;
                    if ((flags & 0x01) != 0)
                    {
                        int size = I(d, ref at);
                        int end = at + size;
                        boundary = GdiPlusData.PathData(d, ref at);
                        at = end;
                    }
                    else
                    {
                        int count = Count(d, ref at, 8);
                        points = new Vector2[count];
                        for (int i = 0; i < count; i++)
                        {
                            points[i] = new Vector2(F(d, ref at), F(d, ref at));
                        }

                        boundary = Path.Polygon(points);
                    }

                    Matrix3x2 transform = (flags & 0x02) != 0 ? Matrix(d, ref at) : Matrix3x2.Identity;
                    GradientStop[]? stops = null;
                    if ((flags & 0x04) != 0)
                    {
                        stops = PresetColors(d, ref at);
                    }
                    else if ((flags & 0x08) != 0)
                    {
                        // Measured against GDI+: a blend position runs from the center (0) to the boundary (1), and its factor from the
                        // center color (0) to the surround color (1). The stops are kept from the boundary, as preset colors are.
                        stops = [.. BlendFactors(d, ref at, center, surround.Length > 0 ? surround[0] : center).Select(s => new GradientStop(1 - s.Offset, s.Color))];
                    }

                    Vector2 focus = Vector2.Zero;
                    if ((flags & 0x40) != 0 && at + 12 <= d.Length)
                    {
                        at += 4;
                        focus = new Vector2(F(d, ref at), F(d, ref at));
                    }

                    return new PlusBrush
                    {
                        Type = type,
                        WrapMode = wrap,
                        Color = center,
                        Center = centerPoint,
                        Surround = surround,
                        Boundary = boundary,
                        BoundaryPoints = points,
                        Transform = transform,
                        Stops = stops,
                        Focus = focus,
                    };
                }

            default:
                throw new ArgumentException($"EMF+ brush type {type} is not known.");
        }
    }

    /// <summary>EmfPlusBlendColors: a count, positions, then ARGB colors.</summary>
    private static GradientStop[] PresetColors(ReadOnlySpan<byte> d, ref int at)
    {
        int n = Count(d, ref at, 8);
        GradientStop[] stops = new GradientStop[n];
        for (int i = 0; i < n; i++)
        {
            stops[i] = GdiPlusBrushes.PresetStop(F32(d, at + (i * 4)), Argb(U32(d, at + (n * 4) + (i * 4))));
        }

        at += n * 8;
        return stops;
    }

    /// <summary>
    /// EmfPlusBlendFactors: a count, positions, then factors from the start color (0) to the end color (1); a gamma-corrected
    /// brush mixes the two colors in linear light.
    /// </summary>
    private static GradientStop[] BlendFactors(ReadOnlySpan<byte> d, ref int at, Rgba8 start, Rgba8 end, bool gamma = false)
    {
        int n = Count(d, ref at, 8);
        GradientStop[] stops = new GradientStop[n];
        for (int i = 0; i < n; i++)
        {
            stops[i] = GdiPlusBrushes.BlendStop(F32(d, at + (i * 4)), F32(d, at + (n * 4) + (i * 4)), start, end, gamma);
        }

        at += n * 8;
        return stops;
    }

    private static PlusPen Pen(ReadOnlySpan<byte> d, ref int at, Func<byte[], Metafile?> loadMetafile, Func<byte[], Raster<Rgba8>?> loadImage)
    {
        at += 8; // version, type
        int flags = I(d, ref at), unit = I(d, ref at);
        float width = F(d, ref at);
        Matrix3x2 transform = (flags & 0x0001) != 0 ? Matrix(d, ref at) : Matrix3x2.Identity;
        int startCap = (flags & 0x0002) != 0 ? I(d, ref at) : 0;
        int endCap = (flags & 0x0004) != 0 ? I(d, ref at) : 0;
        int join = (flags & 0x0008) != 0 ? I(d, ref at) : 0;
        float miter = (flags & 0x0010) != 0 ? F(d, ref at) : 10;
        int style = (flags & 0x0020) != 0 ? I(d, ref at) : 0;
        int dashCap = (flags & 0x0040) != 0 ? I(d, ref at) : 0;
        float dashOffset = (flags & 0x0080) != 0 ? F(d, ref at) : 0;
        float[]? dashes = null;
        if ((flags & 0x0100) != 0)
        {
            int n = Count(d, ref at, 4);
            dashes = new float[n];
            for (int i = 0; i < n; i++)
            {
                dashes[i] = F(d, ref at);
            }
        }

        int alignment = (flags & 0x0200) != 0 ? I(d, ref at) : 0;
        float[]? compound = null;
        if ((flags & 0x0400) != 0)
        {
            int n = Count(d, ref at, 4);
            compound = new float[n];
            for (int i = 0; i < n; i++)
            {
                compound[i] = F(d, ref at);
            }
        }

        GdiPlusCustomCap? customStart = null, customEnd = null;
        if ((flags & 0x0800) != 0)
        {
            int size = I(d, ref at);
            customStart = Cap(d.Slice(at, Math.Min(size, d.Length - at)));
            at += size;
        }

        if ((flags & 0x1000) != 0)
        {
            int size = I(d, ref at);
            customEnd = Cap(d.Slice(at, Math.Min(size, d.Length - at)));
            at += size;
        }

        PlusBrush brush = Brush(d, ref at, loadMetafile, loadImage);
        GdiPlusPen pen = new()
        {
            Width = width,
            Unit = unit,
            Transform = transform,
            StartCap = startCap,
            EndCap = endCap,
            Join = join,
            MiterLimit = miter,
            DashStyle = style,
            DashCap = dashCap,
            DashOffset = dashOffset,
            Dashes = dashes,
            Alignment = alignment,
            Compound = compound,
            CustomStartCap = customStart,
            CustomEndCap = customEnd,
        };
        return new PlusPen { Pen = pen, Brush = brush };
    }

    /// <summary>EmfPlusCustomLineCap: version, type (0 default with path data, 1 adjustable arrow), then its data.</summary>
    private static GdiPlusCustomCap? Cap(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8)
        {
            return null;
        }

        int type = I32(d, 4);
        if (type == 1 && d.Length >= 8 + 16)
        {
            // Width, Height, MiddleInset, FillState, then the line cap fields.
            return new GdiPlusCustomCap { Arrow = true, Width = F32(d, 8), Height = F32(d, 12), MiddleInset = F32(d, 16), Filled = I32(d, 20) != 0 };
        }

        if (type == 0 && d.Length >= 8 + 32)
        {
            // CustomLineCapDataFlags, BaseCap, BaseInset, StrokeStartCap, StrokeEndCap, StrokeJoin, StrokeMiterLimit, WidthScale,
            // FillHotSpot, StrokeHotSpot, then the fill or line path.
            int flags = I32(d, 8);
            float baseInset = F32(d, 16);
            int at = 8 + 56;
            Path? shape = null;
            if (at + 4 <= d.Length)
            {
                int size = I32(d, at);
                at += 4;
                if (size > 0 && at + size <= d.Length)
                {
                    int p = at;
                    shape = GdiPlusData.PathData(d, ref p);
                }
            }

            return new GdiPlusCustomCap { Filled = (flags & 1) != 0, Shape = shape, BaseInset = baseInset };
        }

        return null;
    }

    public static PlusImage Image(ReadOnlySpan<byte> d, ref int at, Func<byte[], Metafile?> loadMetafile, Func<byte[], Raster<Rgba8>?> loadImage)
    {
        at += 4; // version
        int type = I(d, ref at);
        if (type == 2)
        {
            // A metafile image: its type, size, then the file.
            at += 4;
            int size = Count(d, ref at, 1);
            byte[] bytes = d.Slice(at, size).ToArray();
            at += size;
            return new PlusImage { Metafile = loadMetafile(bytes) };
        }

        int width = I(d, ref at), height = I(d, ref at), stride = I(d, ref at), format = I(d, ref at), kind = I(d, ref at);
        if (kind == 1)
        {
            byte[] bytes = d[at..].ToArray();
            at = d.Length;
            return new PlusImage { Bitmap = loadImage(bytes), Encoded = bytes, Width = width, Height = height };
        }

        return new PlusImage { Bitmap = PixelData(d[at..], width, height, stride, format), Width = width, Height = height };
    }

    /// <summary>Uncompressed EMF+ bitmap pixels in the common formats; others are not decoded.</summary>
    private static Raster<Rgba8>? PixelData(ReadOnlySpan<byte> d, int width, int height, int stride, int format)
    {
        if (width <= 0 || height <= 0 || width > 65535 || height > 65535 || stride <= 0)
        {
            return null;
        }

        Rgba8[] palette = [];
        int at = 0;
        if ((format & 0x00010000) != 0 && d.Length >= 8)
        {
            // Indexed: a palette comes first (flags, count, ARGB entries).
            int n = Math.Clamp(I32(d, 4), 0, Math.Max(0, (d.Length - 8) / 4));
            palette = new Rgba8[n];
            for (int i = 0; i < n; i++)
            {
                palette[i] = Argb(U32(d, 8 + (i * 4)));
            }

            at = 8 + (n * 4);
        }

        if ((long)stride * height > d.Length - at)
        {
            return null;
        }

        int bits = (format >> 8) & 0xFF;
        Raster<Rgba8> image = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = d.Slice(at + (y * stride), stride);
            Span<Rgba8> o = image.Row(y);
            for (int x = 0; x < width; x++)
            {
                o[x] = (format, bits) switch
                {
                    (0x0026200A, _) when (x * 4) + 3 < row.Length => new Rgba8(row[(x * 4) + 2], row[(x * 4) + 1], row[x * 4], row[(x * 4) + 3]),
                    (0x000E200B, _) when (x * 4) + 3 < row.Length => Unpremultiply(row[(x * 4) + 2], row[(x * 4) + 1], row[x * 4], row[(x * 4) + 3]),
                    (0x00022009, _) when (x * 4) + 3 < row.Length => new Rgba8(row[(x * 4) + 2], row[(x * 4) + 1], row[x * 4], 255),
                    (0x00021808, _) when (x * 3) + 2 < row.Length => new Rgba8(row[(x * 3) + 2], row[(x * 3) + 1], row[x * 3], 255),
                    (_, 8) when x < row.Length && row[x] < palette.Length => palette[row[x]],
                    (_, 4) when (x >> 1) < row.Length => Index(palette, (row[x >> 1] >> ((1 - (x & 1)) * 4)) & 0xF),
                    (_, 1) when (x >> 3) < row.Length => Index(palette, (row[x >> 3] >> (7 - (x & 7))) & 1),
                    _ => default,
                };
            }
        }

        return image;
    }

    private static Rgba8 Index(Rgba8[] palette, int i) => i < palette.Length ? palette[i] : default;

    private static Rgba8 Unpremultiply(byte r, byte g, byte b, byte a) =>
        a == 0 ? default : new Rgba8((byte)Math.Min(255, r * 255 / a), (byte)Math.Min(255, g * 255 / a), (byte)Math.Min(255, b * 255 / a), a);

    private static PlusFont Font(ReadOnlySpan<byte> d, ref int at)
    {
        at += 4; // version
        float em = F(d, ref at);
        int unit = I(d, ref at), style = I(d, ref at);
        at += 4; // reserved
        int length = Count(d, ref at, 2);
        string family = Encoding.Unicode.GetString(d.Slice(at, length * 2));
        return new PlusFont { EmSize = em, Unit = unit, Style = style, Family = family };
    }

    private static PlusStringFormat StringFormat(ReadOnlySpan<byte> d, ref int at)
    {
        at += 4; // version
        int flags = I(d, ref at);
        at += 4; // language
        int alignment = I(d, ref at), lineAlignment = I(d, ref at);
        at += 8; // digit substitution and language
        float firstTab = F(d, ref at);
        int hotkey = I(d, ref at);
        float leading = F(d, ref at), trailing = F(d, ref at), tracking = F(d, ref at);
        int trimming = I(d, ref at), tabCount = I(d, ref at);
        at += 4; // range count
        float[] tabs = new float[Math.Clamp(tabCount, 0, Math.Max(0, (d.Length - at) / 4))];
        for (int i = 0; i < tabs.Length; i++)
        {
            tabs[i] = F(d, ref at);
        }

        return new PlusStringFormat
        {
            Format = new GdiPlusStringFormat
            {
                Flags = flags,
                Alignment = alignment,
                LineAlignment = lineAlignment,
                HotkeyPrefix = hotkey,
                LeadingMargin = leading,
                TrailingMargin = trailing,
                Tracking = tracking,
                Trimming = trimming,
                FirstTabOffset = firstTab,
                TabStops = tabs,
            },
        };
    }
}
