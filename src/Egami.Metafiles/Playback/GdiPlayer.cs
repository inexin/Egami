using System.Numerics;

using Egami.Drawing;
using Egami.Metafiles.Reading;
using Egami.Pixels;

namespace Egami.Metafiles.Playback;

/// <summary>
/// Plays WMF and EMF records the way GDI does ([MS-WMF], [MS-EMF]): a device context with map modes, window and viewport, the
/// EMF world transform, SaveDC and RestoreDC, an object table, pens, brushes and fonts, and the clip region. Every record turns
/// into canvas calls in the canvas's device space (the canvas matrix is set to identity while playing), so GDI's integer device
/// pixels can be followed when drawing aliased and stay exact when antialiased. A record that cannot be read or drawn is skipped
/// with one warning per kind; nothing throws on content.
/// </summary>
internal sealed partial class GdiPlayer
{
    private readonly Metafile _metafile;
    private readonly int _depth;
    private readonly MetafileContent _content;
    private readonly byte[] _data;
    private readonly Canvas _canvas;
    private readonly MetafileRenderOptions _options;
    private readonly MetafileReport _report;
    private readonly bool _wmf;
    private readonly bool _aliased;

    /// <summary>Device units to the canvas's device space: reference pixels (EMF) or 1/96 inch (WMF) to the frame's points, then the canvas matrix.</summary>
    private Matrix3x2 _device;

    /// <summary>Gets a value indicating whether <see cref="_device"/> only scales and moves, so aliased drawing can round as GDI does.</summary>
    private bool _deviceAxisAligned;

    /// <summary>The scale and offset of <see cref="_device"/>, output pixels per reference device pixel.</summary>
    private Vector2 _deviceScale, _deviceOffset;

    /// <summary>Reference device pixels per millimetre, for the fixed map modes.</summary>
    private readonly Vector2 _pixelsPerMm;

    private readonly List<DcState> _saved = [];
    private readonly HashSet<string> _warned = [];
    private GdiObject?[] _objects;
    private DcState _dc = new();
    private Matrix3x2? _full;
    private Matrix3x2? _pageWorld;
    private int _clipToken;

    internal GdiPlayer(Metafile metafile, Canvas canvas, MetafileRenderOptions options, MetafileReport report, int depth = 0)
    {
        _metafile = metafile;
        _depth = depth;
        _content = metafile.Content;
        _data = _content.Data;
        _canvas = canvas;
        _options = options;
        _report = report;
        _wmf = _content.Kind == MetafileKind.Wmf;
        _aliased = !options.Antialias;
        _objects = new GdiObject?[Math.Clamp(_content.ObjectCount, 1, 65535)];
        Matrix3x2 canvasMatrix = canvas.Matrix;
        if (_wmf)
        {
            // The device is the canvas's own pixels when it only scales, as PlayMetaFile plays into a DC with the picture's
            // box as the window and the output rectangle as the viewport; otherwise units of 1/96 inch.
            RectF frame = metafile.Frame;
            Vector2 units = new(frame.Width / 0.75f, frame.Height / 0.75f);
            Vector2 extent;
            if (canvasMatrix.M12 == 0 && canvasMatrix.M21 == 0 && canvasMatrix.M11 > 0 && canvasMatrix.M22 > 0)
            {
                Vector2 scale = new(canvasMatrix.M11, canvasMatrix.M22);
                _pixelsPerMm = scale * (72 / 25.4f);
                _device = Matrix3x2.CreateTranslation(Vector2.Transform(new Vector2(frame.X, frame.Y), canvasMatrix));
                extent = new Vector2(frame.Width, frame.Height) * scale;
            }
            else
            {
                _pixelsPerMm = new Vector2(96 / 25.4f);
                _device = Matrix3x2.CreateScale(0.75f) * Matrix3x2.CreateTranslation(frame.X, frame.Y) * canvasMatrix;
                extent = units;
            }

            if (_content.PlaceableBox is (short l, short t, short r, short b))
            {
                _dc.WindowOrg = new Vector2(l, t);
                _dc.WindowExt = new Vector2(r - l, b - t);
            }
            else
            {
                _dc.WindowExt = units;
            }

            _dc.MapMode = 8;
            _dc.ViewportExt = extent;
        }
        else
        {
            (int pw, int ph) = _content.DevicePixels;
            (int mw, int mh) = _content.DeviceMillimeters;
            _pixelsPerMm = pw > 0 && ph > 0 && mw > 0 && mh > 0 ? new Vector2((float)pw / mw, (float)ph / mh) : new Vector2(96 / 25.4f);
            Vector2 pointsPerPixel = new Vector2(72 / 25.4f) / _pixelsPerMm;
            _device = Matrix3x2.CreateScale(pointsPerPixel) * canvasMatrix;
        }

        _deviceAxisAligned = _device.M12 == 0 && _device.M21 == 0;
        _deviceScale = new Vector2(_device.M11, _device.M22);
        _deviceOffset = new Vector2(_device.M31, _device.M32);
    }

    /// <summary>Plays the WMF or EMF records of the metafile.</summary>
    public static void Play(Metafile metafile, Canvas canvas, MetafileRenderOptions options, MetafileReport report, int depth = 0)
    {
        GdiPlayer player = new(metafile, canvas, options, report, depth);
        int start = player.Begin();
        try
        {
            foreach (MetaRecord record in player._content.Records)
            {
                player.PlayRecord(record);
            }

            player.FlushPending();
            player.FlushStipple();
        }
        finally
        {
            canvas.RestoreToCount(start);
        }
    }

    /// <summary>Gets reference device units to the canvas's device space, which the EMF+ player shares.</summary>
    internal Matrix3x2 Device => _device;

    /// <summary>
    /// Puts a transform in device units before the device mapping: the EMF+ player fits GDI+'s inclusive frame this way, so the
    /// GDI records of GetDC sections land where GDI+ puts them.
    /// </summary>
    internal void Refit(Matrix3x2 fit)
    {
        _device = fit * _device;
        _deviceAxisAligned = _device.M12 == 0 && _device.M21 == 0;
        _deviceScale = new Vector2(_device.M11, _device.M22);
        _deviceOffset = new Vector2(_device.M31, _device.M32);
        Changed();
    }

    /// <summary>Gets the reference device's pixels per millimetre.</summary>
    internal Vector2 PixelsPerMm => _pixelsPerMm;

    /// <summary>Starts playing: the canvas matrix becomes identity (device space) and the clip has its save. Returns the count to restore to.</summary>
    internal int Begin()
    {
        int start = _canvas.Save();
        _canvas.SetMatrix(Matrix3x2.Identity);
        _clipToken = _canvas.Save();
        return start;
    }

    /// <summary>Plays one record (the EMF+ player calls this for the GDI records of a GetDC section).</summary>
    internal void PlayRecord(MetaRecord record)
    {
        ReadOnlySpan<byte> r = _data.AsSpan(record.Offset, record.Size);
        try
        {
            // A blit that may start a transparent-bitmap idiom waits only for the next blit; a stipple idiom also lets object
            // records through.
            if (_pending is not null && record.Type is not (76 or 77 or 0x0940 or 0x0B41 or 0x0F43))
            {
                FlushPending();
            }

            if (_stipple is not null && record.Type is not (0x061D or 0x0940 or 0x0B41 or 0x012D or 0x0142 or 0x01F0 or 0x02FC or 0x02FA or 0x01F9
                or 37 or 39 or 40 or 93 or 94 or 76 or 77))
            {
                FlushStipple(record.Offset);
            }

            if (_wmf)
            {
                PlayWmf(record.Type, r[6..], record.Offset);
            }
            else
            {
                PlayEmf(record.Type, r, record.Offset);
            }
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or InvalidOperationException or OverflowException or ArithmeticException)
        {
            // A record whose fields do not fit its size, or that describes geometry the canvas refuses, is skipped alone.
            WarnOnce("Metafile.BadRecord", $"A record (type {record.Type}) could not be read or drawn and was skipped: {e.Message}", record.Offset);
        }
    }

    /// <summary>Warns once per id and detail, so a file with thousands of one record does not repeat itself.</summary>
    private void WarnOnce(string id, string message, long? offset = null, string? key = null)
    {
        if (_warned.Add(key ?? id))
        {
            _report.Warn(id, message, offset);
        }
    }

    private void Skip(string what, long offset) =>
        WarnOnce("Metafile.SkippedRecord", $"{what} records are not drawn by Egami.Metafiles; they were skipped.", offset, "skip:" + what);

    // ---- Coordinates ----

    /// <summary>Gets logical units to the canvas's device space: world transform, then window to viewport, then device to canvas.</summary>
    private Matrix3x2 Full => _full ??= _dc.World * Page() * _device;

    /// <summary>Clears the cached transform after a mapping or world transform change.</summary>
    private void Changed()
    {
        _full = null;
        _pageWorld = null;
    }

    /// <summary>The page transform: logical (page) units to device units by the map mode, window and viewport.</summary>
    private Matrix3x2 Page()
    {
        Vector2 scale;
        switch (_dc.MapMode)
        {
            case 2: // MM_LOMETRIC, 0.1 mm, y up
                scale = _pixelsPerMm * new Vector2(0.1f, -0.1f);
                break;
            case 3: // MM_HIMETRIC, 0.01 mm
                scale = _pixelsPerMm * new Vector2(0.01f, -0.01f);
                break;
            case 4: // MM_LOENGLISH, 0.01 inch
                scale = _pixelsPerMm * new Vector2(0.254f, -0.254f);
                break;
            case 5: // MM_HIENGLISH, 0.001 inch
                scale = _pixelsPerMm * new Vector2(0.0254f, -0.0254f);
                break;
            case 6: // MM_TWIPS, 1/1440 inch
                scale = _pixelsPerMm * new Vector2(25.4f / 1440, -25.4f / 1440);
                break;
            case 7: // MM_ISOTROPIC: the smaller scale for both axes, each keeping its sign
            case 8: // MM_ANISOTROPIC
                Vector2 w = _dc.WindowExt, v = _dc.ViewportExt;
                scale = new Vector2(w.X == 0 ? 1 : v.X / w.X, w.Y == 0 ? 1 : v.Y / w.Y);
                if (_dc.MapMode == 7)
                {
                    float s = MathF.Min(MathF.Abs(scale.X), MathF.Abs(scale.Y));
                    scale = new Vector2(MathF.CopySign(s, scale.X), MathF.CopySign(s, scale.Y));
                }

                break;
            default: // MM_TEXT
                scale = Vector2.One;
                break;
        }

        return Matrix3x2.CreateTranslation(-_dc.WindowOrg) * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(_dc.ViewportOrg);
    }

    /// <summary>
    /// A logical point in the canvas's device space. Aliased, it is rounded twice as GDI rounds it: to whole reference device
    /// pixels (the coordinates the records were recorded in), then to whole output pixels after the playback scale.
    /// </summary>
    private Vector2 Dev(Vector2 logical)
    {
        if (!_aliased || !_deviceAxisAligned)
        {
            return Vector2.Transform(logical, Full);
        }

        Vector2 reference = Snap(Vector2.Transform(logical, PageWorld));
        return Snap((reference * _deviceScale) + _deviceOffset);
    }

    /// <summary>Gets logical units to reference device units: the world transform, then window to viewport.</summary>
    private Matrix3x2 PageWorld => _pageWorld ??= _dc.World * Page();

    /// <summary>Rounds to whole pixels as GDI does, halves down (checked against PlayEnhMetaFile at several scales).</summary>
    private static Vector2 Snap(Vector2 p) => new(MathF.Ceiling(p.X - 0.5f), MathF.Ceiling(p.Y - 0.5f));

    /// <summary>The offset to the pixel centers GDI's aliased lines and fills run through.</summary>
    private Vector2 Center => _aliased ? new Vector2(0.5f) : Vector2.Zero;

    /// <summary>The device size of one logical unit, for pen widths.</summary>
    private float Scale
    {
        get
        {
            Matrix3x2 m = Full;
            return MathF.Sqrt(MathF.Abs((m.M11 * m.M22) - (m.M12 * m.M21)));
        }
    }

    private void SetMapMode(int mode)
    {
        _dc.MapMode = mode is >= 1 and <= 8 ? mode : 1;
        if (_dc.MapMode is not (7 or 8))
        {
            _dc.WindowExt = Vector2.One;
            _dc.ViewportExt = Vector2.One;
        }

        Changed();
    }

    private void SetWindowExt(Vector2 extent)
    {
        if (_dc.MapMode is 7 or 8 && extent.X != 0 && extent.Y != 0)
        {
            _dc.WindowExt = extent;
            Changed();
        }
    }

    private void SetViewportExt(Vector2 extent)
    {
        if (_dc.MapMode is 7 or 8 && extent.X != 0 && extent.Y != 0)
        {
            _dc.ViewportExt = extent;
            Changed();
        }
    }

    private void SetWindowOrg(Vector2 origin)
    {
        _dc.WindowOrg = origin;
        Changed();
    }

    private void SetViewportOrg(Vector2 origin)
    {
        _dc.ViewportOrg = origin;
        Changed();
    }

    private void ScaleWindowExt(int xNum, int xDenom, int yNum, int yDenom)
    {
        if (xDenom != 0 && yDenom != 0)
        {
            SetWindowExt(new Vector2(_dc.WindowExt.X * xNum / xDenom, _dc.WindowExt.Y * yNum / yDenom));
        }
    }

    private void ScaleViewportExt(int xNum, int xDenom, int yNum, int yDenom)
    {
        if (xDenom != 0 && yDenom != 0)
        {
            SetViewportExt(new Vector2(_dc.ViewportExt.X * xNum / xDenom, _dc.ViewportExt.Y * yNum / yDenom));
        }
    }

    private void SetWorldTransform(Matrix3x2 m)
    {
        if (IsFinite(m))
        {
            _dc.World = m;
            Changed();
        }
    }

    /// <summary>ModifyWorldTransform: 1 identity, 2 left multiply (the new matrix applies first), 3 right multiply, 4 set.</summary>
    private void ModifyWorldTransform(Matrix3x2 m, int mode)
    {
        Matrix3x2 world = mode switch
        {
            1 => Matrix3x2.Identity,
            2 => m * _dc.World,
            3 => _dc.World * m,
            4 => m,
            _ => _dc.World,
        };
        SetWorldTransform(world);
    }

    private static bool IsFinite(Matrix3x2 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M31) && float.IsFinite(m.M32);

    // ---- State ----

    private void SaveDC()
    {
        if (_saved.Count < 1024)
        {
            _saved.Add(_dc.Clone());
        }
    }

    /// <summary>RestoreDC: a negative level counts back from the latest save, a positive one names a save by its number.</summary>
    private void RestoreDC(int level)
    {
        int index = level < 0 ? _saved.Count + level : level - 1;
        if (index < 0 || index >= _saved.Count)
        {
            return;
        }

        Path? clip = _dc.Clip;
        _dc = _saved[index];
        _saved.RemoveRange(index, _saved.Count - index);
        Changed();
        if (!ReferenceEquals(clip, _dc.Clip))
        {
            ApplyClip();
        }
    }

    // ---- Objects ----

    /// <summary>Puts an object at an index of the table (EMF), growing it within the 65535 the format allows.</summary>
    private void Store(long index, GdiObject obj)
    {
        if (index < 0 || index >= 65535)
        {
            return;
        }

        if (index >= _objects.Length)
        {
            Array.Resize(ref _objects, (int)Math.Min(65535, Math.Max(index + 1, _objects.Length * 2L)));
        }

        _objects[index] = obj;
    }

    /// <summary>Puts an object in the lowest free slot (WMF).</summary>
    private void StoreWmf(GdiObject obj)
    {
        int free = Array.IndexOf(_objects, null);
        if (free < 0)
        {
            free = _objects.Length;
        }

        Store(free, obj);
    }

    private void Select(uint index)
    {
        GdiObject? obj;
        if ((index & 0x80000000) != 0)
        {
            int stock = (int)(index & 0x7FFFFFFF);
            obj = stock switch
            {
                <= 5 => GdiBrush.Stock(stock),
                6 => GdiPen.White,
                7 => GdiPen.Black,
                8 => GdiPen.None,
                18 => GdiBrush.Stock(0),
                19 => GdiPen.Black,
                15 => null,
                _ => GdiFont.System,
            };
        }
        else
        {
            obj = index < _objects.Length ? _objects[index] : null;
        }

        switch (obj)
        {
            case GdiPen pen:
                _dc.Pen = pen;
                break;
            case GdiBrush brush:
                _dc.Brush = brush;
                break;
            case GdiFont font:
                _dc.Font = font;
                break;
            case GdiPalette palette:
                _dc.Palette = palette;
                break;
            case GdiRegion region:
                SelectClipRegion(region.Shape);
                break;
        }
    }

    private void Delete(uint index)
    {
        if (index < _objects.Length)
        {
            _objects[index] = null;
        }
    }

    /// <summary>A COLORREF in the current palette: palette indices (0x01 in the high byte) take that entry.</summary>
    private Rgba8 Color(uint value)
    {
        if ((value >> 24) == 1 && _dc.Palette is GdiPalette palette)
        {
            int i = (int)(value & 0xFFFF);
            return i < palette.Entries.Length ? palette.Entries[i] : new Rgba8(0, 0, 0, 255);
        }

        return GdiPen.ColorRef(value);
    }
}
