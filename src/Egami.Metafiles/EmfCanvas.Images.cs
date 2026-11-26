using System.Numerics;

using Egami.Drawing;
using Egami.GdiPlus;
using Egami.Metadata;
using Egami.Metafiles.Writing;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Metafiles;

public sealed partial class EmfCanvas
{
    /// <summary>PixelFormat32bppARGB and PixelFormat24bppRGB, the formats GDI+ gives PNG and JPEG images.</summary>
    private const uint Argb32 = 0x0026200A, Rgb24 = 0x00021808;

    /// <summary>The EmfPlusImage of each encoded image drawn, its file's bytes as they are, or null when it is decoded instead.</summary>
    private readonly Dictionary<EncodedImage, byte[]?> _passthrough = [];

    /// <inheritdoc/>
    protected override void OnDrawImage(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
    {
        if (Recording())
        {
            _recorder!.DrawImage(image, source, destination, sampling, paint);
            _foldable = false;
        }
        else if (_quiet == 0)
        {
            DrawBitmap(image, null, source, destination, sampling, paint);
        }
    }

    /// <inheritdoc/>
    protected override void OnDrawEncodedImage(EncodedImage image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
    {
        if (Recording())
        {
            _recorder!.DrawImage(image, source, destination, sampling, paint);
            _foldable = false;
        }
        else if (_quiet == 0)
        {
            DrawBitmap(null, image, source, destination, sampling, paint);
        }
    }

    /// <summary>
    /// Draws an image whose EMF+ part takes another image and source rectangle than its GDI part. GDI+ samples an image half a
    /// pixel apart from Egami (unless PixelOffsetModeHalf is set) and fades its edges, while GDI's AlphaBlend takes whole pixels.
    /// Egami.SystemDrawing records <c>Graphics.DrawImage</c> this way: a bordered copy with a moved source rectangle for GDI+, and
    /// the image as it is for GDI, so both players draw what Windows' own recordings draw. Use it when a file must match GDI+'s
    /// own recordings; <see cref="Canvas.DrawImage(Raster, RectF, RectF, SamplingOptions?, Paint?)"/> is right otherwise.
    /// Drawn on another canvas (a fallback image, a layer), the call is the plain image.
    /// </summary>
    /// <param name="image">The image the GDI part draws, and the fallbacks.</param>
    /// <param name="source">The rectangle of <paramref name="image"/> to draw, in its pixels.</param>
    /// <param name="destination">Where the image goes, in local coordinates.</param>
    /// <param name="emfPlusImage">The image the EMF+ part draws.</param>
    /// <param name="emfPlusSource">
    /// The rectangle of <paramref name="emfPlusImage"/> the EMF+ part draws into <paramref name="destination"/>. Where
    /// <paramref name="source"/> reaches past <paramref name="image"/>, it is cut in the same proportion.
    /// </param>
    /// <param name="sampling">How the image is sampled; Egami's default sampling when <see langword="null"/>.</param>
    /// <param name="paint">The blend mode, opacity and antialiasing; its color is not used.</param>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> or <paramref name="emfPlusImage"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A rectangle is not finite.</exception>
    /// <exception cref="ObjectDisposedException">The canvas is disposed.</exception>
    /// <example>
    /// <code source="../../tests/Egami.DocSamples/Metafiles.cs" region="emf-plus-image" language="csharp"/>
    /// </example>
    public void DrawImage(Raster image, RectF source, RectF destination, Raster emfPlusImage, RectF emfPlusSource, SamplingOptions? sampling = null, Paint? paint = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(emfPlusImage);
        ObjectDisposedException.ThrowIf(_finished, this);
        Finite(source, nameof(source));
        Finite(destination, nameof(destination));
        Finite(emfPlusSource, nameof(emfPlusSource));

        RectF clipped = source.Intersect(new RectF(0, 0, image.Width, image.Height));
        if (clipped.IsEmpty || destination.IsEmpty || emfPlusSource.IsEmpty)
        {
            return;
        }

        // Clipping the source shrinks the destination and the EMF+ source in proportion.
        if (clipped != source)
        {
            float sx = destination.Width / source.Width, sy = destination.Height / source.Height;
            float px = emfPlusSource.Width / source.Width, py = emfPlusSource.Height / source.Height;
            destination = new RectF(destination.X + ((clipped.X - source.X) * sx), destination.Y + ((clipped.Y - source.Y) * sy), clipped.Width * sx, clipped.Height * sy);
            emfPlusSource = new RectF(emfPlusSource.X + ((clipped.X - source.X) * px), emfPlusSource.Y + ((clipped.Y - source.Y) * py), clipped.Width * px, clipped.Height * py);
        }

        SamplingOptions how = sampling ?? new SamplingOptions();
        if (Recording())
        {
            _recorder!.DrawImage(image, clipped, destination, how, paint);
            _foldable = false;
        }
        else if (_quiet == 0)
        {
            DrawBitmap(image, null, clipped, destination, how, paint, (emfPlusImage, emfPlusSource));
        }
    }

    private static void Finite(RectF rect, string name)
    {
        if (!rect.IsFinite)
        {
            throw new ArgumentException("The rectangle must be finite.", name);
        }
    }

    /// <inheritdoc/>
    protected override void OnDrawPicture(Picture picture, Matrix3x2 matrix, Paint? paint, bool cacheAsImage)
        => base.OnDrawPicture(picture, matrix, paint, cacheAsImage: false); // played inline, so it stays vectors at any scale

    /// <summary>
    /// Writes an image (a raster, or an encoded file) drawn from a source rectangle into a local one: an EMF+ image object with
    /// DrawImagePoints, the source rectangle and the interpolation mode, where a PNG or JPEG keeps its bytes as they are; and in
    /// the GDI part, an AlphaBlend of a premultiplied 32-bit DIB of the source's pixels. A paint's opacity is baked into the
    /// pixels; other blend modes and blurs fall back to an image of the call, and so does the GDI part of a turned or mirrored one.
    /// </summary>
    private void DrawBitmap(Raster? raster, EncodedImage? encoded, RectF source, RectF destination, SamplingOptions sampling, Paint? paint, (Raster Image, RectF Source)? plus = null)
    {
        if (paint?.BlendMode == BlendMode.Destination)
        {
            return;
        }

        void Draw(Canvas c)
        {
            if (encoded is not null)
            {
                c.DrawImage(encoded, source, destination, sampling, paint);
            }
            else
            {
                c.DrawImage(raster!, source, destination, sampling, paint);
            }
        }

        Remember(Draw);
        if (paint is not null && Unsupported(paint, null) is string what)
        {
            Fallback(destination, 1, Draw, $"an image with {what}", backdrop: paint.BlendMode != BlendMode.SourceOver);
            return;
        }

        Matrix3x2 m = Matrix;
        float opacity = Math.Clamp((paint?.Opacity ?? 1) * _fold, 0, 1);
        if (!Invertible(m) || opacity <= 0)
        {
            return;
        }

        Touch(destination, 1);
        if (_plus)
        {
            byte[] image = plus is ({ } plusImage, _) ? BitmapObject(plusImage, opacity) : (opacity >= 1 && encoded is not null ? Passthrough(encoded) : null) ?? BitmapObject(raster ?? encoded!.Decode(), opacity);
            RectF plusSource = plus?.Source ?? source;
            int id = _w.PlusObject(5, image);

            // Image attributes that mirror at the image's edges, so GDI+ does not fade them toward transparent black.
            int attributes = _w.PlusObject(8, new RecordBuffer(24).U32(GdiPlusData.Version).U32(0).I32(GdiPlusBrushes.WrapTileFlipXY).U32(0).I32(0).U32(0).ToArray());
            SetPlusWorld(m);
            SetSmooth(paint?.Antialias ?? true);
            SetInterpolation(Interpolation(sampling.Filter));
            RecordBuffer b = _w.Data().U32((uint)attributes).I32(2); // UnitPixel
            b.F32(plusSource.X).F32(plusSource.Y).F32(plusSource.Width).F32(plusSource.Height).I32(3);
            b.F32(destination.Left).F32(destination.Top).F32(destination.Right).F32(destination.Top).F32(destination.Left).F32(destination.Bottom);
            _w.Plus(0x401B, (ushort)id, b.Span); // DrawImagePoints: the source's top-left, top-right and bottom-left corners
        }

        if (_gdi && (m.M12 != 0 || m.M21 != 0 || m.M11 < 0 || m.M22 < 0))
        {
            // GDI blends bitmaps upright and unmirrored: a turned or mirrored one is an image of the call in device pixels.
            Fallback(destination, 1, Draw, "a turned or mirrored image", plus: false);
        }
        else if (_gdi)
        {
            Raster pixels = raster ?? encoded!.Decode();
            using Raster<Rgba8> rgba = pixels.As<Rgba8>();

            // AlphaBlend takes whole source pixels: a source between pixels is widened to them and clipped to its destination.
            int x0 = (int)MathF.Floor(source.Left), y0 = (int)MathF.Floor(source.Top), x1 = (int)MathF.Ceiling(source.Right), y1 = (int)MathF.Ceiling(source.Bottom);
            float sx = destination.Width / source.Width, sy = destination.Height / source.Height;
            RectF box = RectF.FromEdges(destination.X + ((x0 - source.X) * sx), destination.Y + ((y0 - source.Y) * sy), destination.X + ((x1 - source.X) * sx), destination.Y + ((y1 - source.Y) * sy));
            bool whole = x0 == source.Left && y0 == source.Top && x1 == source.Right && y1 == source.Bottom;
            if (!whole)
            {
                GdiSave();
            }

            try
            {
                if (whole || WriteGdiPath(Path.Rect(destination), m))
                {
                    if (!whole)
                    {
                        _w.Gdi(67, _w.Data().I32(1).Span); // EMR_SELECTCLIPPATH RGN_AND
                    }

                    WriteAlphaBlend(rgba, RectF.FromEdges(x0, y0, x1, y1), box, m, opacity);
                }
            }
            finally
            {
                if (!whole)
                {
                    GdiRestore();
                }
            }
        }
    }

    /// <summary>
    /// The EmfPlusImage of an encoded PNG, or of a baseline or progressive 8-bit JPEG, stored upright: a compressed bitmap holding
    /// the file's bytes unchanged, as GDI+ writes them. Null for any other file, which is decoded and written as PNG.
    /// </summary>
    private byte[]? Passthrough(EncodedImage image)
    {
        if (_passthrough.TryGetValue(image, out byte[]? cached))
        {
            return cached;
        }

        ReadOnlySpan<byte> data = image.Data.Span;
        bool png = data.Length > 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G';
        byte[]? result = null;
        if ((png || PlainJpeg(data)) && image.Info.Orientation is ImageOrientation.Normal or ImageOrientation.Unknown && image.Width > 0 && image.Height > 0)
        {
            RecordBuffer o = new(data.Length + 32);
            o.U32(GdiPlusData.Version).U32(1); // EmfPlusImage: ImageDataTypeBitmap
            o.I32(image.Width).I32(image.Height).I32(0).U32(png ? Argb32 : Rgb24).U32(1); // EmfPlusBitmap: compressed
            o.Bytes(data).Align();
            result = o.ToArray();
        }

        _passthrough[image] = result;
        return result;
    }

    /// <summary>Whether a JPEG's frame is baseline or progressive Huffman coding with 8-bit samples, which every GDI+ decodes.</summary>
    private static bool PlainJpeg(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8)
        {
            return false;
        }

        int at = 2;
        while (at + 4 <= b.Length)
        {
            if (b[at] != 0xFF)
            {
                return false;
            }

            byte marker = b[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }

            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                return at + 4 < b.Length && b[at + 4] == 8;
            }

            if (marker is (>= 0xC3 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC) or 0xDA or 0xD9)
            {
                return false; // lossless, hierarchical or arithmetic coding, or no frame before the scan
            }

            at += 2 + ((b[at + 2] << 8) | b[at + 3]);
        }

        return false;
    }

    /// <summary>
    /// EMR_ALPHABLEND of a premultiplied 32-bit DIB: the pixels of a whole-pixel source rectangle of an image, their alpha times an
    /// opacity, into a local rectangle under a transform.
    /// </summary>
    private void WriteAlphaBlend(Raster<Rgba8> rgba, RectF source, RectF destination, Matrix3x2 m, float opacity)
    {
        int x0 = (int)source.X, y0 = (int)source.Y, w = (int)source.Width, h = (int)source.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        SetGdiWorld(m);
        RecordBuffer b = new((w * h * 4) + 160);
        b.Bytes(Bounds16(MapRect(destination, m)));
        int left = Fix(destination.Left), top = Fix(destination.Top);
        b.I32(left).I32(top).I32(Fix(destination.Right) - left).I32(Fix(destination.Bottom) - top);
        b.U32(0x01FF0000); // BLENDFUNCTION: AC_SRC_OVER, constant alpha 255, AC_SRC_ALPHA
        b.I32(0).I32(0).Matrix(Matrix3x2.Identity).U32(0).U32(0); // source corner, source transform, background, DIB_RGB_COLORS
        const int Header = 108, Info = 40;
        b.U32(Header).U32(Info).U32(Header + Info).U32((uint)(w * h * 4)).I32(w).I32(h);

        // BITMAPINFOHEADER of a bottom-up 32-bit BI_RGB DIB, then its premultiplied BGRA rows.
        b.U32(Info).I32(w).I32(h).U16(1).U16(32).U32(0).U32((uint)(w * h * 4)).I32(0).I32(0).U32(0).U32(0);
        for (int y = y0 + h - 1; y >= y0; y--)
        {
            foreach (Rgba8 p in rgba.Row(y).Slice(x0, w))
            {
                int a = opacity >= 1 ? p.A : (int)MathF.Round(p.A * opacity);
                b.Bytes([(byte)(((p.B * a) + 127) / 255), (byte)(((p.G * a) + 127) / 255), (byte)(((p.R * a) + 127) / 255), (byte)a]);
            }
        }

        _w.Gdi(114, b.Span); // EMR_ALPHABLEND
    }
}
