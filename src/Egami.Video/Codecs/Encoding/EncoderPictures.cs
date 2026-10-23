using Egami.Pixels;

namespace Egami.Video.Codecs.Encoding;

/// <summary>
/// Turns the frames an encoder is sent into 8-bit 4:2:0 pictures it owns, from a pool so a long encode allocates no pictures
/// after its lookahead has filled. 4:2:0 8-bit planes are copied as they are (their color is the track's); anything else is
/// converted to BT.601 limited range, VP8's own color.
/// </summary>
internal sealed class EncoderPictures : IDisposable
{
    /// <summary>VP8's color: BT.601, limited range, chroma between the luma samples.</summary>
    public static readonly YuvColor Vp8Color = new(YuvMatrix.Bt601, YuvRange.Limited, ChromaSiting.Center);

    private readonly int _width;
    private readonly int _height;
    private readonly YuvColor _color;
    private readonly EgamiContext _context;
    private readonly Stack<YuvRaster<byte>> _free = new();
    private readonly Stack<YuvRaster<byte>> _freeAlpha = new();
    private readonly Stack<YuvRaster> _freeFull = new();

    public EncoderPictures(int width, int height, YuvColor color, EgamiContext context)
    {
        _width = width;
        _height = height;
        _color = color;
        _context = context;
    }

    /// <summary>Returns a picture with or without an alpha plane, its samples undefined.</summary>
    public YuvRaster<byte> Rent(bool alpha)
    {
        Stack<YuvRaster<byte>> free = alpha ? _freeAlpha : _free;
        return free.TryPop(out YuvRaster<byte>? picture) ? picture : YuvRaster.Create(_width, _height, ChromaFormat.Yuv420, _color, alpha, _context);
    }

    /// <summary>Takes a picture back for reuse.</summary>
    public void Return(YuvRaster<byte> picture)
    {
        if (picture.Width == _width && picture.Height == _height && picture.ChromaFormat == ChromaFormat.Yuv420)
        {
            (picture.HasAlpha ? _freeAlpha : _free).Push(picture);
        }
        else
        {
            picture.Dispose();
        }
    }

    /// <summary>Copies or converts a frame into a picture of its own.</summary>
    /// <param name="frame">The frame, at the encoder's size.</param>
    /// <param name="alpha">Whether the picture keeps an alpha plane (opaque when the frame has none).</param>
    public YuvRaster<byte> Import(VideoFrame frame, bool alpha)
    {
        YuvRaster<byte> picture = Rent(alpha);
        if (frame.Planes is YuvRaster<byte> { ChromaFormat: ChromaFormat.Yuv420 } planes)
        {
            CopyPlane(planes.Y, picture.Y);
            CopyPlane(planes.U!, picture.U!);
            CopyPlane(planes.V!, picture.V!);
            if (alpha)
            {
                if (planes.A is { } a)
                {
                    CopyPlane(a, picture.A!);
                }
                else
                {
                    Fill(picture.A!, 255);
                }
            }

            return picture;
        }

        // Other layouts go through RGB once.
        Raster? converted = null;
        Raster raster = frame.Pixels ?? (converted = frame.Planes!.ToRaster());
        try
        {
            using YuvRaster<byte> yuv = YuvRaster.FromRaster(raster, ChromaFormat.Yuv420, _color, alpha);
            CopyPlane(yuv.Y, picture.Y);
            CopyPlane(yuv.U!, picture.U!);
            CopyPlane(yuv.V!, picture.V!);
            if (alpha)
            {
                CopyPlane(yuv.A!, picture.A!);
            }
        }
        finally
        {
            converted?.Dispose();
        }

        return picture;
    }

    /// <summary>Gets or sets the format <see cref="ImportFull"/> keeps frames in: a chroma format, a bit depth and a color.</summary>
    public (ChromaFormat Chroma, int BitDepth, YuvColor Color) FullFormat { get; set; } = (ChromaFormat.Yuv420, 8, Vp8Color);

    /// <summary>
    /// Copies or converts a frame into a picture of <see cref="FullFormat"/>: planes of that chroma format and depth (a 16-bit
    /// plane's samples count) as they are, anything else through RGB once.
    /// </summary>
    public YuvRaster ImportFull(VideoFrame frame)
    {
        (ChromaFormat chroma, int depth, YuvColor color) = FullFormat;
        YuvRaster picture = _freeFull.TryPop(out YuvRaster? free) ? free
            : depth == 8 ? YuvRaster.Create(_width, _height, chroma, color, false, _context)
            : YuvRaster.Create(_width, _height, chroma, depth, color, false, _context);
        if (frame.Planes is YuvRaster<byte> bytes && depth == 8 && bytes.ChromaFormat == chroma)
        {
            YuvRaster<byte> to = (YuvRaster<byte>)picture;
            CopyPlane(bytes.Y, to.Y);
            CopyPlane(bytes.U!, to.U!);
            CopyPlane(bytes.V!, to.V!);
            return picture;
        }

        if (frame.Planes is YuvRaster<ushort> words && depth > 8 && words.ChromaFormat == chroma && words.BitDepth == depth)
        {
            YuvRaster<ushort> to = (YuvRaster<ushort>)picture;
            CopyPlane(words.Y, to.Y);
            CopyPlane(words.U!, to.U!);
            CopyPlane(words.V!, to.V!);
            return picture;
        }

        Raster? converted = null;
        Raster raster = frame.Pixels ?? (converted = frame.Planes!.ToRaster());
        try
        {
            if (depth == 8)
            {
                using YuvRaster<byte> yuv = YuvRaster.FromRaster(raster, chroma, color);
                YuvRaster<byte> to = (YuvRaster<byte>)picture;
                CopyPlane(yuv.Y, to.Y);
                CopyPlane(yuv.U!, to.U!);
                CopyPlane(yuv.V!, to.V!);
            }
            else
            {
                using YuvRaster<ushort> yuv = YuvRaster.FromRaster(raster, chroma, depth, color);
                YuvRaster<ushort> to = (YuvRaster<ushort>)picture;
                CopyPlane(yuv.Y, to.Y);
                CopyPlane(yuv.U!, to.U!);
                CopyPlane(yuv.V!, to.V!);
            }
        }
        finally
        {
            converted?.Dispose();
        }

        return picture;
    }

    /// <summary>Takes a picture from <see cref="ImportFull"/> back for reuse.</summary>
    public void ReturnFull(YuvRaster picture) => _freeFull.Push(picture);

    /// <summary>Whether a frame carries an alpha plane or alpha channel.</summary>
    public static bool HasAlpha(VideoFrame frame) => frame.Planes?.HasAlpha ?? frame.Pixels?.Layout.HasAlpha ?? false;

    public static void CopyPlane<T>(YuvPlane<T> from, YuvPlane<T> to)
        where T : unmanaged
    {
        int width = Math.Min(from.Width, to.Width);
        for (int y = 0; y < to.Height; y++)
        {
            from.Row(Math.Min(y, from.Height - 1))[..width].CopyTo(to.Row(y));
        }
    }

    public static void Fill(YuvPlane<byte> plane, byte value)
    {
        for (int y = 0; y < plane.Height; y++)
        {
            plane.Row(y).Fill(value);
        }
    }

    public void Dispose()
    {
        while (_free.TryPop(out YuvRaster<byte>? p))
        {
            p.Dispose();
        }

        while (_freeAlpha.TryPop(out YuvRaster<byte>? p))
        {
            p.Dispose();
        }

        while (_freeFull.TryPop(out YuvRaster? p))
        {
            p.Dispose();
        }
    }
}
