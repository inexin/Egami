using System.Buffers.Binary;

using Egami.Drawing;
using Egami.Metafiles;
using Egami.SystemDrawing.Imaging;

namespace Egami.SystemDrawing.Internal;

/// <summary>
/// A metafile being recorded: what its constructor asked for, and once <c>Graphics.FromImage</c> starts it, the
/// <see cref="EmfCanvas"/> a Graphics draws on. The reference device is a 96 DPI screen, whatever handle the constructor got.
/// Disposing the Graphics finishes the file: its header's bounds and frame are set as GDI+ sets them (measured with System.Drawing
/// on Windows), and it is written to the file or stream.
/// </summary>
internal sealed class MetafileRecording : IDisposable
{
    /// <summary>The reference device's resolution.</summary>
    public const float Dpi = 96;

    /// <summary>The size of the canvas a metafile without a frame records on: drawing beyond it is still written as vectors.</summary>
    private const float Unframed = 16384;

    private readonly string? _path;
    private readonly Stream? _stream;
    private readonly (int L, int T, int R, int B)? _frame;
    private readonly EmfWriteOptions _options;
    private MemoryStream? _buffer;
    private EmfCanvas? _canvas;
    private RectF _drawn = RectF.Empty;

    private MetafileRecording(string? path, Stream? stream, (int L, int T, int R, int B)? frame, EmfWriteOptions options)
    {
        _path = path;
        _stream = stream;
        _frame = frame;
        _options = options;
    }

    /// <summary>Gets a value indicating whether a Graphics is recording, or has recorded.</summary>
    public bool Started => _canvas is not null;

    /// <summary>Gets the canvas a Graphics records on.</summary>
    public EmfCanvas Canvas => _canvas ?? throw new InvalidOperationException("The recording has not started.");

    /// <summary>Gets the size of the canvas, in device pixels.</summary>
    public SizeF Size => _canvas is null ? SizeF.Empty : new SizeF(_canvas.Width, _canvas.Height);

    /// <summary>The bytes of a metafile with nothing in it, which a recording metafile holds until it is recorded.</summary>
    public static byte[] Empty { get; } = MakeEmpty();

    /// <summary>Checks a recording constructor's arguments as GDI+ checks them, and creates the file it records to.</summary>
    /// <param name="path">The file to write, or null.</param>
    /// <param name="stream">The stream to write, or null.</param>
    /// <param name="target">Whether the constructor took a file (1), a stream (2) or neither (0).</param>
    /// <param name="frame">The frame, or null for the bounds of what is drawn.</param>
    /// <param name="unit">The frame's unit.</param>
    /// <param name="type">The kind of EMF.</param>
    /// <param name="description">The description, or null.</param>
    /// <returns>The recording.</returns>
    public static MetafileRecording Create(string? path, Stream? stream, int target, RectangleF? frame, MetafileFrameUnit unit, EmfType type, string? description)
    {
        if (target == 1)
        {
            ArgumentNullException.ThrowIfNull(path, nameof(path));
        }
        else if (target == 2)
        {
            ArgumentNullException.ThrowIfNull(stream, nameof(stream));
        }

        MetafileKind kind = type switch
        {
            EmfType.EmfOnly => MetafileKind.Emf,
            EmfType.EmfPlusOnly => MetafileKind.EmfPlusOnly,
            EmfType.EmfPlusDual => MetafileKind.EmfPlusDual,
            _ => throw GdiErrors.InvalidParameter(),
        };
        if (unit is < MetafileFrameUnit.Pixel or > MetafileFrameUnit.GdiCompatible)
        {
            throw GdiErrors.InvalidParameter();
        }

        (int, int, int, int)? hundredths = frame is RectangleF f ? Frame(f, unit) : null;
        if (path is not null)
        {
            // GDI+ creates the file at once, empty until the recording ends.
            File.Create(path).Dispose();
        }

        EmfWriteOptions options = new() { Kind = kind, Dpi = Dpi, Text = EmfText.Glyphs, Description = description };
        return new MetafileRecording(path, stream, hundredths, options);
    }

    /// <summary>Starts recording: makes the canvas a Graphics draws on.</summary>
    /// <returns>The canvas.</returns>
    public EmfCanvas Start()
    {
        float width = Unframed, height = Unframed;
        if (_frame is (_, _, int r, int b))
        {
            // The canvas reaches the frame's last pixel; drawing past it is still written, as GDI+ writes it.
            width = MathF.Max(1, MathF.Floor(r * Dpi / 2540) + 1);
            height = MathF.Max(1, MathF.Floor(b * Dpi / 2540) + 1);
        }

        _buffer = new MemoryStream();
        _canvas = new EmfCanvas(_buffer, width, height, _options);
        return _canvas;
    }

    /// <summary>Runs one drawing call on the canvas, clipped, and adds the area it can touch to the bounds of what was drawn.</summary>
    /// <param name="area">The device area the call can touch, or null when it has none of its own (a Clear).</param>
    /// <param name="clip">The clip in device pixels, or null.</param>
    /// <param name="draw">The call.</param>
    public void Draw(RectF? area, Path? clip, Action<Canvas> draw)
    {
        EmfCanvas canvas = Canvas;
        int count = canvas.Save();
        try
        {
            if (clip is not null)
            {
                canvas.ClipPath(clip, ClipOp.Intersect, antialias: false);
            }

            draw(canvas);
        }
        finally
        {
            canvas.RestoreToCount(count);
        }

        if (area is RectF a && a.Width > 0 && a.Height > 0 && a.IsFinite)
        {
            _drawn = _drawn.IsEmpty ? a : RectF.FromEdges(MathF.Min(_drawn.Left, a.Left), MathF.Min(_drawn.Top, a.Top), MathF.Max(_drawn.Right, a.Right), MathF.Max(_drawn.Bottom, a.Bottom));
        }
    }

    /// <summary>Finishes the file, writes it to the file or stream the metafile was made with, and returns its bytes.</summary>
    /// <returns>The file.</returns>
    public byte[] Finish()
    {
        Canvas.Dispose();
        byte[] data = _buffer!.ToArray();
        Dispose();

        // GDI+ writes the bounds of what was drawn in device pixels, inclusive: the pixels whose centers the drawing covers. Without
        // a frame it makes the frame those bounds.
        (int L, int T, int R, int B) bounds = _drawn.IsEmpty
            ? (0, 0, -1, -1)
            : (Center(_drawn.Left), Center(_drawn.Top), Center(_drawn.Right) - 1, Center(_drawn.Bottom) - 1);
        (int L, int T, int R, int B) frame = _frame ?? (Hundredths(bounds.L), Hundredths(bounds.T), Hundredths(bounds.R), Hundredths(bounds.B));
        Span<byte> header = data.AsSpan(8, 32);
        BinaryPrimitives.WriteInt32LittleEndian(header, bounds.L);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], bounds.T);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], bounds.R);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], bounds.B);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], frame.L);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], frame.T);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], frame.R);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], frame.B);
        if (_path is not null)
        {
            File.WriteAllBytes(_path, data);
        }

        if (_stream is not null)
        {
            _stream.Write(data);
            _stream.Flush();
        }

        return data;
    }

    /// <summary>Drops the canvas and its buffer.</summary>
    public void Dispose()
    {
        _canvas?.Dispose();
        _buffer?.Dispose();
    }

    /// <summary>
    /// The frame in 0.01 mm, as GDI+ sets it: a frame in GDI's own unit as it is; in other units from the frame's left and top
    /// to its right and bottom less one device pixel, so the frame is inclusive of its last pixel. GDI+ refuses an empty frame in
    /// those units, and a negative one in any.
    /// </summary>
    private static (int L, int T, int R, int B) Frame(RectangleF f, MetafileFrameUnit unit)
    {
        if (f.Width < 0 || f.Height < 0 || !float.IsFinite(f.X) || !float.IsFinite(f.Y) || !float.IsFinite(f.Width) || !float.IsFinite(f.Height))
        {
            throw GdiErrors.Generic();
        }

        if (unit == MetafileFrameUnit.GdiCompatible)
        {
            return (Round(f.X), Round(f.Y), Round(f.Right), Round(f.Bottom));
        }

        if (f.Width == 0 || f.Height == 0)
        {
            throw GdiErrors.Generic();
        }

        float scale = unit switch
        {
            MetafileFrameUnit.Pixel => 2540 / Dpi,
            MetafileFrameUnit.Point => 2540f / 72,
            MetafileFrameUnit.Inch => 2540,
            MetafileFrameUnit.Document => 2540f / 300,
            _ => 100,
        };
        float pixel = 2540 / Dpi;
        return (Round(f.X * scale), Round(f.Y * scale), Round((f.Right * scale) - pixel), Round((f.Bottom * scale) - pixel));
    }

    /// <summary>The first pixel whose center is at or right of an edge, pixel centers half a pixel in from whole coordinates.</summary>
    private static int Center(float edge) => (int)MathF.Ceiling(Math.Clamp(edge, -1e9f, 1e9f) - 0.5f);

    private static int Hundredths(int pixels) => Round(pixels * 2540 / Dpi);

    private static int Round(float v) => (int)MathF.Round(Math.Clamp(v, int.MinValue / 2f, int.MaxValue / 2f), MidpointRounding.AwayFromZero);

    private static byte[] MakeEmpty()
    {
        using MemoryStream stream = new();
        using (new EmfCanvas(stream, 1, 1, new EmfWriteOptions { Dpi = Dpi }))
        {
        }

        return stream.ToArray();
    }
}
