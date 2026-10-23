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

    /// <summary>Whether a frame carries an alpha plane or alpha channel.</summary>
    public static bool HasAlpha(VideoFrame frame) => frame.Planes?.HasAlpha ?? frame.Pixels?.Layout.HasAlpha ?? false;

    public static void CopyPlane(YuvPlane<byte> from, YuvPlane<byte> to)
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
    }
}
