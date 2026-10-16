using System.Numerics;

using Egami.Drawing;
using Egami.Metafiles.Reading;
using Egami.Pixels;

using static Egami.Metafiles.Reading.MetafileReader;

namespace Egami.Metafiles.Playback;

/// <summary>
/// Plays EMF+ records the way GDI+ does ([MS-EMFPLUS]): the graphics state (world transform, page unit and scale, Save and
/// Restore, containers, clip with six combine modes, smoothing, pixel offset and compositing), the 64-entry object table, and
/// every drawing record, in the device space the GDI player sets up. In a dual file the GDI records between EMF+ records are
/// skipped except after EmfPlusGetDC, where GDI+ hands drawing to GDI until the next EMF+ record (design question 5).
/// </summary>
internal sealed partial class EmfPlusPlayer
{
    private readonly Metafile _metafile;
    private readonly MetafileContent _content;
    private readonly Canvas _canvas;
    private readonly MetafileRenderOptions _options;
    private readonly MetafileReport _report;
    private readonly GdiPlayer _gdi;
    private readonly PlusObject?[] _objects = new PlusObject?[64];
    private readonly Dictionary<int, State> _saved = [];
    private readonly HashSet<string> _warned = [];
    private readonly int _depth;
    private State _state = new();
    private int _clipToken;

    /// <summary>The playback state Save, Restore and containers keep.</summary>
    private sealed record State
    {
        public Matrix3x2 World { get; init; } = Matrix3x2.Identity;

        public int PageUnit { get; init; } = 2;

        public float PageScale { get; init; } = 1;

        /// <summary>Gets the clip in device space, or null for none.</summary>
        public Path? Clip { get; init; }

        public int Smoothing { get; init; }

        public int PixelOffset { get; init; }

        public int Compositing { get; init; }

        public int TextHint { get; init; }

        public int Interpolation { get; init; }

        /// <summary>Gets a container's transform from its source to its destination, applied before the world transform.</summary>
        public Matrix3x2 Container { get; init; } = Matrix3x2.Identity;
    }

    internal EmfPlusPlayer(Metafile metafile, Canvas canvas, MetafileRenderOptions options, MetafileReport report, int depth)
    {
        _metafile = metafile;
        _content = metafile.Content;
        _canvas = canvas;
        _options = options;
        _report = report;
        _depth = depth;
        _gdi = new GdiPlayer(metafile, canvas, options, report, depth);

        // GDI+ takes the frame as inclusive, one device pixel wider than GDI does, and fits that to the destination
        // (checked against DrawImage of a Metafile): the picture is scaled by width / (width + 1) about the frame's corner.
        Vector2 pixelsPerPoint = _gdi.PixelsPerMm * (25.4f / 72);
        RectF frame = metafile.Frame;
        Vector2 size = new Vector2(frame.Width, frame.Height) * pixelsPerPoint, corner = new Vector2(frame.X, frame.Y) * pixelsPerPoint;
        Vector2 fit = new(size.X > 0 ? size.X / (MathF.Round(size.X) + 1) : 1, size.Y > 0 ? size.Y / (MathF.Round(size.Y) + 1) : 1);
        _gdi.Refit(Matrix3x2.CreateScale(fit, corner));
    }

    /// <summary>Plays the EMF+ records, and the GDI records of GetDC sections.</summary>
    public static void Play(Metafile metafile, Canvas canvas, MetafileRenderOptions options, MetafileReport report, int depth = 0)
    {
        EmfPlusPlayer player = new(metafile, canvas, options, report, depth);
        int start = player._gdi.Begin();
        try
        {
            player._clipToken = canvas.Save();
            player.Run();
        }
        finally
        {
            canvas.RestoreToCount(start);
        }
    }

    private void Run()
    {
        List<MetaRecord> records = _content.Records;
        List<PlusRecord> plus = _content.PlusRecords;
        int next = 0;
        bool getDc = false;
        for (int i = 0; i < records.Count; i++)
        {
            bool carries = next < plus.Count && plus[next].EmfIndex == i;
            if (carries)
            {
                if (getDc)
                {
                    _gdi.FlushPending();
                    getDc = false;
                    ApplyClip();
                }

                while (next < plus.Count && plus[next].EmfIndex == i)
                {
                    PlusRecord record = plus[next++];
                    if (record.Type == 0x4004)
                    {
                        getDc = true;
                        continue;
                    }

                    Play(record);
                }
            }
            else if (getDc && records[i].Type is not (EmrHeader or EmrEof or EmrComment))
            {
                _gdi.PlayRecord(records[i]);
            }
        }

        _gdi.FlushPending();
    }

    private void Play(PlusRecord record)
    {
        try
        {
            PlayRecord(record.Type, record.Flags, record.Data.Span);
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or InvalidOperationException or OverflowException or ArithmeticException)
        {
            WarnOnce("Metafile.BadRecord", $"An EMF+ record (type 0x{record.Type:X4}) could not be read or drawn and was skipped: {e.Message}", "plus:" + record.Type);
        }
    }

    private void WarnOnce(string id, string message, string? key = null)
    {
        if (_warned.Add(key ?? id))
        {
            _report.Warn(id, message);
        }
    }

    private void Skip(string what) =>
        WarnOnce("Metafile.SkippedRecord", $"{what} records are not drawn by Egami.Metafiles; they were skipped.", "skip:" + what);

    // ---- Coordinates ----

    /// <summary>
    /// Device pixels per unit: Pixel, World and Display are device pixels; the physical units scale by the EMF+ header's logical
    /// DPI (96 for files GDI+ records against a screen), as GDI+ converts them (checked against GDI+'s playback).
    /// </summary>
    private float UnitScale(int unit)
    {
        float dpi = _content.PlusDpi.X > 0 ? _content.PlusDpi.X : _gdi.PixelsPerMm.X * 25.4f;
        return unit switch
        {
            3 => dpi / 72, // Point
            4 => dpi, // Inch
            5 => dpi / 300, // Document
            6 => dpi / 25.4f, // Millimeter
            _ => 1,
        };
    }

    /// <summary>World units to the canvas's device space: container, world, page unit and scale, device, then GDI+'s pixel offset.</summary>
    private Matrix3x2 Full
    {
        get
        {
            float page = UnitScale(_state.PageUnit) * _state.PageScale;
            Matrix3x2 m = _state.Container * _state.World * Matrix3x2.CreateScale(page) * _gdi.Device;

            // Without PixelOffsetModeHalf (2 HighQuality, 4 Half), GDI+ puts integer coordinates on pixel centers.
            if (_state.PixelOffset is not (2 or 4))
            {
                m *= Matrix3x2.CreateTranslation(0.5f, 0.5f);
            }

            return m;
        }
    }

    /// <summary>Gets a value indicating whether shapes are antialiased: SmoothingModeHighQuality (2), AntiAlias (4) and AntiAlias8x8 (5).</summary>
    private bool Smooth => _state.Smoothing is 2 or 4 or 5;

    private static Matrix3x2 Matrix(ReadOnlySpan<byte> d, int at) => new(F32(d, at), F32(d, at + 4), F32(d, at + 8), F32(d, at + 12), F32(d, at + 16), F32(d, at + 20));

    private static bool IsFinite(Matrix3x2 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M31) && float.IsFinite(m.M32);

    /// <summary>Applies a transform to the world transform: prepended (applied first) unless the append flag (0x2000) is set.</summary>
    private void Transform(Matrix3x2 m, ushort flags)
    {
        if (!IsFinite(m))
        {
            return;
        }

        _state = _state with { World = (flags & 0x2000) != 0 ? _state.World * m : m * _state.World };
    }

    // ---- Clip ----

    private void ApplyClip()
    {
        _canvas.RestoreToCount(_clipToken);
        _clipToken = _canvas.Save();
        if (_state.Clip is Path clip)
        {
            _canvas.ClipPath(clip, ClipOp.Intersect, antialias: Smooth);
        }
    }

    /// <summary>Combines a world-space shape with the clip (null shape: infinite).</summary>
    private void SetClip(Path? world, int mode)
    {
        Path? device = world?.Transform(Full);
        _state = _state with { Clip = PlusObjectReader.Combine(_state.Clip, device, mode) };
        if (mode == 0 && world is null)
        {
            _state = _state with { Clip = null };
        }

        ApplyClip();
    }
}
