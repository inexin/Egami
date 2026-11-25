using System.Numerics;
using System.Runtime.CompilerServices;

using Egami.Drawing;
using Egami.Metafiles.Writing;

namespace Egami.Metafiles;

/// <summary>
/// A canvas that writes an EMF file: every drawing call becomes EMF+ records and, beside them, GDI records that draw the same
/// thing, so anything drawn on a <see cref="Canvas"/> can be saved as vectors that Word, PowerPoint, Excel, LibreOffice, GDI and
/// GDI+ open. Paths, rectangles, round rectangles, ovals and points are filled with solid colors (with alpha in EMF+, opaque in
/// GDI) and stroked with every <see cref="Stroke"/> setting; transforms, save and restore, and rectangle and path clips carry over.
/// Linear and centered radial gradients, image and picture paints become EMF+ brushes (opaque linear gradients are GDI gradient
/// fills in the GDI part, the rest images there); a color matrix or blend paint over solid colors becomes its color; and a
/// layer with only an opacity is folded into its shapes when they do not overlap. Images are EMF+ images (PNG and JPEG files
/// kept as they are) and GDI AlphaBlend bitmaps; pictures are played inline. Glyph runs are written as outlines, or as text
/// with <see cref="EmfText.Glyphs"/>.
/// What this version does not write as vectors (sweep and two-point conical gradients, other blend modes, blurs, meshes and
/// other layers with opacity, blend modes, filters or masks) is drawn by Egami into a bitmap of that area at
/// <see cref="EmfWriteOptions.FallbackScale"/> and embedded, with one <c>Metafile.FallbackImage</c> warning per kind in
/// <see cref="Warnings"/>; a call that blends with what is beneath it is drawn over everything before it, and its bitmap
/// replaces those pixels.
/// <para>
/// The canvas's units are pixels of a reference device at <see cref="EmfWriteOptions.Dpi"/>. The header's frame covers the
/// canvas in 0.01 mm, inclusive of its last pixel row and column as GDI+ writes it, so GDI+ and Office show the picture at the
/// canvas's size. Disposing the canvas finishes the file and writes it to the stream, which is left open.
/// </para>
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Metafiles.cs" region="emf-canvas"/>
/// </example>
public sealed partial class EmfCanvas : Canvas
{
    /// <summary>GDI coordinates are integers: they are written in sixteenths, with a world transform that scales them back, as GDI+ does.</summary>
    private const float Fixed = 16;

    private const uint BlackPen = 0x80000007, WhiteBrush = 0x80000000, NullPen = 0x80000008, NullBrush = 0x80000005;

    private readonly Stream _stream;
    private readonly EmfWriter _w = new();
    private Func<uint, bool>? _selectedCheck;

    /// <summary>Nonzero paths already made into contours that do not overlap, while the path lives.</summary>
    private static readonly ConditionalWeakTable<Path, Path> Simplified = new();
    private readonly bool _plus, _gdi;
    private readonly List<EgamiDiagnostic> _warnings = [];
    private readonly HashSet<string> _warned = [];
    private readonly List<FileState> _saved = [];
    private readonly List<FileState> _gdiSaved = [];
    private FileState _file;
    private RectF _drawn = RectF.Empty;
    private uint _plusSaves;
    private int _quiet;

    /// <summary>Initializes a new instance of the <see cref="EmfCanvas"/> class that writes one EMF file to a stream when disposed.</summary>
    /// <param name="stream">Where the file goes, from its current position; left open.</param>
    /// <param name="width">The picture's width in device units, pixels at <see cref="EmfWriteOptions.Dpi"/>.</param>
    /// <param name="height">The picture's height.</param>
    /// <param name="options">How to write the file, or <see langword="null"/> for <see cref="EmfWriteOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive and finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Metafiles.cs" region="emf-canvas"/>
    /// </example>
    public EmfCanvas(Stream stream, float width, float height, EmfWriteOptions? options = null)
        : base(width, height)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _stream = stream;
        Options = options ?? EmfWriteOptions.Default;
        _plus = Options.Kind != MetafileKind.Emf;
        _gdi = Options.Kind != MetafileKind.EmfPlusOnly;
        _file = new FileState
        {
            PlusWorld = Matrix3x2.Identity,
            GdiWorld = Matrix3x2.Identity,
            Smooth = false,
            Pen = BlackPen,
            Brush = WhiteBrush,
            FillMode = 1,
            Miter = 10,
            TextColor = uint.MaxValue,
        };
        Prologue();
    }

    /// <summary>Gets the options the file is written with.</summary>
    public EmfWriteOptions Options { get; }

    /// <summary>
    /// Gets what the canvas had to draw as images instead of vectors so far, one <c>Metafile.FallbackImage</c> warning per kind,
    /// as <see cref="Metafile.Warnings"/> lists what a load skipped.
    /// </summary>
    public IReadOnlyList<EgamiDiagnostic> Warnings => _warnings;

    /// <summary>What the file's drawing state is at this point of the records, so state records are written only when it changes.</summary>
    private struct FileState
    {
        public Matrix3x2 PlusWorld;
        public Matrix3x2 GdiWorld;
        public bool Smooth;
        public uint Pen;
        public uint Brush;
        public int FillMode;
        public float Miter;
        public uint PlusSave;
        public int Interpolation;
        public int TextHint;
        public uint Font;
        public uint TextColor;
        public int TextAlign;
    }

    /// <inheritdoc/>
    protected override void OnSave()
    {
        if (Recording())
        {
            _recorder!.Save();
            _recordDepth++;
        }
        else if (_quiet == 0)
        {
            WriteSave();
        }
    }

    /// <inheritdoc/>
    protected override void OnRestore()
    {
        if (_recorder is not null)
        {
            _recorder.Restore();
            if (--_recordDepth == 0)
            {
                FinishRecording();
            }
        }
        else if (_quiet == 0)
        {
            WriteRestore();
        }
    }

    /// <inheritdoc/>
    protected override void OnClipRect(RectF rect, ClipOp op, bool antialias)
    {
        if (Recording())
        {
            _recorder!.ClipRect(rect, op, antialias);
        }
        else if (_quiet == 0)
        {
            WriteClip(Path.Rect(rect), rect, op, antialias);
        }
    }

    /// <inheritdoc/>
    protected override void OnClipRoundRect(RoundRect roundRect, ClipOp op, bool antialias)
    {
        if (Recording())
        {
            _recorder!.ClipRoundRect(roundRect, op, antialias);
        }
        else if (_quiet == 0)
        {
            WriteClip(Path.RoundRect(roundRect), null, op, antialias);
        }
    }

    /// <inheritdoc/>
    protected override void OnClipPath(Path path, ClipOp op, bool antialias)
    {
        if (Recording())
        {
            _recorder!.ClipPath(path, op, antialias);
        }
        else if (_quiet == 0)
        {
            WriteClip(path, null, op, antialias);
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_plus)
        {
            _w.Plus(0x4002, 0, []); // EmfPlusEndOfFile
            _w.FlushPlus();
        }

        float dpi = Options.Dpi;
        int frameW = (int)MathF.Round(MathF.Max(Width - 1, 0) * 2540 / dpi), frameH = (int)MathF.Round(MathF.Max(Height - 1, 0) * 2540 / dpi);
        (int, int, int, int) bounds = _drawn.IsEmpty
            ? (0, 0, -1, -1)
            : ((int)MathF.Floor(_drawn.Left), (int)MathF.Floor(_drawn.Top), (int)MathF.Ceiling(_drawn.Right) - 1, (int)MathF.Ceiling(_drawn.Bottom) - 1);
        int devicePixels = Math.Max(1, (int)MathF.Round(dpi * 10)); // 254 mm, ten inches: whole numbers for any DPI with one decimal
        string? description = Options.Description is string name ? "Egami\0" + name + "\0\0" : null;
        _w.Finish(_stream, new EmfWriter.Header(bounds, (0, 0, frameW, frameH), (devicePixels, devicePixels), (254000, 254000), description), Options.Compress);
        base.Dispose(disposing);
    }

    /// <summary>The records every file starts with after its header: the EMF+ header and the state Egami's drawing model needs.</summary>
    private void Prologue()
    {
        if (_plus)
        {
            int dpi = Math.Max(1, (int)MathF.Round(Options.Dpi));
            _w.Plus(0x4001, (ushort)(_gdi ? 1 : 0), _w.Data().U32(Egami.GdiPlus.GdiPlusData.Version).U32(1).I32(dpi).I32(dpi).Span);
            _w.FlushPlus(); // the header's comment holds the header alone, right after EMR_HEADER

            // Egami puts integer coordinates on pixel edges: GDI+'s PixelOffsetModeHalf. Page units are device pixels.
            _w.Plus(0x4022, 4, []);
            _w.Plus(0x4030, 2, _w.Data().F32(1).Span);
            SetSmooth(true);
        }

        if (_gdi)
        {
            _w.Gdi(18, _w.Data().U32(1).Span); // EMR_SETBKMODE TRANSPARENT: gaps in dashed lines stay empty
        }
    }

    // ---- State ----

    private void WriteSave()
    {
        _file.PlusSave = ++_plusSaves;
        _saved.Add(_file);
        _history.Add((c, _) => c.Save());
        if (_plus)
        {
            _w.Plus(0x4025, 0, _w.Data().U32(_file.PlusSave).Span);
        }

        if (_gdi)
        {
            _w.Gdi(33, []); // EMR_SAVEDC
        }
    }

    private void WriteRestore()
    {
        if (_saved.Count == 0)
        {
            return;
        }

        FileState saved = _saved[^1];
        _saved.RemoveAt(_saved.Count - 1);
        _history.Add((c, _) => c.Restore());
        if (_plus)
        {
            _w.Plus(0x4026, 0, _w.Data().U32(saved.PlusSave).Span);
        }

        if (_gdi)
        {
            _w.Gdi(34, _w.Data().I32(-1).Span); // EMR_RESTOREDC
        }

        _file = saved;
    }

    private void SetPlusWorld(Matrix3x2 m)
    {
        if (_file.PlusWorld != m)
        {
            _w.Plus(0x402A, 0, _w.Data().Matrix(m).Span);
            _file.PlusWorld = m;
        }
    }

    /// <summary>Sets GDI's world transform for coordinates in sixteenths of a local unit.</summary>
    private void SetGdiWorld(Matrix3x2 m)
    {
        Matrix3x2 world = Matrix3x2.CreateScale(1 / Fixed) * m;
        if (_file.GdiWorld != world)
        {
            _w.Gdi(35, _w.Data().Matrix(world).Span); // EMR_SETWORLDTRANSFORM
            _file.GdiWorld = world;
        }
    }

    /// <summary>EmfPlusSetInterpolationMode, which samples images and texture brushes.</summary>
    private void SetInterpolation(int mode)
    {
        if (_file.Interpolation != mode && mode != 0)
        {
            _w.Plus(0x4021, (ushort)mode, []);
            _file.Interpolation = mode;
        }
    }

    /// <summary>EMR_SAVEDC alone, around GDI records that clip; <see cref="GdiRestore"/> brings back the GDI part of the state.</summary>
    private void GdiSave()
    {
        _gdiSaved.Add(_file);
        _w.Gdi(33, []);
    }

    private void GdiRestore()
    {
        FileState s = _gdiSaved[^1];
        _gdiSaved.RemoveAt(_gdiSaved.Count - 1);
        _w.Gdi(34, _w.Data().I32(-1).Span); // EMR_RESTOREDC
        (_file.GdiWorld, _file.Pen, _file.Brush, _file.FillMode, _file.Miter) = (s.GdiWorld, s.Pen, s.Brush, s.FillMode, s.Miter);
        (_file.Font, _file.TextColor, _file.TextAlign) = (s.Font, s.TextColor, s.TextAlign);
    }

    /// <summary>SetAntiAliasMode: SmoothingModeAntiAlias (4) with the antialias bit, or SmoothingModeNone (3).</summary>
    private void SetSmooth(bool smooth)
    {
        if (_file.Smooth != smooth)
        {
            _w.Plus(0x401E, (ushort)(smooth ? (4 << 1) | 1 : 3 << 1), []);
            _file.Smooth = smooth;
        }
    }

    private void SetFillMode(FillRule rule)
    {
        int mode = rule == FillRule.NonZero ? 2 : 1; // WINDING, ALTERNATE
        if (_file.FillMode != mode)
        {
            _w.Gdi(19, _w.Data().I32(mode).Span); // EMR_SETPOLYFILLMODE
            _file.FillMode = mode;
        }
    }

    private void SelectPen(uint pen)
    {
        if (_file.Pen != pen)
        {
            _w.Gdi(37, _w.Data().U32(pen).Span); // EMR_SELECTOBJECT
            _file.Pen = pen;
        }
    }

    private void SelectBrush(uint brush)
    {
        if (_file.Brush != brush)
        {
            _w.Gdi(37, _w.Data().U32(brush).Span);
            _file.Brush = brush;
        }
    }

    /// <summary>Gets <see cref="Selected"/> as a delegate, made once.</summary>
    private Func<uint, bool> SelectedCheck => _selectedCheck ??= Selected;

    /// <summary>Whether a GDI handle is selected now or in a saved state, which RestoreDC would select again.</summary>
    private bool Selected(uint handle)
    {
        if (_file.Pen == handle || _file.Brush == handle || _file.Font == handle)
        {
            return true;
        }

        foreach (FileState s in _saved.Concat(_gdiSaved))
        {
            if (s.Pen == handle || s.Brush == handle || s.Font == handle)
            {
                return true;
            }
        }

        return false;
    }

    // ---- Clips ----

    private void WriteClip(Path path, RectF? rect, ClipOp op, bool antialias)
    {
        Remember(c => c.ClipPath(path, op, antialias));
        Matrix3x2 m = Matrix;
        if (op == ClipOp.Difference && path.IsEmpty)
        {
            return;
        }

        if (!Invertible(m))
        {
            // A degenerate transform maps the clip to nothing: an intersection clips everything away.
            if (op == ClipOp.Intersect)
            {
                WriteClip(Path.Empty, new RectF(0, 0, 0, 0), op, antialias);
            }

            return;
        }

        if (path.IsEmpty)
        {
            rect = new RectF(0, 0, 0, 0);
        }

        if (_plus)
        {
            SetPlusWorld(m);
            SetSmooth(antialias);
            int mode = op == ClipOp.Intersect ? 1 : 4; // CombineModeIntersect, CombineModeExclude
            if (rect is RectF r)
            {
                _w.Plus(0x4032, (ushort)(mode << 8), _w.Data().F32(r.X).F32(r.Y).F32(r.Width).F32(r.Height).Span);
            }
            else
            {
                int id = _w.PlusObject(3, Egami.GdiPlus.GdiPlusData.WritePath(EvenOdd(path)));
                _w.Plus(0x4033, (ushort)((mode << 8) | id), []);
            }
        }

        if (_gdi)
        {
            int mode = op == ClipOp.Intersect ? 1 : 4; // RGN_AND, RGN_DIFF
            if (rect is RectF r && m.M12 == 0 && m.M21 == 0)
            {
                // An axis-aligned rectangle: a device region, which GDI rounds to whole pixels anyway.
                RectF d = MapRect(r, m);
                int left = (int)MathF.Round(d.Left), top = (int)MathF.Round(d.Top), right = (int)MathF.Round(d.Right), bottom = (int)MathF.Round(d.Bottom);
                RecordBuffer b = _w.Data();
                bool empty = right <= left || bottom <= top;
                b.U32(empty ? 32u : 48u).U32((uint)mode);
                b.U32(32).U32(1).U32(empty ? 0u : 1u).U32(empty ? 0u : 16u); // RGNDATAHEADER: size, RDH_RECTANGLES, count, rects' size
                b.I32(empty ? 0 : left).I32(empty ? 0 : top).I32(empty ? 0 : right).I32(empty ? 0 : bottom);
                if (!empty)
                {
                    b.I32(left).I32(top).I32(right).I32(bottom);
                }

                _w.Gdi(75, b.Span); // EMR_EXTSELECTCLIPRGN
            }
            else if (WriteGdiPath(path.IsEmpty ? Path.Rect(new RectF(0, 0, 0, 0)) : path, m))
            {
                SetFillMode(path.FillRule);
                _w.Gdi(67, _w.Data().I32(mode).Span); // EMR_SELECTCLIPPATH
            }
        }
    }

    // ---- Helpers ----

    /// <summary>
    /// The path to give EMF+: a nonzero path that may overlap itself becomes contours that do not, which both fill rules fill
    /// alike. GDI+ reads a winding flag on EMF+ paths (0x2000, which <see cref="GdiPlus.GdiPlusData.WritePath"/> writes), but other
    /// readers, LibreOffice among them, fill every EMF+ path even-odd. A path drawn again, such as a symbol at many places, is
    /// simplified once.
    /// </summary>
    private static Path EvenOdd(Path path)
    {
        if (path.FillRule == FillRule.EvenOdd || path.IsConvex)
        {
            return path;
        }

        return Simplified.GetValue(path, static p =>
        {
            try
            {
                return p.Simplify();
            }
            catch (ArgumentException)
            {
                return p;
            }
        });
    }

    private static bool Invertible(Matrix3x2 m)
    {
        float det = m.GetDeterminant();
        return det != 0 && float.IsFinite(det) && float.IsFinite(m.M31) && float.IsFinite(m.M32);
    }

    private static RectF MapRect(RectF rect, Matrix3x2 m)
        => RectF.FromPoints(
        [
            Vector2.Transform(new Vector2(rect.Left, rect.Top), m),
            Vector2.Transform(new Vector2(rect.Right, rect.Top), m),
            Vector2.Transform(new Vector2(rect.Right, rect.Bottom), m),
            Vector2.Transform(new Vector2(rect.Left, rect.Bottom), m),
        ]);

    /// <summary>Adds a local rectangle, grown by a device outset, to the drawn bounds, within the clip.</summary>
    private void Touch(RectF local, float outset)
    {
        RectF device = MapRect(local, Matrix).Inflate(outset, outset).Intersect(DeviceClipBounds).Intersect(new RectF(0, 0, Width, Height));
        if (device.IsEmpty)
        {
            return;
        }

        _drawn = _drawn.IsEmpty ? device : RectF.FromEdges(
            MathF.Min(_drawn.Left, device.Left),
            MathF.Min(_drawn.Top, device.Top),
            MathF.Max(_drawn.Right, device.Right),
            MathF.Max(_drawn.Bottom, device.Bottom));
    }

    private void Warn(string what)
    {
        if (_warned.Add(what))
        {
            _warnings.Add(new EgamiDiagnostic(DiagnosticLevel.Warning, "Metafile", "Metafile.FallbackImage", $"EMF has no form for {what} in this version; it was drawn by Egami as an image.", null, null));
        }
    }
}
