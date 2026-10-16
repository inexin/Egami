using System.Numerics;
using System.Text;

using Egami.Drawing;
using Egami.Pixels;

using static Egami.Metafiles.Reading.MetafileReader;

namespace Egami.Metafiles.Playback;

/// <summary>The record dispatch: EMF records ([MS-EMF] 2.3) and WMF records ([MS-WMF] 2.3), each read into the shared operations.</summary>
internal sealed partial class GdiPlayer
{
    // ---- EMF ----

    private void PlayEmf(int type, ReadOnlySpan<byte> r, long offset)
    {
        switch (type)
        {
            case 1: // EMR_HEADER
            case 14: // EMR_EOF
            case 70: // EMR_GDICOMMENT (EMF+ and public comments are read elsewhere)
                break;
            case 2: // EMR_POLYBEZIER
                PolyBezier(Points32(r, 24, I32(r, 24)), to: false);
                break;
            case 3:
                Polygon(Points32(r, 24, I32(r, 24)));
                break;
            case 4:
                Polyline(Points32(r, 24, I32(r, 24)));
                break;
            case 5:
                PolyBezier(Points32(r, 24, I32(r, 24)), to: true);
                break;
            case 6:
                PolylineTo(Points32(r, 24, I32(r, 24)));
                break;
            case 7: // EMR_POLYPOLYLINE
                PolyPolyline(PolyPoints(r, wide: true));
                break;
            case 8:
                PolyPolygon(PolyPoints(r, wide: true));
                break;
            case 9:
                SetWindowExt(new Vector2(I32(r, 8), I32(r, 12)));
                break;
            case 10:
                SetWindowOrg(new Vector2(I32(r, 8), I32(r, 12)));
                break;
            case 11:
                SetViewportExt(new Vector2(I32(r, 8), I32(r, 12)));
                break;
            case 12:
                SetViewportOrg(new Vector2(I32(r, 8), I32(r, 12)));
                break;
            case 13:
                _dc.BrushOrg = new Vector2(I32(r, 8), I32(r, 12));
                break;
            case 15: // EMR_SETPIXELV
                SetPixel(new Vector2(I32(r, 8), I32(r, 12)), Color(U32(r, 16)));
                break;
            case 16: // EMR_SETMAPPERFLAGS
            case 21: // EMR_SETSTRETCHBLTMODE
                _dc.StretchMode = type == 21 ? I32(r, 8) : _dc.StretchMode;
                break;
            case 17:
                SetMapMode(I32(r, 8));
                break;
            case 18:
                _dc.BkMode = I32(r, 8);
                break;
            case 19:
                _dc.PolyFillMode = I32(r, 8);
                break;
            case 20:
                _dc.Rop2 = I32(r, 8);
                break;
            case 22:
                _dc.TextAlign = U32(r, 8);
                break;
            case 24:
                _dc.TextColor = Color(U32(r, 8));
                break;
            case 25:
                _dc.BkColor = Color(U32(r, 8));
                break;
            case 26:
                OffsetClip(new Vector2(I32(r, 8), I32(r, 12)));
                break;
            case 27:
                MoveTo(new Vector2(I32(r, 8), I32(r, 12)));
                break;
            case 29:
                ExcludeClipRect(I32(r, 8), I32(r, 12), I32(r, 16), I32(r, 20));
                break;
            case 30:
                IntersectClipRect(I32(r, 8), I32(r, 12), I32(r, 16), I32(r, 20));
                break;
            case 31:
                ScaleViewportExt(I32(r, 8), I32(r, 12), I32(r, 16), I32(r, 20));
                break;
            case 32:
                ScaleWindowExt(I32(r, 8), I32(r, 12), I32(r, 16), I32(r, 20));
                break;
            case 33:
                SaveDC();
                break;
            case 34:
                RestoreDC(I32(r, 8));
                break;
            case 35:
                SetWorldTransform(XForm(r, 8));
                break;
            case 36:
                ModifyWorldTransform(XForm(r, 8), I32(r, 32));
                break;
            case 37:
                Select(U32(r, 8));
                break;
            case 38: // EMR_CREATEPEN: LOGPEN
                Store(U32(r, 8), Pen(U32(r, 12), I32(r, 16), U32(r, 24), geometric: false, null));
                break;
            case 39: // EMR_CREATEBRUSHINDIRECT: LOGBRUSH32
                Store(U32(r, 8), Brush(U32(r, 12), U32(r, 16), U32(r, 20)));
                break;
            case 40:
                Delete(U32(r, 8));
                break;
            case 41: // EMR_ANGLEARC
                AngleArc(new Vector2(I32(r, 8), I32(r, 12)), U32(r, 16), F32(r, 20), F32(r, 24));
                break;
            case 42:
                {
                    (float l, float t, float rt, float b) = Inclusive(I32(r, 8), I32(r, 12), I32(r, 16), I32(r, 20));
                    Ellipse(l, t, rt, b);
                    break;
                }

            case 43:
                {
                    (float l, float t, float rt, float b) = Inclusive(I32(r, 8), I32(r, 12), I32(r, 16), I32(r, 20));
                    Rectangle(l, t, rt, b);
                    break;
                }

            case 44:
                {
                    (float l, float t, float rt, float b) = Inclusive(I32(r, 8), I32(r, 12), I32(r, 16), I32(r, 20));
                    RoundRect(l, t, rt, b, I32(r, 24), I32(r, 28));
                    break;
                }

            case 45:
            case 46:
            case 47:
            case 55:
                {
                    (float l, float t, float rt, float b) = Inclusive(I32(r, 8), I32(r, 12), I32(r, 16), I32(r, 20));
                    Arc(type == 55 ? 3 : type - 45, l, t, rt, b, new Vector2(I32(r, 24), I32(r, 28)), new Vector2(I32(r, 32), I32(r, 36)));
                    break;
                }
            case 48: // EMR_SELECTPALETTE
                Select(U32(r, 8));
                break;
            case 49: // EMR_CREATEPALETTE
                Store(U32(r, 8), Palette(r[12..]));
                break;
            case 50: // EMR_SETPALETTEENTRIES
            case 51: // EMR_RESIZEPALETTE
            case 52: // EMR_REALIZEPALETTE
            case 98: // EMR_SETICMMODE
            case 99: // EMR_CREATECOLORSPACE
            case 100: // EMR_SETCOLORSPACE
            case 101: // EMR_DELETECOLORSPACE
            case 111: // EMR_COLORCORRECTPALETTE
            case 112: // EMR_SETICMPROFILEA
            case 113: // EMR_SETICMPROFILEW
            case 115: // EMR_SETLAYOUT
            case 119: // EMR_SETLINKEDUFIS
            case 120: // EMR_SETTEXTJUSTIFICATION
            case 121: // EMR_COLORMATCHTOTARGETW
            case 122: // EMR_CREATECOLORSPACEW
            case 109: // EMR_FORCEUFIMAPPING
            case 23: // EMR_SETCOLORADJUSTMENT
            case 28: // EMR_SETMETARGN
                break;
            case 54:
                LineTo(new Vector2(I32(r, 8), I32(r, 12)));
                break;
            case 56: // EMR_POLYDRAW: points, then one type byte each
                {
                    int n = I32(r, 24);
                    PolyDraw(Points32(r, 24, n), r.Slice(28 + (n * 8), n));
                    break;
                }

            case 57:
                _dc.ArcClockwise = I32(r, 8) == 2;
                break;
            case 58: // EMR_SETMITERLIMIT: GDI writes a float there, the specification an integer
                {
                    uint raw = U32(r, 8);
                    float f = BitConverter.UInt32BitsToSingle(raw);
                    _dc.MiterLimit = raw is > 0 and < 1000 ? raw : (float.IsFinite(f) && f > 0 ? f : 10);
                    break;
                }

            case 59:
                BeginPath();
                break;
            case 60:
                EndPath();
                break;
            case 61:
                CloseFigure();
                break;
            case 62:
                FillPath(stroke: false);
                break;
            case 63:
                FillPath(stroke: true);
                break;
            case 64:
                StrokePath();
                break;
            case 65:
                FlattenPath();
                break;
            case 66:
                WidenPath();
                break;
            case 67:
                SelectClipPath(I32(r, 8));
                break;
            case 68:
                AbortPath();
                break;
            case 71: // EMR_FILLRGN
                FillRegion(RegionData(r.Slice(32, Math.Min(I32(r, 24), r.Length - 32)), DeviceRegion), Object<GdiBrush>(U32(r, 28)));
                break;
            case 72: // EMR_FRAMERGN
                FrameRegion(RegionData(r.Slice(40, Math.Min(I32(r, 24), r.Length - 40)), DeviceRegion), Object<GdiBrush>(U32(r, 28)), new Vector2(I32(r, 32), I32(r, 36)));
                break;
            case 73:
                InvertRegion(RegionData(r.Slice(28, Math.Min(I32(r, 24), r.Length - 28)), DeviceRegion));
                break;
            case 74:
                PaintRegion(RegionData(r.Slice(28, Math.Min(I32(r, 24), r.Length - 28)), DeviceRegion));
                break;
            case 75: // EMR_EXTSELECTCLIPRGN
                {
                    int size = I32(r, 8);
                    CombineClip(size <= 0 ? null : RegionData(r.Slice(16, Math.Min(size, r.Length - 16)), DeviceRegion, clip: true), I32(r, 12));
                    break;
                }

            case 85:
                PolyBezier(Points16(r, 24, I32(r, 24)), to: false);
                break;
            case 86:
                Polygon(Points16(r, 24, I32(r, 24)));
                break;
            case 87:
                Polyline(Points16(r, 24, I32(r, 24)));
                break;
            case 88:
                PolyBezier(Points16(r, 24, I32(r, 24)), to: true);
                break;
            case 89:
                PolylineTo(Points16(r, 24, I32(r, 24)));
                break;
            case 90:
                PolyPolyline(PolyPoints(r, wide: false));
                break;
            case 91:
                PolyPolygon(PolyPoints(r, wide: false));
                break;
            case 92:
                {
                    int n = I32(r, 24);
                    PolyDraw(Points16(r, 24, n), r.Slice(28 + (n * 4), n));
                    break;
                }

            case 95: // EMR_EXTCREATEPEN
                Store(U32(r, 8), ExtPen(r));
                break;
            case 82: // EMR_EXTCREATEFONTINDIRECTW
                Store(U32(r, 8), FontW(r[12..]));
                break;
            case 93: // EMR_CREATEMONOBRUSH
            case 94: // EMR_CREATEDIBPATTERNBRUSHPT
                Store(U32(r, 8), DibBrush(r, I32(r, 12), I32(r, 16), I32(r, 20), I32(r, 24), I32(r, 28)));
                break;
            case 76: // EMR_BITBLT
            case 77: // EMR_STRETCHBLT
            case 78: // EMR_MASKBLT
            case 79: // EMR_PLGBLT
            case 80: // EMR_SETDIBITSTODEVICE
            case 81: // EMR_STRETCHDIBITS
            case 114: // EMR_ALPHABLEND
            case 116: // EMR_TRANSPARENTBLT
                PlayEmfBitmap(type, r, offset);
                break;
            case 83: // EMR_EXTTEXTOUTA
            case 84: // EMR_EXTTEXTOUTW
            case 96: // EMR_POLYTEXTOUTA
            case 97: // EMR_POLYTEXTOUTW
            case 108: // EMR_SMALLTEXTOUT
                PlayEmfText(type, r, offset);
                break;
            case 118: // EMR_GRADIENTFILL
                GradientFill(r, offset);
                break;
            case 53: // EMR_EXTFLOODFILL
                Skip("Flood fill", offset);
                break;
            case 102: // EMR_GLSRECORD
            case 103: // EMR_GLSBOUNDEDRECORD
            case 104: // EMR_PIXELFORMAT
                Skip("OpenGL", offset);
                break;
            case 105: // EMR_DRAWESCAPE
            case 106: // EMR_EXTESCAPE
            case 110: // EMR_NAMEDESCAPE
                Skip("Escape", offset);
                break;
            default:
                Skip($"EMF type {type}", offset);
                break;
        }
    }

    /// <summary>
    /// An EMF box (Rectangle, Ellipse, RoundRect, the arcs) is inclusive: GDI recorded the call's box less one device pixel on
    /// the right and bottom (in compatible mode) or as given (in advanced mode, where it is drawn inclusive). Playback adds that
    /// device pixel back, in logical units, on the side that maps to the larger device coordinate.
    /// </summary>
    private (float L, float T, float R, float B) Inclusive(float l, float t, float r, float b)
    {
        Matrix3x2 m = PageWorld;
        if (m.M12 != 0 || m.M21 != 0 || m.M11 == 0 || m.M22 == 0)
        {
            return (l, t, r, b);
        }

        float ux = 1 / m.M11, uy = 1 / m.M22;
        if (ux > 0)
        {
            (l, r) = l > r ? (l + ux, r) : (l, r + ux);
        }
        else
        {
            (l, r) = l < r ? (l + ux, r) : (l, r + ux);
        }

        if (uy > 0)
        {
            (t, b) = t > b ? (t + uy, b) : (t, b + uy);
        }
        else
        {
            (t, b) = t < b ? (t + uy, b) : (t, b + uy);
        }

        return (l, t, r, b);
    }

    private T? Object<T>(uint index)
        where T : GdiObject => index < _objects.Length ? _objects[index] as T : null;

    private static Matrix3x2 XForm(ReadOnlySpan<byte> r, int at) =>
        new(F32(r, at), F32(r, at + 4), F32(r, at + 8), F32(r, at + 12), F32(r, at + 16), F32(r, at + 20));

    /// <summary>Points after a count at <paramref name="countAt"/>: POINTL (32-bit) pairs.</summary>
    private static Vector2[] Points32(ReadOnlySpan<byte> r, int countAt, int count)
    {
        int n = (int)Math.Clamp(count, 0, (r.Length - countAt - 4) / 8);
        Vector2[] points = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            points[i] = new Vector2(I32(r, countAt + 4 + (i * 8)), I32(r, countAt + 8 + (i * 8)));
        }

        return points;
    }

    /// <summary>Points after a count: POINTS (16-bit) pairs.</summary>
    private static Vector2[] Points16(ReadOnlySpan<byte> r, int countAt, int count)
    {
        int n = (int)Math.Clamp(count, 0, (r.Length - countAt - 4) / 4);
        Vector2[] points = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            points[i] = new Vector2(I16(r, countAt + 4 + (i * 4)), I16(r, countAt + 6 + (i * 4)));
        }

        return points;
    }

    /// <summary>EMR_POLYPOLYGON and EMR_POLYPOLYLINE (and their 16-bit forms): Bounds, NumberOfPolygons, Count, the counts, the points.</summary>
    private static List<Vector2[]> PolyPoints(ReadOnlySpan<byte> r, bool wide)
    {
        int polys = I32(r, 24), total = I32(r, 28), size = wide ? 8 : 4;
        int countsAt = 32;
        polys = (int)Math.Clamp(polys, 0, (r.Length - countsAt) / 4);
        int pointsAt = countsAt + (polys * 4);
        total = (int)Math.Clamp(total, 0, (r.Length - pointsAt) / size);
        List<Vector2[]> result = [];
        int at = 0;
        for (int i = 0; i < polys; i++)
        {
            int count = (int)Math.Min(U32(r, countsAt + (i * 4)), (uint)(total - at));
            Vector2[] points = new Vector2[count];
            for (int j = 0; j < count; j++, at++)
            {
                int p = pointsAt + (at * size);
                points[j] = wide ? new Vector2(I32(r, p), I32(r, p + 4)) : new Vector2(I16(r, p), I16(r, p + 2));
            }

            result.Add(points);
        }

        return result;
    }

    /// <summary>A LOGPEN's style, width and color.</summary>
    private static GdiPen Pen(uint style, int width, uint color, bool geometric, float[]? dashes) => new()
    {
        Style = (int)(style & 0xF),
        Cap = (int)((style >> 8) & 0xF),
        Join = (int)((style >> 12) & 0xF),
        Geometric = geometric,
        Width = Math.Abs(width),
        Color = GdiPen.ColorRef(color),
        Dashes = dashes,
    };

    /// <summary>EMR_EXTCREATEPEN: LOGPENEX after the bitmap offsets; a hatched or pattern pen brush draws in its color.</summary>
    private static GdiPen ExtPen(ReadOnlySpan<byte> r)
    {
        uint style = U32(r, 28);
        int width = I32(r, 32), entries = (int)Math.Min(U32(r, 48), (uint)Math.Max(0, (r.Length - 52) / 4));
        float[]? dashes = null;
        if ((style & 0xF) == GdiPen.UserStyle && entries > 0)
        {
            dashes = new float[entries];
            for (int i = 0; i < entries; i++)
            {
                dashes[i] = U32(r, 52 + (i * 4));
            }
        }

        bool geometric = (style & 0x000F0000) == 0x00010000;
        return Pen(style, width, U32(r, 40), geometric, dashes);
    }

    private static GdiBrush Brush(uint style, uint color, uint hatch) => new()
    {
        Style = style switch { 0 => GdiBrush.Solid, 2 => GdiBrush.Hatched, 1 => GdiBrush.Null, _ => GdiBrush.Solid },
        Color = GdiPen.ColorRef(color),
        Hatch = (int)hatch,
    };

    private static GdiPalette Palette(ReadOnlySpan<byte> logPalette)
    {
        int n = Math.Min(U16(logPalette, 2), (logPalette.Length - 4) / 4);
        Rgba8[] entries = new Rgba8[Math.Max(0, n)];
        for (int i = 0; i < entries.Length; i++)
        {
            int at = 4 + (i * 4);
            entries[i] = new Rgba8(logPalette[at], logPalette[at + 1], logPalette[at + 2], 255);
        }

        return new GdiPalette { Entries = entries };
    }

    /// <summary>A LOGFONTW: five 32-bit fields, eight bytes, then a 32-character UTF-16 face name.</summary>
    private static GdiFont FontW(ReadOnlySpan<byte> f)
    {
        string face = f.Length >= 92 ? Encoding.Unicode.GetString(f.Slice(28, 64)) : "";
        int nul = face.IndexOf('\0');
        return new GdiFont
        {
            Height = I32(f, 0),
            Width = I32(f, 4),
            Escapement = I32(f, 8),
            Orientation = I32(f, 12),
            Weight = I32(f, 16),
            Italic = f[20] != 0,
            Underline = f[21] != 0,
            StrikeOut = f[22] != 0,
            CharSet = f[23],
            Face = nul >= 0 ? face[..nul] : face,
        };
    }

    // ---- WMF ----

    private void PlayWmf(int function, ReadOnlySpan<byte> p, long offset)
    {
        switch (function)
        {
            case -1: // the META_HEADER
            case 0x0000: // META_EOF
                break;
            case 0x0201:
                _dc.BkColor = Color(U32(p, 0));
                break;
            case 0x0102:
                _dc.BkMode = I16(p, (0) * 2);
                break;
            case 0x0103:
                SetMapMode(I16(p, (0) * 2));
                break;
            case 0x0104:
                _dc.Rop2 = I16(p, (0) * 2);
                break;
            case 0x0106:
                _dc.PolyFillMode = I16(p, (0) * 2);
                break;
            case 0x0107:
                _dc.StretchMode = I16(p, (0) * 2);
                break;
            case 0x0108:
                _dc.TextCharExtra = I16(p, (0) * 2);
                break;
            case 0x0209:
                _dc.TextColor = Color(U32(p, 0));
                break;
            case 0x012E:
                _dc.TextAlign = U16(p, 0);
                break;
            case 0x020B:
                SetWindowOrg(new Vector2(I16(p, (1) * 2), I16(p, (0) * 2)));
                break;
            case 0x020C:
                SetWindowExt(new Vector2(I16(p, (1) * 2), I16(p, (0) * 2)));
                break;
            case 0x020D:
                SetViewportOrg(new Vector2(I16(p, (1) * 2), I16(p, (0) * 2)));
                break;
            case 0x020E:
                SetViewportExt(new Vector2(I16(p, (1) * 2), I16(p, (0) * 2)));
                break;
            case 0x020F:
                SetWindowOrg(_dc.WindowOrg + new Vector2(I16(p, (1) * 2), I16(p, (0) * 2)));
                break;
            case 0x0211:
                SetViewportOrg(_dc.ViewportOrg + new Vector2(I16(p, (1) * 2), I16(p, (0) * 2)));
                break;
            case 0x0410:
                ScaleWindowExt(I16(p, (3) * 2), I16(p, (2) * 2), I16(p, (1) * 2), I16(p, (0) * 2));
                break;
            case 0x0412:
                ScaleViewportExt(I16(p, (3) * 2), I16(p, (2) * 2), I16(p, (1) * 2), I16(p, (0) * 2));
                break;
            case 0x0213:
                LineTo(new Vector2(I16(p, (1) * 2), I16(p, (0) * 2)));
                break;
            case 0x0214:
                MoveTo(new Vector2(I16(p, (1) * 2), I16(p, (0) * 2)));
                break;
            case 0x0415:
                ExcludeClipRect(I16(p, (3) * 2), I16(p, (2) * 2), I16(p, (1) * 2), I16(p, (0) * 2));
                break;
            case 0x0416:
                IntersectClipRect(I16(p, (3) * 2), I16(p, (2) * 2), I16(p, (1) * 2), I16(p, (0) * 2));
                break;
            case 0x0220:
                OffsetClip(new Vector2(I16(p, (1) * 2), I16(p, (0) * 2)));
                break;
            case 0x0817:
            case 0x081A:
            case 0x0830:
                Arc(function == 0x0817 ? 0 : function == 0x0830 ? 1 : 2, I16(p, (7) * 2), I16(p, (6) * 2), I16(p, (5) * 2), I16(p, (4) * 2), new Vector2(I16(p, (3) * 2), I16(p, (2) * 2)), new Vector2(I16(p, (1) * 2), I16(p, (0) * 2)));
                break;
            case 0x0418:
                Ellipse(I16(p, (3) * 2), I16(p, (2) * 2), I16(p, (1) * 2), I16(p, (0) * 2));
                break;
            case 0x041B:
                Rectangle(I16(p, (3) * 2), I16(p, (2) * 2), I16(p, (1) * 2), I16(p, (0) * 2));
                break;
            case 0x061C:
                RoundRect(I16(p, (5) * 2), I16(p, (4) * 2), I16(p, (3) * 2), I16(p, (2) * 2), I16(p, (1) * 2), I16(p, (0) * 2));
                break;
            case 0x041F:
                SetPixel(new Vector2(I16(p, (3) * 2), I16(p, (2) * 2)), Color(U32(p, 0)));
                break;
            case 0x0324:
            case 0x0325:
                {
                    Vector2[] points = WmfPoints(p, 2, I16(p, (0) * 2));
                    if (function == 0x0324)
                    {
                        Polygon(points);
                    }
                    else
                    {
                        Polyline(points);
                    }

                    break;
                }

            case 0x0538: // META_POLYPOLYGON
                {
                    int polys = Math.Clamp((int)I16(p, (0) * 2), 0, (p.Length - 2) / 2);
                    List<Vector2[]> list = [];
                    int at = 2 + (polys * 2);
                    for (int i = 0; i < polys; i++)
                    {
                        int n = Math.Max(0, (int)I16(p, (1 + i) * 2));
                        Vector2[] points = WmfPoints(p, at, n);
                        list.Add(points);
                        at += n * 4;
                    }

                    PolyPolygon(list);
                    break;
                }

            case 0x001E:
                SaveDC();
                break;
            case 0x0127:
                RestoreDC(I16(p, (0) * 2));
                break;
            case 0x02FA: // META_CREATEPENINDIRECT: LOGPEN16
                StoreWmf(Pen(U16(p, 0), I16(p, (1) * 2), U32(p, 6), geometric: false, null));
                break;
            case 0x02FC: // META_CREATEBRUSHINDIRECT: LOGBRUSH16
                StoreWmf(Brush(U16(p, 0), U32(p, 2), U16(p, 6)));
                break;
            case 0x02FB: // META_CREATEFONTINDIRECT: LOGFONT16
                StoreWmf(Font16(p));
                break;
            case 0x00F7: // META_CREATEPALETTE
                StoreWmf(Palette(p));
                break;
            case 0x06FF: // META_CREATEREGION
                StoreWmf(new GdiRegion { Shape = WmfRegion(p) });
                break;
            case 0x0142: // META_DIBCREATEPATTERNBRUSH
                StoreWmf(WmfDibBrush(p));
                break;
            case 0x01F9: // META_CREATEPATTERNBRUSH (a device-dependent bitmap)
                StoreWmf(new GdiBrush { Style = GdiBrush.Solid, Color = new Rgba8(128, 128, 128, 255) });
                WarnOnce("Metafile.SkippedRecord", "A device-dependent pattern brush is drawn as gray.", offset, "skip:ddb-brush");
                break;
            case 0x012D:
            case 0x0234: // META_SELECTPALETTE
                Select(U16(p, 0));
                break;
            case 0x01F0:
                Delete(U16(p, 0));
                break;
            case 0x012C: // META_SELECTCLIPREGION
                if (Object<GdiRegion>(U16(p, 0)) is GdiRegion region)
                {
                    SelectClipRegion(region.Shape);
                }
                else
                {
                    CombineClip(null, 5);
                }

                break;
            case 0x0228: // META_FILLREGION: region, brush
                FillRegion(Object<GdiRegion>(U16(p, 0))?.Shape.Transform(Full), Object<GdiBrush>(U16(p, 2)));
                break;
            case 0x0429: // META_FRAMEREGION: region, brush, height, width
                FrameRegion(Object<GdiRegion>(U16(p, 0))?.Shape.Transform(Full), Object<GdiBrush>(U16(p, 2)), new Vector2(I16(p, (3) * 2), I16(p, (2) * 2)));
                break;
            case 0x012A:
                InvertRegion(Object<GdiRegion>(U16(p, 0))?.Shape.Transform(Full));
                break;
            case 0x012B:
                PaintRegion(Object<GdiRegion>(U16(p, 0))?.Shape.Transform(Full));
                break;
            case 0x0035: // META_REALIZEPALETTE
            case 0x0037: // META_SETPALENTRIES
            case 0x0436: // META_ANIMATEPALETTE
            case 0x0139: // META_RESIZEPALETTE
            case 0x0231: // META_SETMAPPERFLAGS
            case 0x0105: // META_SETRELABS
            case 0x020A: // META_SETTEXTJUSTIFICATION
            case 0x0149: // META_SETLAYOUT
                break;
            case 0x061D: // META_PATBLT
            case 0x0922: // META_BITBLT
            case 0x0B23: // META_STRETCHBLT
            case 0x0940: // META_DIBBITBLT
            case 0x0B41: // META_DIBSTRETCHBLT
            case 0x0F43: // META_STRETCHDIB
            case 0x0D33: // META_SETDIBTODEV
                PlayWmfBitmap(function, p, offset);
                break;
            case 0x0521: // META_TEXTOUT
            case 0x0A32: // META_EXTTEXTOUT
                PlayWmfText(function, p, offset);
                break;
            case 0x0626: // META_ESCAPE
                break;
            case 0x0419: // META_FLOODFILL
            case 0x0548: // META_EXTFLOODFILL
                Skip("Flood fill", offset);
                break;
            default:
                Skip($"WMF function 0x{function:X4}", offset);
                break;
        }
    }

    private static Vector2[] WmfPoints(ReadOnlySpan<byte> p, int at, int count)
    {
        int n = Math.Clamp(count, 0, Math.Max(0, (p.Length - at) / 4));
        Vector2[] points = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            points[i] = new Vector2(I16(p, at + (i * 4)), I16(p, at + 2 + (i * 4)));
        }

        return points;
    }

    /// <summary>A LOGFONT16: five 16-bit fields, eight bytes, then an ANSI face name up to 32 bytes.</summary>
    private static GdiFont Font16(ReadOnlySpan<byte> f)
    {
        ReadOnlySpan<byte> name = f.Length > 18 ? f[18..Math.Min(f.Length, 50)] : [];
        int nul = name.IndexOf((byte)0);
        if (nul >= 0)
        {
            name = name[..nul];
        }

        return new GdiFont
        {
            Height = I16(f, 0),
            Width = I16(f, 2),
            Escapement = I16(f, 4),
            Orientation = I16(f, 6),
            Weight = I16(f, 8),
            Italic = f[10] != 0,
            Underline = f[11] != 0,
            StrikeOut = f[12] != 0,
            CharSet = f[13],
            Face = Encoding.Latin1.GetString(name),
        };
    }

    /// <summary>
    /// A META_CREATEREGION region: a header, then scans, each a count, top, bottom, left and right pairs and the count again.
    /// </summary>
    private static Path WmfRegion(ReadOnlySpan<byte> p)
    {
        PathBuilder builder = new();
        int scans = p.Length >= 12 ? U16(p, 10) : 0;
        int at = 22; // nextInChain, ObjectType, ObjectCount, RegionSize, ScanCount, MaxScan, then the bounding rectangle
        for (int s = 0; s < scans && at + 6 <= p.Length; s++)
        {
            int count = U16(p, at);
            int top = I16(p, at + 2), bottom = I16(p, at + 4);
            int pairs = count / 2;
            for (int i = 0; i < pairs && at + 10 + (i * 4) <= p.Length; i++)
            {
                int left = I16(p, at + 6 + (i * 4)), right = I16(p, at + 8 + (i * 4));
                if (right > left && bottom > top)
                {
                    builder.AddRect(new RectF(left, top, right - left, bottom - top));
                }
            }

            at += 8 + (count * 2);
        }

        return builder.ToPath(FillRule.NonZero);
    }
}
